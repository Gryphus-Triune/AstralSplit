using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AstralSplit
{
    internal sealed class RuleStamp
    {
        internal readonly int Generation;
        internal readonly long ChangedUtcTicks;
        internal RuleStamp(int generation, long changed) { Generation = generation; ChangedUtcTicks = changed; }
    }

    internal sealed class RouteObservation
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Protocol { get; set; }
        public string Outlet { get; set; }
        public string Rule { get; set; }
        public long StartedUtcTicks { get; set; }
        public long LastSeenUtcTicks { get; set; }
        public long UploadBytes { get; set; }
        public long DownloadBytes { get; set; }
        public bool Active { get; set; }
        public bool OlderThanRules { get; set; }
    }

    internal sealed class RouteObservationSnapshot
    {
        public bool Available { get; set; }
        public bool Truncated { get; set; }
        public long SampleUtcTicks { get; set; }
        public int RuleGeneration { get; set; }
        public long RulesChangedUtcTicks { get; set; }
        public RouteObservation[] Connections { get; set; } = new RouteObservation[0];
    }

    // Only these engine fields can cross the privilege boundary. Hostnames, addresses,
    // ports, user identities, raw rule text and controller credentials are never published.
    internal sealed class ConnectionSummary
    {
        internal const int MaxRows = 256;
        static readonly long Remember = TimeSpan.FromSeconds(45).Ticks;
        readonly Dictionary<string, RouteObservation> history = new Dictionary<string, RouteObservation>();
        public sealed class Metadata
        {
            public string network { get; set; }
            public string processPath { get; set; }
            public string type { get; set; }
        }
        public sealed class Link
        {
            public string id { get; set; }
            public string start { get; set; }
            public Metadata metadata { get; set; }
            public string[] chains { get; set; }
            public string rule { get; set; }
            public long upload { get; set; }
            public long download { get; set; }
        }
        public sealed class Reply { public Link[] connections { get; set; } }

        static string Executable(string raw)
        {
            if (String.IsNullOrEmpty(raw) || raw.Length > 8192) return null;
            // The Windows controller can append " (DOMAIN\\user)" to an otherwise exact path.
            int suffix = raw.LastIndexOf(".exe (", StringComparison.OrdinalIgnoreCase);
            if (suffix >= 0 && raw.EndsWith(")", StringComparison.Ordinal)) raw = raw.Substring(0, suffix + 4);
            if (raw.Length > 2048 || !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || raw.Any(Char.IsControl)) return null;
            try { return System.IO.Path.IsPathRooted(raw) ? raw : null; }
            catch (ArgumentException) { return null; }
        }
        static string Outlet(string[] chain)
        {
            if (chain == null) return "unknown";
            string outlet = null;
            foreach (var tag in chain) {
                if (tag != "astral4" && tag != "direct4" && tag != "direct6") continue;
                if (outlet != null && outlet != tag) return "unknown";
                outlet = tag;
            }
            return outlet ?? "unknown";
        }
        static string RuleLabel(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return "unknown";
            if (raw == "final") return "final";
            return raw.IndexOf("rule_set=selected", StringComparison.OrdinalIgnoreCase) >= 0 ? "selected" : "other";
        }

        internal RouteObservationSnapshot Update(string json, RuleStamp rules, long now)
        {
            var reply = new JavaScriptSerializer().Deserialize<Reply>(json);
            if (reply == null || reply.connections == null) throw new InvalidDataException("Missing engine snapshot");
            foreach (var row in history.Values) row.Active = false;
            bool limited = false;
            int count = 0;
            foreach (var link in reply.connections) {
                if (link?.metadata == null || !Guid.TryParse(link.id, out var id)) continue;
                var meta = link.metadata;
                if (meta.network != "tcp" && meta.network != "udp" || !(meta.type == "tun/applications" || meta.type == "mixed/applications")) continue;
                if (++count > MaxRows) { limited = true; continue; }
                string key = id.ToString("N");
                if (!history.TryGetValue(key, out var row)) history.Add(key, row = new RouteObservation { Id = key });
                row.Path = Executable(meta.processPath);
                row.Protocol = meta.network;
                row.Outlet = Outlet(link.chains);
                row.Rule = RuleLabel(link.rule);
                row.UploadBytes = Math.Max(0, link.upload);
                row.DownloadBytes = Math.Max(0, link.download);
                row.StartedUtcTicks = DateTimeOffset.TryParse(link.start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start) ? start.UtcDateTime.Ticks : 0;
                row.LastSeenUtcTicks = now;
                row.Active = true;
            }
            foreach (var row in history.Values)
                row.OlderThanRules = rules.Generation > 0 && row.StartedUtcTicks > 0 && row.StartedUtcTicks < rules.ChangedUtcTicks;
            var kept = history.Values.Where(r => r.Active || now - r.LastSeenUtcTicks <= Remember)
                .OrderByDescending(r => r.Active).ThenByDescending(r => r.LastSeenUtcTicks).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(MaxRows).ToArray();
            if (history.Count > kept.Length && history.Values.Count(r => r.Active || now - r.LastSeenUtcTicks <= Remember) > MaxRows) limited = true;
            history.Clear();
            foreach (var row in kept) history.Add(row.Id, row);
            return new RouteObservationSnapshot { Available = true, SampleUtcTicks = now, RuleGeneration = rules.Generation,
                RulesChangedUtcTicks = rules.ChangedUtcTicks, Truncated = limited, Connections = kept };
        }
    }

    // A pagefile-backed mapping carries a bounded, sanitized snapshot. It is owned by the
    // session worker, shared read-only with the frontend and vanishes at session teardown.
    internal static class RouteDiagnostics
    {
        internal const int Capacity = 1024 * 1024;
        const int Header = 8;
        static string MapName(string id) { return @"Local\AstralSplit.Observation.v1." + Guid.ParseExact(id, "N").ToString("N"); }
        internal static MemoryMappedFile CreateMap(string id, SecurityIdentifier reader)
        {
            var acl = new MemoryMappedFileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new AccessRule<MemoryMappedFileRights>(MachineStorage.Admins, MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new AccessRule<MemoryMappedFileRights>(MachineStorage.SystemAccount, MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new AccessRule<MemoryMappedFileRights>(reader, MemoryMappedFileRights.Read, AccessControlType.Allow));
            return MemoryMappedFile.CreateNew(MapName(id), Capacity, MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.None, acl, HandleInheritability.None);
        }
        internal static MemoryMappedFile TryCreateMap(string id, SecurityIdentifier reader)
        {
            try { return CreateMap(id, reader); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is ArgumentException || ex is InvalidOperationException) { return null; }
        }
        internal static void Publish(MemoryMappedViewAccessor view, RouteObservationSnapshot snapshot)
        {
            var serializer = new JavaScriptSerializer();
            var payload = Encoding.UTF8.GetBytes(serializer.Serialize(snapshot));
            while (payload.Length > Capacity - Header) {
                snapshot.Truncated = true;
                snapshot.Connections = snapshot.Connections.Take(snapshot.Connections.Length / 2).ToArray();
                payload = Encoding.UTF8.GetBytes(serializer.Serialize(snapshot));
            }
            int seq = view.ReadInt32(0) & ~1;
            view.Write(0, unchecked(seq + 1));
            Thread.MemoryBarrier();
            view.Write(4, payload.Length);
            view.WriteArray(Header, payload, 0, payload.Length);
            Thread.MemoryBarrier();
            view.Write(0, unchecked(seq + 2));
        }
        internal static RouteObservationSnapshot Read(string sessionId)
        {
            if (!Guid.TryParseExact(sessionId, "N", out _)) return null;
            try {
                using (var map = MemoryMappedFile.OpenExisting(MapName(sessionId), MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read)) {
                    for (int attempt = 0; attempt < 3; attempt++) {
                        int start = view.ReadInt32(0);
                        if ((start & 1) != 0) continue;
                        Thread.MemoryBarrier();
                        int length = view.ReadInt32(4);
                        if (length <= 0 || length > Capacity - Header) return null;
                        var data = new byte[length];
                        view.ReadArray(Header, data, 0, length);
                        Thread.MemoryBarrier();
                        if (view.ReadInt32(0) != start) continue;
                        return new JavaScriptSerializer().Deserialize<RouteObservationSnapshot>(Encoding.UTF8.GetString(data));
                    }
                }
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidOperationException || ex is ArgumentException || ex is System.Security.SecurityException) { }
            return null;
        }
    }
}
