using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AstralSplit
{
    // The privileged worker owns the controller credential and only performs GETs.
    // sing-box binds port zero itself, eliminating the probe/close/rebind race.
    internal sealed class RouteObserver : IDisposable
    {
        readonly MemoryMappedViewAccessor view;
        readonly string token;
        readonly Func<RuleStamp> rules;
        readonly ConnectionSummary summaries = new ConnectionSummary();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly object publishLock = new object();
        bool retired;
        HttpWebRequest pending;
        Thread sampler;
        int port;

        RouteObserver(MemoryMappedFile file, Func<RuleStamp> current)
        {
            rules = current;
            var secret = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            token = Convert.ToBase64String(secret);
            view = file.CreateViewAccessor(0, RouteDiagnostics.Capacity, MemoryMappedFileAccess.ReadWrite);
        }
        internal object EngineOptions { get {
            return new { clash_api = new { external_controller = "127.0.0.1:0", secret = token,
                access_control_allow_origin = new[] { "https://astralsplit.invalid" }, access_control_allow_private_network = false } };
        } }
        internal static RouteObserver Attach(MemoryMappedFile file, Func<RuleStamp> current)
        {
            if (file == null) return null;
            try {
                return new RouteObserver(file, current);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException ||
                ex is System.Security.SecurityException || ex is ArgumentException || ex is InvalidOperationException) {
                return null;
            }
        }
        internal void Start(Process process)
        {
            if (sampler != null) return;
            int pid = process.Id;
            sampler = new Thread(() => Sample(pid)) { IsBackground = true, Name = "AstralSplit connection observations" };
            sampler.Start();
        }
        void Sample(int pid)
        {
            try {
                do {
                    RouteObservationSnapshot snapshot;
                    var stamp = rules();
                    try {
                        var listeners = LoopbackListeners(pid);
                        if (!listeners.Contains(port)) port = 0;
                        if (port == 0) {
                            foreach (int candidate in listeners) {
                                if (stop.WaitOne(0)) return;
                                if (RequiresCredential(candidate)) { port = candidate; break; }
                            }
                        }
                        if (port == 0) throw new IOException("Local connection observer unavailable.");
                        string payload = Fetch(port, "connections", true);
                        snapshot = summaries.Update(payload, rules(), DateTime.UtcNow.Ticks);
                    } catch (Exception ex) when (ex is IOException || ex is WebException || ex is InvalidOperationException ||
                        ex is ArgumentException || ex is FormatException || ex is OverflowException ||
                        ex is UnauthorizedAccessException || ex is System.Security.SecurityException) {
                        if (ex is WebException web) web.Response?.Dispose();
                        snapshot = new RouteObservationSnapshot { SampleUtcTicks = DateTime.UtcNow.Ticks,
                            RuleGeneration = stamp.Generation, RulesChangedUtcTicks = stamp.ChangedUtcTicks };
                    }
                    lock (publishLock) {
                        if (retired) return;
                        RouteDiagnostics.Publish(view, snapshot);
                    }
                } while (!stop.WaitOne(700));
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
                ex is ArgumentException || ex is System.Security.SecurityException) {
                // Failure of the optional observer must not stop the routing session.
            } finally {
                ClosePublisher();
                stop.Dispose();
            }
        }
        const int MaxBodyBytes = 2 * 1024 * 1024;
        static string ReadBounded(Stream body, long declared)
        {
            if (declared > MaxBodyBytes) throw new InvalidDataException("Large connection snapshot.");
            using (var bytes = new MemoryStream()) {
                var buffer = new byte[8192];
                int n;
                while ((n = body.Read(buffer, 0, buffer.Length)) > 0) {
                    if (bytes.Length + n > MaxBodyBytes) throw new InvalidDataException("Large connection snapshot.");
                    bytes.Write(buffer, 0, n);
                }
                return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            }
        }
        static HttpWebRequest Request(int port, string path)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/" + path);
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Timeout = request.ReadWriteTimeout = 650;
            return request;
        }
        string Fetch(int endpoint, string path, bool authenticated)
        {
            var request = Request(endpoint, path);
            if (authenticated) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            Interlocked.Exchange(ref pending, request);
            try {
                if (stop.WaitOne(0)) throw new IOException("Observer closed.");
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var body = response.GetResponseStream())
                    return ReadBounded(body, response.ContentLength);
            } finally { Interlocked.CompareExchange(ref pending, null, request); }
        }
        bool RequiresCredential(int candidate)
        {
            // Never pass a secret while identifying the controller among engine-owned
            // listeners (e.g. a loopback mixed test inbound owned by the same process).
            try { Fetch(candidate, "version", false); return false; }
            catch (WebException ex) {
                using (var response = ex.Response as HttpWebResponse)
                    return response?.StatusCode == HttpStatusCode.Unauthorized;
            }
        }
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable([Out] byte[] buffer, ref int size, bool sorted, int family, int tableClass, uint reserved);
        byte[] table = new byte[8192];
        List<int> LoopbackListeners(int pid)
        {
            int size = table.Length;
            const int TcpTableOwnerPidListener = 3;
            uint result = GetExtendedTcpTable(table, ref size, false, 2, TcpTableOwnerPidListener, 0);
            if (result == 122 && size >= 4 && size <= 8 * 1024 * 1024) {
                table = new byte[size];
                result = GetExtendedTcpTable(table, ref size, false, 2, TcpTableOwnerPidListener, 0);
            }
            var ports = new List<int>();
            if (result != 0 || size < 4) return ports;
            int rows = Math.Min(BitConverter.ToInt32(table, 0), (Math.Min(table.Length, size) - 4) / 24);
            for (int i = 0, k = 4; i < rows; i++, k += 24) {
                if (BitConverter.ToInt32(table, k + 20) == pid && BitConverter.ToUInt32(table, k + 4) == 0x0100007F)
                    ports.Add(table[k + 8] * 256 + table[k + 9]);
            }
            return ports;
        }
        void ClosePublisher()
        {
            lock (publishLock) {
                if (retired) return;
                retired = true;
                // Zero length invalidates the old snapshot as soon as the engine retires.
                // The owner keeps the mapping across reconnects so that an overlapping
                // frontend reader cannot prevent the next engine from publishing.
                try { view.Write(0, view.ReadInt32(0) | 1); Thread.MemoryBarrier(); view.Write(4, 0); }
                finally { view.Dispose(); }
            }
        }
        public void Dispose()
        {
            // Detach even if a Windows networking call is delayed. A reconnect can
            // immediately create a new publication under this same session ID.
            ClosePublisher();
            if (sampler == null) { stop.Dispose(); return; }
            try { stop.Set(); } catch (ObjectDisposedException) { }
            Volatile.Read(ref pending)?.Abort();
            sampler.Join(900);
        }
    }
}
