using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

namespace AstralSplit
{
    internal static class MachineStorage
    {
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AstralSplit.Network.v2");
        internal static readonly string Journal = Path.Combine(Root, "network-recovery.json");
        // SHA-256 of the sing-box 1.14.4 Windows x64 binary shipped in engine/.
        const string EngineHash = "8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9";
        internal static readonly string Engine = Path.Combine(Root, "engine-" + EngineHash.Substring(0, 12), "AstralSplit.Router.exe");
        internal static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        internal static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        internal static void Prepare()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Admins);
            foreach (var sid in new[] { Admins, SystemAccount })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            // Non-elevated UI needs to see whether a recovery record exists.
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAttributes, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            Directory.CreateDirectory(Root, security);
            var acl = Directory.GetAccessControl(Root);
            var owner = acl.GetOwner(typeof(SecurityIdentifier));
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 || (!Admins.Equals(owner) && !SystemAccount.Equals(owner)) ||
                !acl.AreAccessRulesProtected || acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r =>
                    r.AccessControlType == AccessControlType.Allow && !Admins.Equals(r.IdentityReference) && !SystemAccount.Equals(r.IdentityReference) &&
                    (r.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0))
                throw new InvalidOperationException("网络恢复目录的权限异常，无法安全启动分流。");
        }

        internal static void PrepareEngine(string bundled)
        {
            // Prepare() and the global session mutex precede this call. Only admins
            // and SYSTEM can create or replace anything in this cache directory.
            if (File.Exists(Engine) && MatchesEngine(Engine)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Engine));
            string staging = Engine + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = File.OpenRead(bundled))
                using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(target);
                if (!MatchesEngine(staging)) throw new InvalidDataException("分流引擎与随包版本不符，请重新解压完整安装包。");
                if (File.Exists(Engine)) File.Replace(staging, Engine, null);
                else File.Move(staging, Engine);
            }
            finally { File.Delete(staging); }
        }

        static bool MatchesEngine(string path)
        {
            using (var file = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "") == EngineHash;
        }
    }

    internal sealed class RuleNotifications : IDisposable
    {
        internal readonly AutoResetEvent Changed = new AutoResetEvent(false);
        internal volatile bool Watching;
        FileSystemWatcher watcher;

        internal RuleNotifications(string folder, Action<Action> asUser)
        {
            try
            {
                asUser(() => {
                    watcher = new FileSystemWatcher(folder, "selection.json") {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    watcher.Changed += (_, e) => Signal();
                    watcher.Created += (_, e) => Signal();
                    watcher.Deleted += (_, e) => Signal();
                    watcher.Renamed += (_, e) => Signal();
                    watcher.Error += (_, e) => { Watching = false; Signal(); };
                    Watching = true;
                    watcher.EnableRaisingEvents = true;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is System.Security.SecurityException || ex is Win32Exception)
            {
                Watching = false;
            }
        }

        void Signal()
        {
            try { Changed.Set(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (watcher != null) watcher.Dispose();
            Changed.Dispose();
        }
    }

    // Keep IPC in the launching user's profile, even when UAC selects a different administrator.
    // The engine reads only files generated by its worker inside the protected machine directory.
    internal sealed class SessionPeer : IDisposable
    {
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
        internal readonly string Folder, AstralCorePath, TransferRun;
        readonly WindowsIdentity user;
        internal SecurityIdentifier UserSid { get { return user.User; } }
        RuleStamp stamp = new RuleStamp(0, 0);
        internal RuleStamp Stamp { get { return Volatile.Read(ref stamp); } }

        internal SessionPeer(Process parent, long start, string id)
        {
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != start)
                throw new OperationCanceledException();
            TransferRun=StellarMilestone.RunId(parent.Id,start);
            IntPtr token;
            if (!OpenProcessToken(parent.Handle, 0x000E, out token)) throw new Win32Exception();
            try
            {
                var localData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
                IntPtr path;
                Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref localData, 0, token, out path));
                try
                {
                    string localDataPath = Marshal.PtrToStringUni(path);
                    Folder = Path.Combine(localDataPath, Files.ProfileName, "sessions", id);
                    AstralCorePath = Path.Combine(localDataPath, "nbapp", "tun.exe");
                }
                finally { Marshal.FreeCoTaskMem(path); }
                user = new WindowsIdentity(token);
            }
            finally { CloseHandle(token); }
        }

        internal void Report(string phase, string detail)
        {
            using (user.Impersonate())
                Files.Write(Path.Combine(Folder, "status.json"), new SessionStatus { Phase = phase, Detail = detail });
        }

        public sealed class SelectionRule { public string[] process_path_regex { get; set; } }
        public sealed class Selection { public int version { get; set; } public SelectionRule[] rules { get; set; } }
        string previous;

        internal void CopyRules(string destination)
        {
            string content;
            using (user.Impersonate())
            using (var input = new FileStream(Path.Combine(Folder, "selection.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(input))
            {
                if (input.Length > 2097152) throw new InvalidDataException("应用规则文件过大。");
                content = reader.ReadToEnd();
            }
            if (content == previous && File.Exists(destination)) return;
            var selection = new JavaScriptSerializer().Deserialize<Selection>(content);
            if (selection == null || selection.version != 3 || selection.rules == null || selection.rules.Any(r =>
                r == null || r.process_path_regex == null || r.process_path_regex.Length == 0 || r.process_path_regex.Any(String.IsNullOrWhiteSpace)))
                throw new InvalidDataException("应用规则格式错误。");
            Files.Write(destination, selection);
            previous = content;
            Volatile.Write(ref stamp, new RuleStamp((Stamp?.Generation ?? 0) + 1, DateTime.UtcNow.Ticks));
        }

        public void Dispose() { user.Dispose(); }
        internal void AsUser(Action action) { using(user.Impersonate())action(); }
    }
}
