using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AstralSplit
{
    // The desktop UI owns this one connection. The elevated end only dispatches the
    // two RouterSession operations, always on behalf of the process that opened it.
    internal sealed class SessionAuthorization : IDisposable
    {
        const byte Ready = 1, Route = 2, Restore = 3, Finished = 4;
        const string Disconnected = "本次授权已结束。请从托盘退出星云分流，重新打开后再试。";
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

        readonly NamedPipeServerStream pipe;
        readonly Process host;
        Task activeSession;
        internal static SessionAuthorization Current { get; private set; }

        SessionAuthorization(NamedPipeServerStream server, Process process) { pipe = server; host = process; }
        static string PipeName(Guid key) { return "AstralSplit.Authorization." + key.ToString("N"); }

        internal static SessionAuthorization Authorize()
        {
            var key = Guid.NewGuid();
            var access = new PipeSecurity();
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            using (var identity = WindowsIdentity.GetCurrent())
                access.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            var server = new NamedPipeServerStream(PipeName(key), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 256, 256, access);
            Process helper = null;
            try
            {
                var connected = server.WaitForConnectionAsync();
                using (var self = Process.GetCurrentProcess())
                {
                    string arguments = String.Format(CultureInfo.InvariantCulture, "--authorize {0} {1} {2:N}",
                        self.Id, self.StartTime.ToUniversalTime().Ticks, key);
                    helper = Process.Start(new ProcessStartInfo(typeof(App).Assembly.Location, arguments) {
                        Verb = "runas", UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory
                    });
                }
                if (helper == null) throw new IOException("Windows 没有启动授权助手。");
                var clock = Stopwatch.StartNew();
                while (!connected.IsCompleted && !helper.HasExited && clock.ElapsedMilliseconds < 10000)
                    ((IAsyncResult)connected).AsyncWaitHandle.WaitOne(50);
                if (!connected.IsCompleted) throw new IOException("无法连接到授权助手。");
                connected.GetAwaiter().GetResult();
                uint client;
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out client) || client != helper.Id)
                    throw new IOException("授权助手的身份无法验证。");
                var hello = new byte[1];
                var read = server.ReadAsync(hello, 0, 1);
                if (!((IAsyncResult)read).AsyncWaitHandle.WaitOne(5000) || read.GetAwaiter().GetResult() != 1 || hello[0] != Ready)
                    throw new IOException("授权助手未能确认当前窗口。");
                return Current = new SessionAuthorization(server, helper);
            }
            catch { server.Dispose(); if (helper != null) helper.Dispose(); throw; }
        }

        internal Task StartSession(string id, bool recoverOnly)
        {
            Guid key = Guid.ParseExact(id, "N");
            lock (pipe)
            {
                if (activeSession != null && !activeSession.IsCompleted)
                    throw new InvalidOperationException("上一个分流会话仍在结束。");
                try
                {
                    if (host.HasExited || !pipe.IsConnected) throw new IOException(Disconnected);
                    var command = new byte[17];
                    command[0] = recoverOnly ? Restore : Route;
                    key.ToByteArray().CopyTo(command, 1);
                    pipe.Write(command, 0, command.Length);
                    return activeSession = AwaitCompletion();
                }
                catch (IOException ex) { throw new InvalidOperationException(Disconnected, ex); }
            }
        }

        async Task AwaitCompletion()
        {
            var reply = new byte[1];
            try
            {
                if (await pipe.ReadAsync(reply, 0, 1).ConfigureAwait(false) == 1 && reply[0] == Finished) return;
            }
            catch (IOException ex) { throw new InvalidOperationException(Disconnected, ex); }
            throw new InvalidOperationException(Disconnected);
        }

        internal static void RunHost(string[] args)
        {
            int pid;
            long ticks;
            Guid key;
            if (args.Length != 4 || !Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) ||
                !Int64.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                !Guid.TryParseExact(args[3], "N", out key) || pid <= 0 || ticks <= 0) return;
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
                using (var parent = Process.GetProcessById(pid))
                using (var self = Process.GetCurrentProcess())
                using (var client = new NamedPipeClientStream(".", PipeName(key), PipeDirection.InOut,
                    PipeOptions.None, TokenImpersonationLevel.Identification))
                {
                    // Holding the original process handle prevents PID reuse for the lifetime of this channel.
                    if (parent.Handle == IntPtr.Zero || parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != ticks ||
                        parent.SessionId != self.SessionId ||
                        !String.Equals(parent.MainModule.FileName, self.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) return;
                    client.Connect(5000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out server) || server != pid) return;
                    client.WriteByte(Ready);
                    var runId = new byte[16];
                    while (!parent.HasExited)
                    {
                        int operation = client.ReadByte();
                        if (operation != Route && operation != Restore) return;
                        int offset = 0;
                        while (offset < runId.Length)
                        {
                            int got = client.Read(runId, offset, runId.Length - offset);
                            if (got == 0) return;
                            offset += got;
                        }
                        if (parent.HasExited) return;
                        RouterSession.Run(new Guid(runId).ToString("N"), pid, ticks, operation == Restore);
                        client.WriteByte(Finished);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                ex is InvalidOperationException || ex is ArgumentException || ex is TimeoutException) { }
        }

        public void Dispose() { Current = null; pipe.Dispose(); host.Dispose(); }
    }
}
