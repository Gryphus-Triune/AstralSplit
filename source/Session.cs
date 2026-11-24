using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AstralSplit
{
    internal static class Files
    {
        static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        internal const string ProfileName = "AstralSplit";
        internal static readonly string Home = Path.Combine(LocalAppData, ProfileName);
        internal static readonly string Settings = Path.Combine(Home, "apps.json");
        internal static readonly string Journal = Path.Combine(Home, "network-recovery.json");
        internal static readonly string Engine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "AstralSplit.Router.exe");
        internal static readonly string AstralCore = Path.Combine(LocalAppData, "nbapp", "tun.exe");

        internal static T Read<T>(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return new JavaScriptSerializer().Deserialize<T>(reader.ReadToEnd());
        }

        internal static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static string[] LoadPaths()
        {
            try
            {
                var paths = Read<string[]>(Settings);
                if (paths == null) throw new InvalidDataException("应用列表为空或已损坏。");
                return paths.Where(IsApp).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (FileNotFoundException) { return new string[0]; }
            catch (DirectoryNotFoundException) { return new string[0]; }
        }

        static bool IsAstralClient(string path)
        {
            if (!String.Equals(Path.GetFileName(path), "星云加速器.exe", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                return version.CompanyName == "locklayertech" && version.ProductName == "星云加速器";
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool IsApp(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && path.Length < 32768 &&
                Path.IsPathRooted(path) && String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
                !IsAstralClient(path) &&
                !String.Equals(path, AstralCore, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(path, Engine, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(path, MachineStorage.Engine, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(path, typeof(Files).Assembly.Location, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class EngineConfig
    {
        internal static void WriteRules(string path, IEnumerable<string> apps)
        {
            var patterns = apps.Where(Files.IsApp).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(ProcessPattern).ToArray();
            object[] rules = patterns.Length == 0 ? new object[0] : new object[] { new { process_path_regex = patterns } };
            Files.Write(path, new { version = 3, rules });
        }

        static string ProcessPattern(string path)
        {
            var expression = new StringBuilder("(?i)^");
            foreach (char ch in path)
            {
                if (@"\.+*?()|[]{}^$".IndexOf(ch) >= 0) expression.Append('\\');
                expression.Append(ch);
            }
            return expression.Append('$').ToString();
        }

        internal static object Create(NetworkState network, string auxiliary, string rulesPath, int testPort = 0, string peerAstralCore = null,
            RoutePlan plan = null, RouteObserver observation = null)
        {
            var v4 = plan?.Auxiliary4 ?? new Prefix4(RoutePlan.SelectAuxiliary4(network).Network + 1, 32).Address;
            var addresses = new List<string> { v4 + "/30" };
            if (network.Physical6 != null) addresses.Add((plan?.Auxiliary6 ?? RoutePlan.SelectAuxiliary6(network)) + "/126");
            object inbound = testPort == 0 ? (object)new {
                type = "tun", tag = "applications", interface_name = auxiliary, address = addresses,
                mtu = network.CaptureMtu(), auto_route = false, dns_mode = "disabled", stack = "mixed"
            } : new { type = "mixed", tag = "applications", listen = "127.0.0.1", listen_port = testPort };

            var outbounds = new List<object> {
                new { type = "direct", tag = "direct4", bind_interface = network.Physical4.Name, inet4_bind_address = network.Physical4.V4.ToString() },
                new { type = "direct", tag = "astral4", bind_interface = network.Astral.Name, inet4_bind_address = network.Astral.V4.ToString() }
            };
            var corePaths = new[] { Files.AstralCore, peerAstralCore }.Where(path => !String.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(ProcessPattern).ToArray();
            var boundSources = new List<string> { network.Astral.V4 + "/32", network.Physical4.V4 + "/32" };
            if (network.Physical6 != null)
            {
                boundSources.AddRange(network.Physical6.Attached.Select(p => p.Item1).Concat(new[] { network.Physical6.V6 })
                    .Where(address => address != null && address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(address => new IPAddress(address.GetAddressBytes()) + "/128").Distinct());
                outbounds.Add(new { type = "direct", tag = "direct6", bind_interface = network.Physical6.Name });
            }
            string[] socketProtocols = { "tcp", "udp" };
            var rules = new List<object> {
                // A bound outbound must never be forwarded again if Windows feeds it back into this TUN.
                new { source_ip_cidr = boundSources, action = "reject" },
                new { process_path_regex = new[] { ProcessPattern(MachineStorage.Engine), ProcessPattern(Files.Engine) }, action = "reject" },
                new { process_path_regex = corePaths, ip_cidr = RoutePlan.FakeIp4, action = "reject" },
                new { process_path_regex = corePaths, network = socketProtocols, ip_version = 4, action = "route", outbound = "direct4" }
            };
            // Only redirect DNS from selected apps whose packets actually reach this TUN.
            // The vendor's on-link resolver can serve its own synthetic address mappings.
            if (network.AstralDns4 != null)
                rules.Add(new { rule_set = "selected", network = socketProtocols, ip_version = 4, port = 53,
                    action = "route", outbound = "astral4", override_address = network.AstralDns4.ToString() });
            rules.Add(new { rule_set = "selected", network = socketProtocols, ip_version = 4, action = "route", outbound = "astral4" });
            rules.Add(new { rule_set = "selected", network = socketProtocols, ip_version = 6, action = "reject" });
            // A synthetic IPv4 destination is meaningful only to the TUN that issued it.
            rules.Add(new { ip_cidr = RoutePlan.FakeIp4, action = "reject" });
            rules.Add(new { network = "icmp", ip_version = 4, action = "route", outbound = "direct4" });
            if (network.Physical6 != null)
                rules.Add(new { network = "icmp", ip_version = 6, action = "route", outbound = "direct6" });
            if (testPort == 0 && network.Guest4.Length != 0)
                rules.Add(new { source_ip_cidr = network.Guest4.Select(net => net.ToString()).ToArray(),
                    network = socketProtocols, ip_version = 4, action = "route", outbound = "direct4" });
            // Processless host TCP/UDP still cannot fall through to the physical interface.
            rules.Add(new { process_path_regex = ".+", invert = true, action = "reject" });
            if (network.Physical6 != null)
                rules.Add(new { ip_version = 6, action = "route", outbound = "direct6" });
            else rules.Add(new { ip_version = 6, action = "reject" });

            var log = new { level = testPort == 0 ? "warn" : "info", timestamp = true };
            var inbounds = new[] { inbound };
            var route = new {
                    rules, final = "direct4", find_process = true,
                    rule_set = new[] { new { type = "local", tag = "selected", format = "source", path = rulesPath } }
            };
            if (observation == null) return new { log, inbounds, outbounds, route };
            return new { log, inbounds, outbounds, route, experimental = observation.EngineOptions };
        }

        internal static ProcessStartInfo Command(string command, string config)
        {
            return new ProcessStartInfo(MachineStorage.Engine, command + " -c \"" + config + "\"") {
                WorkingDirectory = Path.GetDirectoryName(config),
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
            };
        }
    }

    public sealed class SessionStatus
    {
        public string Phase { get; set; }
        public string Detail { get; set; }
    }

    public sealed class Recovery
    {
        public string Name { get; set; }
        public string AuxiliaryId { get; set; }
        public ulong AuxiliaryLuid { get; set; }
        public string AuxiliaryNext4 { get; set; }
        public string AuxiliaryNext6 { get; set; }
        public string AstralId { get; set; }
        public ulong AstralLuid { get; set; }
        public bool OriginalAutomatic { get; set; }
        public uint OriginalMetric { get; set; }
        public bool? OriginalForwarding { get; set; }
        public string AddedGateway { get; set; }
        public string SessionId { get; set; }
        public uint? AddedMetric { get; set; }
        static Recovery ownedFallback;
        internal static bool Pending { get { return File.Exists(MachineStorage.Journal); } }
        internal static bool LegacyRecordPresent { get { return File.Exists(Files.Journal); } }

        internal const uint SessionMetric = 4096, FallbackMetric = 59993;

        internal static bool IsFallback(Native.RouteRow route)
        {
            return route.V4 && route.PrefixLength == 0 && route.Protocol == 3 && route.Metric == FallbackMetric;
        }

        bool MatchesFallback(Native.RouteRow route)
        {
            return AddedGateway != null && route.Luid == AstralLuid && route.V4 && route.PrefixLength == 0 &&
                route.Protocol == 3 && route.Metric == (AddedMetric ?? FallbackMetric) && route.NextHop.Address.ToString() == AddedGateway;
        }

        internal static bool IsOwnedFallback(Native.RouteRow route)
        {
            return ownedFallback != null && ownedFallback.MatchesFallback(route);
        }

        internal bool MatchesAuxiliary(Native.RouteRow route, Adapter adapter)
        {
            if (adapter == null || route.Luid != AuxiliaryLuid || route.Protocol != 3 ||
                adapter.Luid != AuxiliaryLuid || adapter.Id != AuxiliaryId) return false;
            if (!route.V4)
            {
                if (AuxiliaryNext6 == null) return route.NextHop.Address.Equals(RoutePlan.LegacyNext6);
                if (!IPAddress.TryParse(AuxiliaryNext6, out var next6) || next6.AddressFamily != AddressFamily.InterNetworkV6 ||
                    next6.ScopeId != 0) return false;
                var address = next6.GetAddressBytes();
                return address[0] == 0xfd && (address[15] & 3) == 2 && route.NextHop.Address.Equals(next6);
            }
            if (AuxiliaryNext4 == null) return route.NextHop.Address.Equals(RoutePlan.LegacyNext4);
            return IPAddress.TryParse(AuxiliaryNext4, out var next) && next.AddressFamily == AddressFamily.InterNetwork &&
                RoutePlan.IsPrivateSubnet(Prefix4.From(next, 30)) && (next.GetAddressBytes()[3] & 3) == 2 &&
                route.NextHop.Address.Equals(next);
        }

        internal void AddFallback(IPAddress gateway)
        {
            // Record intent with a session-specific metric before touching the route table.
            AddedMetric = (uint)(50000 + BitConverter.ToUInt16(Guid.NewGuid().ToByteArray(), 0) % 14000);
            AddedGateway = gateway.ToString();
            Files.Write(MachineStorage.Journal, this);
            if (!Native.TryAddRoute(AstralLuid, IPAddress.Any, 0, gateway, AddedMetric.Value))
            {
                AddedGateway = null;
                Files.Write(MachineStorage.Journal, this);
                throw new InvalidOperationException("星云默认路由在启动时已发生变化。");
            }
            ownedFallback = this;
        }

        internal static void Restore()
        {
            RestoreMachineJournal();
            ownedFallback = null;
        }

        static void RestoreMachineJournal()
        {
            // Only a journal written by a previous elevated session may authorize route changes.
            string path = MachineStorage.Journal;
            if (!File.Exists(path)) return;
            var saved = Files.Read<Recovery>(path);
            if (saved == null || saved.Name == null || !saved.Name.StartsWith("ASplit-", StringComparison.Ordinal))
                throw new InvalidOperationException("无法读取上次的网络恢复记录。");
            var adapters = Adapter.Enumerate(onlyUp: false).ToArray();
            var auxiliary = adapters.FirstOrDefault(a => a.Id == saved.AuxiliaryId && a.Luid == saved.AuxiliaryLuid);
            var astral = adapters.FirstOrDefault(a => a.Id == saved.AstralId && a.Luid == saved.AstralLuid &&
                a.Description.StartsWith("astral-tun", StringComparison.OrdinalIgnoreCase));
            var errors = new List<Exception>();
            foreach (var route in Native.Routes())
            {
                bool ours = saved.MatchesAuxiliary(route, auxiliary);
                bool fallback = astral != null && saved.MatchesFallback(route);
                if (!ours && !fallback) continue;
                try { Native.RemoveRoute(route); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (astral != null)
            {
                try
                {
                    var current = Native.ReadInterface(astral.Luid, Native.IPv4);
                    bool restoreMetric = !current.Automatic && current.Metric == SessionMetric;
                    bool restoreForwarding = saved.OriginalForwarding == true && !current.Forwarding;
                    if (restoreMetric || restoreForwarding)
                        Native.WriteMetric(astral.Luid, Native.IPv4, restoreMetric ? saved.OriginalAutomatic : current.Automatic,
                            restoreMetric ? saved.OriginalMetric : current.Metric, restoreForwarding ? true : (bool?)null);
                }
                catch (Win32Exception ex) when (Native.InterfaceGone(ex.NativeErrorCode)) { }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new InvalidOperationException("恢复网络时出错。请在界面中重试恢复。 " + errors[0].Message);
            File.Delete(path);
        }
    }

    internal static class RouterSession
    {
        [ThreadStatic] static SessionPeer peer;
        internal static EventWaitHandle CreateStopEvent(string id)
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(MachineStorage.Admins, EventWaitHandleRights.Synchronize, AccessControlType.Allow));
            bool created;
            return new EventWaitHandle(false, EventResetMode.ManualReset, StopName(id), out created, security);
        }

        static Mutex ActivityLock()
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(MachineStorage.Admins, MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(MachineStorage.SystemAccount, MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            return new Mutex(false, @"Global\AstralSplit.Active.v2", out created, security);
        }
        internal static string Folder(string id) { return Path.Combine(Files.Home, "sessions", id); }
        internal static string StopName(string id) { return "Local\\AstralSplit.Stop." + id; }
        internal static string StatusPath(string id) { return Path.Combine(Folder(id), "status.json"); }
        internal static string RulesPath(string id) { return Path.Combine(Folder(id), "selection.json"); }

        internal static void Run(string id, int parentPid, long parentStart, bool recoverOnly)
        {
            string status = StatusPath(id);
            string error = null;
            bool owns = false;
            bool legacyOwns = false;
            bool storageReady = false;
            Process parent = null;
            Mutex sessionLock = null, legacyLock = null;
            {
                try
                {
                    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new InvalidOperationException("启动分流需要 Windows 管理员权限。");
                    if (parentPid != 0)
                    {
                        parent = Process.GetProcessById(parentPid);
                        peer = new SessionPeer(parent, parentStart, id);
                    }
                    sessionLock = ActivityLock();
                    try { owns = sessionLock.WaitOne(0); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("另一个星云分流会话正在运行。");
                    legacyLock = new Mutex(false, @"Local\AstralSplit.Active.v1");
                    try { legacyOwns = legacyLock.WaitOne(0); }
                    catch (AbandonedMutexException) { legacyOwns = true; }
                    if (!legacyOwns) throw new InvalidOperationException("旧版星云分流会话正在运行。");
                    MachineStorage.Prepare();
                    storageReady = true;
                    Recovery.Restore();
                    if (recoverOnly) return;

                    using (var stop = EventWaitHandle.OpenExisting(StopName(id), EventWaitHandleRights.Synchronize))
                    using (var observations = RouteDiagnostics.TryCreateMap(id, peer.UserSid))
                    {
                        if (parent.StartTime.ToUniversalTime().Ticks != parentStart || parent.HasExited || stop.WaitOne(0)) return;
                        if (!File.Exists(Files.Engine)) throw new InvalidOperationException("还没有安装分流引擎。请先运行同目录的「下载分流引擎.cmd」。");
                        MachineStorage.PrepareEngine(Files.Engine);
                        Func<int, bool> stopping = delay => parent.HasExited || stop.WaitOne(delay) || parent.HasExited;
                        string connecting = "starting";
                        Report(status, connecting, "正在检查网卡与路由…");
                        while (!stopping(0))
                        {
                            var network = WaitForNetwork(ReadNetwork, stopping, connecting,
                                (phase,detail) => Report(status, phase, detail));
                            if (network == null) break;
                            Report(status, connecting, "正在绑定 " + network.Astral.Name + " 和 " + network.Physical4.Name + "…");
                            bool reconnect;
                            try { reconnect = ConnectUntilChanged(id, network, stopping, stop, observations); }
                            catch (ObservationBindException) {
                                if (stopping(0)) break;
                                reconnect = ConnectUntilChanged(id, network, stopping, stop, null);
                            }
                            if (!reconnect) break;
                            connecting = "reconnecting";
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    if (storageReady && !File.Exists(MachineStorage.Journal))
                    {
                        try
                        {
                            string scratch = Path.Combine(MachineStorage.Root, id);
                            foreach (var name in new[] { "router.json", "selection.json" }) File.Delete(Path.Combine(scratch, name));
                            if (Directory.Exists(scratch)) Directory.Delete(scratch);
                        }
                        catch (DirectoryNotFoundException) { }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    try { Report(status, error == null ? "stopped" : "error", error ?? ""); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    finally
                    {
                        if (legacyOwns) legacyLock.ReleaseMutex();
                        if (owns) sessionLock.ReleaseMutex();
                        if (legacyLock != null) legacyLock.Dispose();
                        if (sessionLock != null) sessionLock.Dispose();
                        if (peer != null) { peer.Dispose(); peer = null; }
                        if (parent != null) parent.Dispose();
                    }
                }
            }
        }

        internal static NetworkState WaitForNetwork(Func<NetworkState> read, Func<int, bool> stopping, string connecting, Action<string,string> report)
        {
            string previous = null;
            string lastDetail = null;
            while (!stopping(0))
            {
                var network = read();
                string signature = network != null && network.Ready ? network.Signature(0) : null;
                if (signature != null && signature == previous) return network;
                previous = signature;
                string detail = signature != null ? "网络已连接，正在确认路由稳定…" :
                    network == null || network.Physical4 == null ? "等待 IPv4 直连网络。连接后自动继续。" :
                    "等待星云 TUN。节点连接后自动继续。";
                if (detail != lastDetail) { report(signature != null ? connecting : "waiting",detail); lastDetail = detail; }
                if (stopping(800)) break;
            }
            return null;
        }

        static NetworkState ReadNetwork()
        {
            try { return NetworkState.Read(); }
            catch (Win32Exception ex) when (Native.InterfaceGone(ex.NativeErrorCode)) { return null; }
        }

        static bool Changed(NetworkState live, string signature, ulong auxiliary)
        {
            return live == null || !live.Ready || live.Signature(auxiliary) != signature;
        }

        sealed class ObservationBindException : Exception { }
        static bool ConnectUntilChanged(string id, NetworkState network, Func<int, bool> stopping, WaitHandle stop,
            System.IO.MemoryMappedFiles.MemoryMappedFile observations)
        {
            string status = StatusPath(id);
            string signature = network.Signature(0);
            string auxName = "ASplit-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string scratch = Path.Combine(MachineStorage.Root, id);
            string config = Path.Combine(scratch, "router.json");
            string rules = Path.Combine(scratch, "selection.json");
            var plan = new RoutePlan(network);
            Process engine = null;
            ProcessJob job = null;
            RoutedByteMeter meter=null;
            Adapter aux = null;
            string lastError = null;
            string rulesError = null;
            RuleNotifications notifications = null;
            RouteObserver observation = null;
            try
            {
                var original = Native.ReadInterface(network.Astral.Luid, Native.IPv4);
                notifications = new RuleNotifications(peer.Folder, peer.AsUser);
                peer.CopyRules(rules);
                var ruleOwner = peer;
                observation = RouteObserver.Attach(observations, () => ruleOwner.Stamp);
                Files.Write(config, EngineConfig.Create(network, auxName, rules, peerAstralCore: peer.AstralCorePath, plan: plan, observation: observation));
                CheckConfig(config);
                if (stopping(0)) return false;

                job = new ProcessJob();
                engine = Process.Start(EngineConfig.Command("run", config));
                job.Add(engine);
                var errorFinished = new System.Threading.Tasks.TaskCompletionSource<bool>();
                engine.ErrorDataReceived += (sender, args) => {
                    if (args.Data == null) { errorFinished.TrySetResult(true); return; }
                    if (String.IsNullOrWhiteSpace(args.Data)) return;
                    Interlocked.Exchange(ref lastError, args.Data);
                    if (args.Data.IndexOf("reload rule-set", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        args.Data.IndexOf("watch rule-set file", StringComparison.OrdinalIgnoreCase) >= 0)
                        Interlocked.Exchange(ref rulesError, "分流引擎无法读取实时应用列表。");
                };
                engine.OutputDataReceived += (sender, args) => { };
                engine.BeginErrorReadLine();
                engine.BeginOutputReadLine();
                Action ensureRunning = () => {
                    if (stopping(0)) throw new OperationCanceledException();
                    if (engine.HasExited) {
                        // Flush redirected startup diagnostics before deciding whether the
                        // optional local controller alone prevented the engine from starting.
                        errorFinished.Task.Wait(250);
                        if (observation != null && lastError != null &&
                            lastError.IndexOf("external controller listen error", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new ObservationBindException();
                        throw new InvalidOperationException(EngineError(lastError));
                    }
                    if (rulesError != null) throw new InvalidOperationException(rulesError);
                };

                var deadline = Stopwatch.StartNew();
                do
                {
                    ensureRunning();
                    aux = Adapter.Enumerate().FirstOrDefault(a => a.Name == auxName && Equals(a.V4, plan.Auxiliary4));
                    if (aux != null && (plan.V6.Length == 0 || Equals(aux.V6, plan.Auxiliary6))) break;
                    if (deadline.Elapsed >= TimeSpan.FromSeconds(18))
                        throw new InvalidOperationException("Windows 没有准备好分流网卡。 " + EngineError(lastError));
                } while (!stopping(240));
                ensureRunning();
                var live = ReadNetwork();
                if (Changed(live, signature, aux.Luid) || !plan.AddressStillAvailable(live, aux.Luid)) return Reconnecting(status);

                var recovery = new Recovery {
                    SessionId = id,
                    Name = aux.Name, AuxiliaryId = aux.Id, AuxiliaryLuid = aux.Luid,
                    AuxiliaryNext4 = plan.Next4.ToString(),
                    AuxiliaryNext6 = plan.Next6?.ToString(),
                    AstralId = network.Astral.Id, AstralLuid = network.Astral.Luid,
                    OriginalAutomatic = original.Automatic, OriginalMetric = original.Metric,
                    OriginalForwarding = original.Forwarding
                };
                Files.Write(MachineStorage.Journal, recovery);
                // A forwarding-enabled tun0 can route a bound socket back into our capture interface.
                Native.WriteMetric(network.Astral.Luid, Native.IPv4, false, Recovery.SessionMetric, forwarding: false);
                if (!network.AstralHasDefault)
                    recovery.AddFallback(network.AstralGateway);
                plan.Install(aux, ensureRunning);
                plan.Verify(aux, network, ensureRunning);
                live = ReadNetwork();
                if (Changed(live, signature, aux.Luid)) return Reconnecting(status);
                if (!RoutesIntact(live, network, plan, aux))
                    throw new InvalidOperationException("Windows 没有保留完整的分流路由。");
                Report(status, "active", "");
                observation?.Start(engine);
                meter=RoutedByteMeter.Start(engine.Id,network.Astral.V4,Directory.GetParent(peer.Folder).Parent.FullName,peer.TransferRun,peer.AsUser);

                var watchdog = Stopwatch.StartNew();
                var ruleCheck = Stopwatch.StartNew();
                var waits = new[] { stop, notifications.Changed };
                bool retryRules = false;
                while (!stopping(0))
                {
                    int signaled = WaitHandle.WaitAny(waits, (int)Math.Max(0, 1200 - watchdog.ElapsedMilliseconds));
                    if (signaled == 0 || stopping(0)) break;
                    if (watchdog.ElapsedMilliseconds >= 1200)
                    {
                        watchdog.Restart();
                        meter?.Checkpoint();
                        if(meter?.Complete==true){meter.Dispose();meter=null;}
                        live = ReadNetwork();
                        if (Changed(live, signature, aux.Luid) || !RoutesIntact(live, network, plan, aux))
                            return Reconnecting(status);
                        ensureRunning();
                        RoutePlan.VerifyOutlets(live, checkSocket: false);
                    }
                    if (signaled != 1 && ruleCheck.ElapsedMilliseconds < (retryRules || !notifications.Watching ? 1200 : 15000)) continue;
                    ruleCheck.Restart();
                    try
                    {
                        peer.CopyRules(rules);
                        if (retryRules) Report(status, "active", "");
                        retryRules = false;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                        ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException)
                    {
                        if (!retryRules) Report(status, "active", "应用选择更新失败，暂时沿用上次规则，正在重试。");
                        retryRules = true;
                    }
                }
                return false;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                if (stopping(0)) return false;
                if (Changed(ReadNetwork(), signature, aux == null ? 0 : aux.Luid)) return Reconnecting(status);
                throw;
            }
            finally
            {
                if (notifications != null) notifications.Dispose();
                try { Recovery.Restore(); }
                finally
                {
                    try { StopEngine(engine); }
                    finally { try { if (job != null) job.Dispose(); } finally { try { observation?.Dispose(); } finally { meter?.Dispose(); } } }
                }
            }
        }

        static bool Reconnecting(string status)
        {
            Report(status, "reconnecting", "线路已变化，正在撤销旧路由…");
            return true;
        }

        static bool RoutesIntact(NetworkState live, NetworkState original, RoutePlan plan, Adapter auxiliary)
        {
            var fallbackPresent = original.AstralHasDefault || live.Routes.Any(route =>
                route.Luid == original.Astral.Luid && Recovery.IsOwnedFallback(route) && Equals(route.NextHop.Address, original.AstralGateway));
            var astral = Native.ReadInterface(original.Astral.Luid, Native.IPv4);
            var auxiliary4 = Native.ReadInterface(auxiliary.Luid, Native.IPv4);
            var auxiliary6 = plan.V6.Length == 0 ? auxiliary4 : Native.ReadInterface(auxiliary.Luid, Native.IPv6);
            return fallbackPresent && plan.IsInstalled(live.Routes, auxiliary.Luid) && plan.AddressStillAvailable(live, auxiliary.Luid) &&
                !astral.Forwarding && !astral.Automatic && astral.Metric == Recovery.SessionMetric &&
                auxiliary4.Metric == 0 && auxiliary4.Mtu == plan.Mtu &&
                auxiliary6.Metric == 0 && auxiliary6.Mtu == plan.Mtu;
        }

        static void StopEngine(Process engine)
        {
            if (engine == null) return;
            using (engine)
            {
                if (engine.HasExited) return;
                try { engine.Kill(); }
                catch (Win32Exception) when (engine.HasExited) { return; }
                if (!engine.WaitForExit(4000)) throw new InvalidOperationException("旧的分流引擎未停止，请重新打开星云分流。");
            }
        }

        internal static void CheckConfig(string config)
        {
            using (var process = Process.Start(EngineConfig.Command("check", config)))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(); throw new InvalidOperationException("分流规则检查超时。"); }
                if (process.ExitCode != 0) throw new InvalidOperationException("分流规则检查失败。 " + EngineError(stderr.Result));
                stdout.GetAwaiter().GetResult();
            }
        }

        static string EngineError(string error)
        {
            if (String.IsNullOrWhiteSpace(error)) return "分流引擎已退出。";
            error = System.Text.RegularExpressions.Regex.Replace(error, @"\x1B\[[0-9;]*m", "").Trim();
            return error.Length <= 270 ? error : error.Substring(0, 270) + "…";
        }

        static void Report(string path, string phase, string detail)
        {
            if (peer != null) peer.Report(phase, detail);
            else Files.Write(path, new SessionStatus { Phase = phase, Detail = detail });
        }
    }

    internal sealed class ProcessJob : IDisposable
    {
        readonly IntPtr job;
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int info, IntPtr data, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        internal ProcessJob()
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var data = Marshal.AllocHGlobal(144); // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64.
            try
            {
                Marshal.Copy(new byte[144], 0, data, 144);
                Marshal.WriteInt32(data, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
                if (!SetInformationJobObject(job, 9, data, 144)) throw new Win32Exception();
            }
            catch { CloseHandle(job); throw; }
            finally { Marshal.FreeHGlobal(data); }
        }

        internal void Add(Process process)
        {
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception();
        }

        public void Dispose() { CloseHandle(job); }
    }
}
