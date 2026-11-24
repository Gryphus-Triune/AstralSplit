using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;

[assembly: System.Reflection.AssemblyTitle("星云分流")]
[assembly: System.Reflection.AssemblyProduct("星云分流")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace AstralSplit
{
    internal static class App
    {
        internal static bool BrowseOnly { get; private set; }
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--authorize") { SessionAuthorization.RunHost(args); return; }
            Guid id;
            int parent;
            long started;
            if (args.Length == 4 && (args[0] == "--session" || args[0] == "--restore") && Guid.TryParseExact(args[1], "N", out id) &&
                Int32.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parent) &&
                Int64.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out started))
            {
                RouterSession.Run(id.ToString("N"), parent, started, args[0] == "--restore");
                return;
            }
            if (args.Length == 2 && args[0] == "--restore" && Guid.TryParseExact(args[1], "N", out id))
            {
                RouterSession.Run(id.ToString("N"), 0, 0, true);
                return;
            }
            if(args.Length==1 && (args[0]=="--native-scene" || args[0]=="--native-live-glass")){
                if(args[0]=="--native-live-glass")Environment.SetEnvironmentVariable("ASTRALSPLIT_SCENE_FROST","live");
                Environment.SetEnvironmentVariable("ASTRALSPLIT_WINDOW_SCENE","1");args=new string[0];
            }
            if(args.Length==1 && args[0]=="--wpf"){
                Environment.SetEnvironmentVariable("ASTRALSPLIT_WINDOW_SCENE","0");args=new string[0];
            }
            if(Environment.GetEnvironmentVariable("ASTRALSPLIT_WINDOW_SCENE")==null)
                Environment.SetEnvironmentVariable("ASTRALSPLIT_WINDOW_SCENE","1");
            BrowseOnly = args.Length == 1 && args[0] == "--preview-only";
            if (BrowseOnly) args = new string[0];
            const string channel = "v1";
            bool trayOnly = args.Length == 1 && args[0] == "--tray";
            if (args.Length != 0 && !trayOnly) return;
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AstralSplit.Show." + channel))
            using (var ui = new Mutex(true, "Local\\AstralSplit.Window." + channel, out bool first))
            {
                if (!first) { if (!trayOnly) show.Set(); return; }
                SessionAuthorization authorization;
                try { authorization = BrowseOnly ? null : SessionAuthorization.Authorize(); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }
                catch (Exception ex)
                {
                    MessageBox.Show("无法取得本次运行所需的管理员授权。\n" + ex.Message, "星云分流",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                using (authorization)
                {
                    // This executable is built directly with csc. Enable WPF's per-monitor layout alongside the Win32 manifest.
                    AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
                    if(!trayOnly && ScenePresenter.Requested && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                        GalaxyGpu.Prepare();
                    var application = new Application();
                    var controller = new MainUi();
                    if (trayOnly)
                    {
                        application.MainWindow = controller.Window;
                        application.Startup += (s, e) => controller.InitializeStatus();
                    }
                    else controller.PrepareWindowReveal(false);
                    var listener = ThreadPool.RegisterWaitForSingleObject(show, (state, timedOut) => {
                        if (!application.Dispatcher.HasShutdownStarted)
                            application.Dispatcher.BeginInvoke(new Action(controller.ShowWindow));
                    }, null, Timeout.Infinite, false);
                    try { application.Run(trayOnly ? null : controller.Window); }
                    finally { listener.Unregister(null); }
                }
            }
        }
    }

    public sealed partial class AppRow : INotifyPropertyChanged
    {
        public string Path { get; internal set; }
        public string Name { get; internal set; }
        public ImageSource Icon { get; internal set; }
        Dictionary<int, Task<BitmapSource>> iconSizes;
        internal int Count;
        internal long WorkingSet;
        internal bool VisibleWindow;
        internal bool AwaitingFileName;
        bool active, selected, showMemory, showPath, pendingRemoval, lastVisible;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool HasPathQualifier { get { return showPath; } }
        public string Description { get {
            string exe = System.IO.Path.GetFileName(Path);
            string hint = Count == 0 ? exe + "  ·  未运行" : Count > 1 ? exe + "  ·  " + Count + " 个进程" : exe;
            return showPath ? hint + "  ·  " + System.IO.Path.GetDirectoryName(Path) : hint;
        } }
        public string MemoryLabel { get {
            long share = showMemory ? DisplayShare(Count, WorkingSet) : -1;
            if (share < 0) return "";
            return share == 0 ? "<0.01%" : (share / 100.0).ToString("F2", CultureInfo.InvariantCulture) + "%";
        } }
        public string RouteLabel { get { return Selected ? (active && Count > 0 ? "TUN 规则" : "已选 TUN") : active && Count > 0 ? "直连规则" : ""; } }
        internal void CompleteFileName(string name)
        {
            AwaitingFileName = false;
            if (Name == name) return;
            Name = name;
            Notify("Name");
            Notify("ToggleLabel");
        }
        public string ToggleLabel { get { return "为 " + (showPath ? Path : Name) + " 启用星云 TUN"; } }
        public bool PendingRemoval { get { return pendingRemoval; } internal set {
            if (pendingRemoval == value) return;
            pendingRemoval = value;
            Notify("PendingRemoval");
        } }
        public bool LastVisible { get { return lastVisible; } internal set {
            if (lastVisible == value) return;
            lastVisible = value;
            Notify("LastVisible");
        } }
        public bool Selected { get { return selected; } internal set {
            if (selected == value) return;
            selected = value;
            Notify("Selected");
            Notify("RouteLabel");
        } }

        internal void UpdateUsage(int count, bool window, long workingSet)
        {
            bool countChanged = Count != count;
            bool memoryChanged = showMemory && DisplayShare(Count, WorkingSet) != DisplayShare(count, workingSet);
            bool changedRunning = (Count == 0) != (count == 0);
            Count = count;
            VisibleWindow = window;
            WorkingSet = workingSet;
            if (countChanged) Notify("Description");
            if (memoryChanged) Notify("MemoryLabel");
            if (changedRunning) Notify("RouteLabel");
        }

        internal void RefreshSelection() { Notify("Selected"); }

        // A row belongs to one executable path. Only realized list items request a size.
        internal Task<BitmapSource> IconAt(int pixels)
        {
            if (iconSizes == null) iconSizes = new Dictionary<int, Task<BitmapSource>>();
            Task<BitmapSource> image;
            if (!iconSizes.TryGetValue(pixels, out image) ||
                (image.Status == TaskStatus.RanToCompletion && image.Result == null))
                iconSizes[pixels] = image = ShellIcons.LoadAsync(Path, pixels);
            return image;
        }

        internal void ShowIcon(BitmapSource image)
        {
            if (image == null || ReferenceEquals(Icon, image)) return;
            Icon = image;
            Notify(nameof(Icon));
        }

        static long DisplayShare(int count, long bytes)
        {
            if (count <= 0 || bytes <= 0 || Apps.TotalPhysicalMemory == 0) return -1;
            return (long)Math.Round(10000.0 * bytes / Apps.TotalPhysicalMemory, MidpointRounding.AwayFromZero);
        }

        internal void ShowMemory(bool value)
        {
            if (showMemory == value) return;
            showMemory = value;
            if (DisplayShare(Count, WorkingSet) >= 0) Notify("MemoryLabel");
        }

        internal void ShowPath(bool value)
        {
            if (showPath == value) return;
            showPath = value;
            Notify(nameof(HasPathQualifier));
            Notify("Description");
            Notify("ToggleLabel");
        }

        internal void SetActive(bool value)
        {
            if (active == value) return;
            active = value;
            Notify("RouteLabel");
        }

        void Notify(string property)
        {
            var changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(property));
        }
    }

    internal static class Apps
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage;
            internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created,
            out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visit, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        static HashSet<int> ReadWindowOwners()
        {
            const uint GW_OWNER = 4;
            var owners = new HashSet<int>();
            bool complete = EnumWindows((window, unused) => {
                // Match the visible, unowned top-level windows used by Process.MainWindowHandle.
                if (IsWindowVisible(window) && GetWindow(window, GW_OWNER) == IntPtr.Zero &&
                    GetWindowThreadProcessId(window, out uint processId) != 0)
                    owners.Add((int)processId);
                return true;
            }, IntPtr.Zero);
            return complete ? owners : null;
        }

        internal static readonly ulong TotalPhysicalMemory = ReadTotalMemory();

        static ulong ReadTotalMemory()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
        }

        internal static AppRow[] Read(string[] saved, HashSet<string> known)
        {
            var rows = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
            var buffer = new StringBuilder(32768);
            var windowOwners = ReadWindowOwners();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string path = ImagePath(p.Id, buffer);
                    if (!Files.IsApp(path)) continue;
                    AppRow row;
                    if (!rows.TryGetValue(path, out row)) rows[path] = row = NewRow(path, known);
                    row.Count++;
                    try { row.WorkingSet += Math.Max(0, p.WorkingSet64); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception) { }
                    if (windowOwners != null) row.VisibleWindow |= windowOwners.Contains(p.Id);
                    else if (!row.VisibleWindow)
                        try { row.VisibleWindow = p.MainWindowHandle != IntPtr.Zero; }
                        catch (InvalidOperationException) { }
                }
            }
            foreach (var path in saved)
                if (!rows.ContainsKey(path)) rows[path] = NewRow(path, known);
            return rows.Values.ToArray();
        }

        static AppRow NewRow(string path, HashSet<string> known)
        {
            var row = new AppRow { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            if (known.Contains(path)) return row;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = info.FileDescription;
                if (!String.IsNullOrWhiteSpace(name)) row.Name = name.Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is Win32Exception) { }
            return row;
        }

        internal static string ImagePath(int id, StringBuilder buffer)
        {
            var handle = OpenProcess(0x1000, false, id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                return ImagePath(handle, buffer);
            }
            finally { CloseHandle(handle); }
        }

        static string ImagePath(IntPtr handle, StringBuilder buffer)
        {
            buffer.Clear();
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }

        internal struct SocketOwner
        {
            internal long Created;
            internal string Path;
        }

        internal static SocketOwner SocketOwnerAt(int id, SocketOwner cached, StringBuilder buffer)
        {
            var handle = OpenProcess(0x1000, false, id);
            if (handle == IntPtr.Zero) return default(SocketOwner);
            try
            {
                if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return default(SocketOwner);
                if (cached.Created == created && cached.Path != null) return cached;
                return new SocketOwner { Created = created, Path = ImagePath(handle, buffer) };
            }
            finally { CloseHandle(handle); }
        }
    }

    internal sealed partial class MainUi
    {
        internal readonly Window Window;
        readonly ObservableCollection<AppRow> rows = new ObservableCollection<AppRow>();
        readonly ObservableCollection<AppRow> visibleRows = new ObservableCollection<AppRow>();
        readonly Dictionary<string, AppRow> byPath = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly AnimatedSwitch master;
        readonly TextBox search;
        readonly TextBlock empty, mode, detail, networkText, detailReason, noticeText;
        readonly CountTransition counter;
        readonly Border notice;
        readonly Ellipse dot, statusGlyph;
        readonly ListBox applications;
        readonly Grid windowBody;
        readonly Border filterIndicator;
        readonly TranslateTransform filterSlide = new TranslateTransform();
        readonly Button refresh, recover, sort;
        readonly ToggleButton all, selected;
        readonly MenuItem windowSort, memorySort;
        readonly Forms.NotifyIcon tray;
        readonly StellarTaskbarSweep taskbarSweep;
        readonly DispatcherTimer timer;
        readonly Stopwatch startupAge = new Stopwatch();
        Task worker;
        EventWaitHandle stop;
        string sessionId;
        string phase = "stopped";
        string phaseDetail;
        string message;
        string lastSaveError;
        string selectionIssue;
        bool promoting, stopping, refreshInProgress, networkInProgress, selectedOnly, restoring, sortByMemory;
        bool manualRefreshQueued;
        bool choicesLoaded, rowsActive, closed, closingAfterSave, loadingChoices, readingWorker, recoveryPending;
        bool legacyRecoveryPresent;
        int workerExitRevision;
        bool saveWaitExpired, closingForExit, exitInProgress;
        DispatcherOperation pendingTrayHide;
        Task<bool> pendingSave = Task.FromResult(true);
        string[] committedChoices = new string[0];
        long choiceRevision;
        DateTime nextList, nextNetwork, nextWorker;
        NetworkState network;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal MainUi()
        {
            using (var xaml = typeof(MainUi).Assembly.GetManifestResourceStream("AstralSplit.Main.xaml"))
                Window = (Window)XamlReader.Load(xaml);
            taskbarSweep = new StellarTaskbarSweep(Window);
            master = Find<AnimatedSwitch>("Master");
            search = Find<TextBox>("Search");
            empty = Find<TextBlock>("Empty");
            mode = Find<TextBlock>("ModeTitle");
            detail = Find<TextBlock>("ModeDetail");
            networkText = Find<TextBlock>("NetworkLabel");
            detailReason = Find<TextBlock>("DetailReason");
            counter = new CountTransition(Find<TextBlock>("RunningCount"), Find<TextHalo>("PreviousRunningCount"));
            noticeText = Find<TextBlock>("NoticeText");
            notice = Find<Border>("Notice");
            dot = Find<Ellipse>("NetworkDot");
            statusGlyph = Find<Ellipse>("StatusGlyph");
            refresh = Find<Button>("Refresh");
            sort = Find<Button>("Sort");
            recover = Find<Button>("Recover");
            all = Find<ToggleButton>("AllApps");
            selected = Find<ToggleButton>("SelectedApps");
            windowBody = Find<Grid>("WindowBody");
            filterIndicator = Find<Border>("FilterIndicator");
            applications = Find<ListBox>("Applications");
            applications.ItemsSource = visibleRows;

            windowSort = new MenuItem { Header = "有窗口的应用优先", IsCheckable = true };
            memorySort = new MenuItem { Header = "内存占用从高到低", IsCheckable = true, Tag = "Memory",
                ToolTip = "同一应用的所有进程占用的内存，按占系统总内存的比例显示" };
            windowSort.Click += (sender, args) => ChooseSort(false);
            memorySort.Click += (sender, args) => ChooseSort(true);
            sort.ContextMenu = new ContextMenu { Placement = PlacementMode.Bottom, PlacementTarget = sort,
                Style = (Style)Window.Resources["StarSortMenu"] };
            sort.ContextMenu.Items.Add(windowSort);
            sort.ContextMenu.Items.Add(memorySort);
            LiquidLens.SetScene(sort.ContextMenu,Find<FrostedPane>("ListFrost"));
            SetSort(true);
            InitializePresentation();
            BindConnectionControls();

            var trayMenu = new GlassTrayMenu(ReadAppDarkMode);
            trayMenu.Items.Add("打开星云分流", null, (s, e) => ShowWindow()).Name="OpenWindow";
            trayMenu.Items.Add("退出", null, (s, e) => ExitFromTray()).Name="ExitApp";
            tray = new Forms.NotifyIcon { Text = "星云分流", ContextMenuStrip = trayMenu };
            trayMenu.Bind(tray);
            tray.Visible = true;
            Point? trayPress = null;
            tray.MouseDown += (s,e) => {
                trayPress = e.Button == Forms.MouseButtons.Right ? TaskbarGeometry.Pointer() : null;
            };
            trayMenu.Opening += (s,e) => {
                // The overflow panel may close by the time the user chooses Exit.
                trayExitOrigin = StellarWindowFlight.TrayPoint(tray,trayPress);
                trayPress = null;
            };
            trayMenu.SetWindowHidden(!Window.IsVisible);
            Window.IsVisibleChanged += (s,e) => trayMenu.SetWindowHidden(!Window.IsVisible);
            tray.MouseClick += (s,e) => { if(e.Button==Forms.MouseButtons.Left && (!Window.IsVisible || hidingToTray))ShowWindow(); };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };

            Window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RouteClick));
            Find<Border>("SearchField").MouseLeftButtonDown += (s, e) => {
                search.Focus();
                e.Handled = true;
            };
            search.TextChanged += (s, e) => {
                sky.SearchChart(search.Text);
                bool blank = String.IsNullOrEmpty(search.Text);
                Find<TextBlock>("SearchHint").Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
                ShowSearchClear(!blank);
                RefreshView();
                listScroller?.ScrollToTop();
            };
            Window.PreviewKeyDown += (s, e) => {
                if (exitInProgress) { e.Handled = true; return; }
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
                if (e.Key == Key.Escape && search.IsKeyboardFocusWithin) { search.Clear(); e.Handled = true; }
            };
            Window.Loaded += (s, e) => InitializeStatus();
            Window.Closing += (s, e) => {
                if (closed || closingForExit) return;
                e.Cancel = true;
                if (exitInProgress || !Window.IsVisible || hidingToTray || pendingTrayHide != null) return;
                // WPF must finish cancelling the native close before the window can be hidden.
                pendingTrayHide = Window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                    pendingTrayHide = null;
                    HideToTray();
                }));
            };
            Window.Closed += (s, e) => {
                closed = true;
                tray.Visible = false;
                trayMenu.Dispose();
                tray.Dispose();
                timer.Stop();
                if (stop != null) { stop.Set(); stop.Dispose(); }
            };
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => {
                if (worker != null && DateTime.UtcNow >= nextWorker) PollWorker();
                if (!Window.IsVisible || Window.WindowState == WindowState.Minimized) return;
                CheckMilestone();
                SampleStellarTraffic();
                if (skyDay != DateTime.Today) { skyDay = DateTime.Today; sky.EnsureAtlas(); }
                ApplyDeferredListUpdates();
                if (DateTime.UtcNow >= nextList) RefreshApps();
                if (DateTime.UtcNow >= nextNetwork) UpdateNetwork();
            };
            timer.Start();
            UpdateListSummary();
            UpdateState();
            Task.Run(() => TrimSessionHistory());
        }

        T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        internal async void InitializeStatus()
        {
            if (!choicesLoaded) await LoadChoices();
            if (closed) return;
            UpdateNetwork();
            if (Window.IsVisible) RefreshApps(true);
            UpdateState();
        }

        async Task LoadChoices()
        {
            if (loadingChoices || closed || closingAfterSave) return;
            loadingChoices = true;
            UpdateState();
            string rulesPath = worker != null && !restoring && !stopping ? RouterSession.RulesPath(sessionId) : null;
            try
            {
                var saved = await Task.Run(() => {
                    var paths = Files.LoadPaths();
                    if (rulesPath != null) EngineConfig.WriteRules(rulesPath, paths);
                    return paths;
                });
                if (closed) return;
                committedChoices = saved;
                chosen.Clear();
                chosen.UnionWith(saved);
                foreach (var path in saved)
                {
                    if (byPath.ContainsKey(path)) continue;
                    var added = new AppRow { Path = path, Name = Path.GetFileNameWithoutExtension(path), AwaitingFileName = true };
                    added.SetActive(rowsActive);
                    added.ShowMemory(sortByMemory);
                    byPath.Add(path, added);
                    rows.Add(added);
                }
                foreach (var row in rows) row.Selected = chosen.Contains(row.Path);
                choicesLoaded = true;
                selectionIssue = null;
                message = null;
            }
            catch (Exception ex)
            {
                choicesLoaded = false;
                selectionIssue = message = "无法载入已保存的应用选择。 " + ex.Message;
            }
            finally { loadingChoices = false; }
            if (!closed) { UpdateStellarApplications(); RefreshView(); UpdateState(); }
        }

        void SyncTrayState(string title,string color,int selected,bool connected)
        {
            var menu=tray?.ContextMenuStrip as GlassTrayMenu;
            menu?.UpdateStatus(title,color,selected,connected);
        }

        static bool IsSessionDirectory(DirectoryInfo directory)
        {
            Guid sessionGuid;
            var root = directory.Parent;
            return Guid.TryParseExact(directory.Name, "N", out sessionGuid) && root != null &&
                String.Equals(root.FullName, Path.Combine(Files.Home, "sessions"), StringComparison.OrdinalIgnoreCase) &&
                root.Parent != null && !IsLink(root.Parent) && !IsLink(root) && !IsLink(directory);
        }

        static bool IsLink(FileSystemInfo entry) { return (entry.Attributes & FileAttributes.ReparsePoint) != 0; }

        static bool HistoryReadError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException || ex is System.Security.SecurityException;
        }

        static bool IsTerminal(DirectoryInfo directory)
        {
            var status = new FileInfo(Path.Combine(directory.FullName, "status.json"));
            if (!status.Exists || IsLink(status)) return false;
            var report = ReadStatus(status.FullName);
            return report != null && (report.Phase == "stopped" || report.Phase == "error");
        }

        static void RemoveSessionFiles(DirectoryInfo directory, bool canceled)
        {
            try
            {
                if (!directory.Exists || !IsSessionDirectory(directory)) return;
                var entries = directory.GetFileSystemInfos();
                if (entries.Any(entry => !(entry is FileInfo) || IsLink(entry))) return;
                if (canceled)
                {
                    if (entries.Length > 1 || (entries.Length == 1 && entries[0].Name != "selection.json")) return;
                }
                else if (!IsTerminal(directory) || entries.Any(entry => entry.Name != "status.json" && entry.Name != "router.json" && entry.Name != "selection.json")) return;
                foreach (var entry in entries.OrderBy(entry => entry.Name == "status.json")) entry.Delete();
                directory.Delete(false);
            }
            catch (Exception ex) when (HistoryReadError(ex)) { }
        }

        static void DiscardPreparedSelection(string id) { RemoveSessionFiles(new DirectoryInfo(RouterSession.Folder(id)), true); }

        static void TrimSessionHistory()
        {
            try
            {
                var root = new DirectoryInfo(Path.Combine(Files.Home, "sessions"));
                if (!root.Exists || IsLink(root) || root.Parent == null || IsLink(root.Parent)) return;
                var completed = new List<Tuple<DirectoryInfo, DateTime>>();
                foreach (var directory in root.EnumerateDirectories())
                {
                    try {
                        if (IsSessionDirectory(directory) && IsTerminal(directory))
                            completed.Add(Tuple.Create(directory, File.GetLastWriteTimeUtc(Path.Combine(directory.FullName, "status.json"))));
                    }
                    catch (Exception ex) when (HistoryReadError(ex)) { }
                }
                foreach (var item in completed.OrderByDescending(item => item.Item2).ThenBy(item => item.Item1.Name, StringComparer.Ordinal).Skip(12))
                    RemoveSessionFiles(item.Item1, false);
            }
            catch (Exception ex) when (HistoryReadError(ex)) { }
        }

        internal void ShowWindow()
        {
            if (closed || exitInProgress) return;
            CancelPendingTaskbarMinimize();
            pendingTrayHide?.Abort();
            pendingTrayHide = null;
            bool reversing = hidingToTray;
            bool fromTray = !Window.IsVisible;
            bool reveal = fromTray || Window.WindowState == WindowState.Minimized || reversing;
            bool returning = reversing && ReverseOutgoingFlight();
            if (reveal && !returning)
            {
                CancelWindowFlight();
                if (!fromTray && Window.WindowState == WindowState.Minimized) PrepareTaskbarReveal();
                else PrepareWindowReveal(fromTray);
            }
            try
            {
                Window.Show();
                if (Window.WindowState == WindowState.Minimized) Window.WindowState = taskbarRestoreState;
                Window.Activate();
                StartPreparedReveal();
            }
            catch { CancelWindowFlight(); throw; }
            nextList = nextNetwork = DateTime.MinValue;
        }

        async void ExitFromTray()
        {
            if (closed || exitInProgress) return;
            var save = pendingSave;
            if (closingAfterSave)
            {
                // Only another explicit Exit after the warning can abandon a stalled write.
                if (!saveWaitExpired || save.IsCompleted) return;
            }
            else if (!save.IsCompleted)
            {
                closingAfterSave = true;
                UpdateState();
                if (await Task.WhenAny(save, Task.Delay(4000)) != save && !save.IsCompleted && !closed)
                {
                    saveWaitExpired = true;
                    ShowWindow();
                    UpdateState();
                }
                bool ok;
                try { ok = await save; }
                catch (Exception ex) when (PreferenceFailure(ex)) { ok = false; }
                if (closed || exitInProgress) return;
                closingAfterSave = saveWaitExpired = false;
                if (!ok) { ShowWindow(); UpdateState(); return; }
            }
            BeginAnimatedExit();
        }

        void SetFilter(bool onlySelected)
        {
            bool changed = selectedOnly != onlySelected;
            if (changed) CancelRowRemovals();
            selectedOnly = onlySelected;
            all.IsChecked = !onlySelected;
            selected.IsChecked = onlySelected;
            if (changed)
            {
                PositionFilterIndicator(true);
                TransitionList();
            }
            else RefreshView();
        }

        bool Matches(AppRow row, string term)
        {
            if (selectedOnly ? !row.Selected && !rowRemovals.ContainsKey(row) : row.Count == 0) return false;
            return term.Length == 0 || row.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                row.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        sealed class RowOrder : IComparer<AppRow>
        {
            readonly bool byMemory;
            internal RowOrder(bool byMemory) { this.byMemory = byMemory; }

            public int Compare(AppRow a, AppRow b)
            {
                if (byMemory)
                {
                    int memory = b.WorkingSet.CompareTo(a.WorkingSet);
                    if (memory != 0) return memory;
                }
                int windows = b.VisibleWindow.CompareTo(a.VisibleWindow);
                if (windows != 0) return windows;
                int label = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                return label == 0 ? StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path) : label;
            }
        }

        void RefreshView()
        {
            CancelSortLayout();
            listScroller?.StopWheelScroll();
            listUpdatePending = false;
            string term = search.Text.Trim();
            var next = rows.Where(row => Matches(row, term)).OrderBy(row => row, new RowOrder(sortByMemory)).ToArray();
            var present = new HashSet<AppRow>(next);
            for (int i = visibleRows.Count - 1; i >= 0; i--)
                if (!present.Contains(visibleRows[i])) visibleRows.RemoveAt(i);
            for (int i = 0; i < next.Length; i++)
            {
                if (i < visibleRows.Count && visibleRows[i] == next[i]) continue;
                int previous = visibleRows.IndexOf(next[i]);
                if (previous < 0) visibleRows.Insert(i, next[i]);
                else visibleRows.Move(previous, i);
            }
            UpdateListSummary();
        }

        void UpdateListSummary()
        {
            int count = visibleRows.Count;
            for (int i = 0; i < count; i++) visibleRows[i].LastVisible = i == count - 1;
            selected.Content = choicesLoaded ? "已选 " + chosen.Count : "已选 ?";
            bool loading = count == 0 && !appsRead;
            ShowListLoading(loading);
            empty.Visibility = count == 0 && !loading ? Visibility.Visible : Visibility.Collapsed;
            bool searching = !String.IsNullOrWhiteSpace(search.Text);
            ShowEmptyArt(searching);
            counter.Show(loading ? (int?)null : count, loading ? String.Empty : count + (searching ? " 个结果" : " 个"));
            string scope = selectedOnly ? "已选应用" : "运行中的应用";
            AutomationProperties.SetName(applications, scope + (searching ? "搜索结果" : "列表"));
            emptyHint.Text = String.Empty;
            if (count != 0 || loading) empty.Text = String.Empty;
            else if (selectedOnly && !choicesLoaded)
            {
                empty.Text = "无法读取已选应用";
                emptyHint.Text = "点按刷新重试。";
            }
            else if (searching)
            {
                empty.Text = "没有找到匹配的" + scope;
                emptyHint.Text = "试试其他应用名称或程序路径。";
            }
            else if (selectedOnly)
            {
                empty.Text = "还没有选择应用";
                emptyHint.Text = "在「运行中」打开需要分流的应用开关。";
            }
            else
            {
                empty.Text = "暂无可选的运行中应用";
                emptyHint.Text = "启动应用后点按刷新。";
            }
        }

        async void RefreshApps(bool indicate = false)
        {
            if (closed) return;
            if (refreshInProgress)
            {
                // A manual scan already covers further clicks. During an automatic scan,
                // acknowledge the release immediately and take one fresh snapshot afterwards.
                if (indicate && !refreshFeedback)
                {
                    manualRefreshQueued = true;
                    SetRefreshFeedback(true);
                }
                return;
            }
            refreshInProgress = true;
            if (indicate && !refreshFeedback) SetRefreshFeedback(true);
            try
            {
                var remembered = chosen.ToArray();
                var known = new HashSet<string>(byPath.Values.Where(r=>!r.AwaitingFileName).Select(r=>r.Path), StringComparer.OrdinalIgnoreCase);
                var apps = await Task.Run(() => Apps.Read(remembered, known));
                if (closed) return;
                var paths = new HashSet<string>(apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
                bool membershipChanged = false, filterChanged = false, windowPriorityChanged = false, rankMayChange = false;
                foreach (var app in apps)
                {
                    AppRow existing;
                    if (byPath.TryGetValue(app.Path, out existing))
                    {
                        if(existing.AwaitingFileName){membershipChanged|=existing.Name!=app.Name;existing.CompleteFileName(app.Name);}
                        filterChanged |= (existing.Count == 0) != (app.Count == 0);
                        windowPriorityChanged |= existing.VisibleWindow != app.VisibleWindow;
                        rankMayChange |= sortByMemory && existing.WorkingSet != app.WorkingSet;
                        existing.UpdateUsage(app.Count, app.VisibleWindow, app.WorkingSet);
                    }
                    else
                    {
                        app.Selected = chosen.Contains(app.Path);
                        app.SetActive(rowsActive);
                        app.ShowMemory(sortByMemory);
                        byPath.Add(app.Path, app);
                        rows.Add(app);
                        membershipChanged = true;
                    }
                }
                foreach (var stale in byPath.Values.Where(r => !paths.Contains(r.Path)).ToArray())
                {
                    if (stale.Selected || visibleRows.Contains(stale))
                    {
                        filterChanged |= stale.Count != 0;
                        windowPriorityChanged |= stale.VisibleWindow;
                        rankMayChange |= sortByMemory && stale.WorkingSet != 0;
                        stale.UpdateUsage(0, false, 0);
                    }
                    else { rows.Remove(stale); byPath.Remove(stale.Path); membershipChanged = true; }
                }
                if (membershipChanged)
                    foreach (var group in rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        bool duplicate = group.Skip(1).Any();
                        foreach (var row in group) row.ShowPath(duplicate);
                    }
                if (membershipChanged || filterChanged || windowPriorityChanged) UpdateStellarApplications();
                listUpdatePending |= membershipChanged || filterChanged || ((rankMayChange || windowPriorityChanged) && OrderChanged()) || !appsRead;
                appsRead = true;
                if (indicate && listUpdatePending) RefreshView();
                else ApplyDeferredListUpdates();
            }
            catch (Exception ex) { message = "应用列表刷新失败。 " + ex.Message; UpdateState(); }
            finally
            {
                refreshInProgress = false;
                nextList = DateTime.UtcNow.AddSeconds(7);
                if (!closed && indicate) SetRefreshFeedback(false);
                if (!closed && !appsRead) { appsRead = true; UpdateListSummary(); }
                if (manualRefreshQueued)
                {
                    manualRefreshQueued = false;
                    if (!closed) RefreshApps(true);
                }
            }
        }

        bool OrderChanged()
        {
            var order = new RowOrder(sortByMemory);
            AppRow previous = null;
            foreach (var current in visibleRows)
            {
                if (previous != null && order.Compare(previous, current) > 0) return true;
                previous = current;
            }
            return false;
        }

        async void UpdateNetwork()
        {
            if (networkInProgress) return;
            networkInProgress = true;
            int revision = workerExitRevision;
            bool pending = false, legacyPresent = false;
            try { network = await Task.Run(() => {
                pending = Recovery.Pending;
                legacyPresent = Recovery.LegacyRecordPresent;
                return NetworkState.Read();
            }); }
            catch (Exception) { network = null; }
            finally
            {
                legacyRecoveryPresent = legacyPresent;
                // A worker's exit snapshot supersedes any journal read started before its cleanup.
                if (revision == workerExitRevision) {
                    recoveryPending = pending;
                    trialRecoveryCheckPending=false;
                }
                networkInProgress = false;
                nextNetwork = DateTime.UtcNow.AddSeconds(4);
                if (!closed) {
                    UpdateState();
                    if(trialRecoveryCheckPending)UpdateNetwork();
                }
            }
        }

        async void RouteClick(object sender, RoutedEventArgs args)
        {
            if (exitInProgress) return;
            var source = args.OriginalSource as FrameworkElement;
            if (source == null) return;
            var toggle = source as AnimatedSwitch;
            if (toggle == master)
            {
                if (!master.IsEnabled) { UpdateState(); return; }
                if (worker != null && !restoring)
                {
                    stop.Set();
                    stopping = true;
                    UpdateState();
                }
                else if (worker == null && !promoting) await Start(false);
                return;
            }
            if (toggle != null && toggle.DataContext is AppRow)
            {
                ChangeApp((AppRow)toggle.DataContext, toggle.IsChecked == true);
                return;
            }
            switch (source.Name)
            {
                case "HideToTray": HideToTray(); break;
                case "Minimize": MinimizeToTaskbar(); break;
                case "ZoomWindow": Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                case "Refresh":
                    if (!choicesLoaded) await LoadChoices();
                    RefreshApps(true); UpdateNetwork(); break;
                case "Sort": OpenSortMenu(true); break;
                case "ClearSearch": search.Clear(); search.Focus(); break;
                case "AllApps": SetFilter(false); break;
                case "SelectedApps": SetFilter(true); break;
                case "Recover": if (worker == null && !promoting) await Start(true); break;
            }
        }

        void ChangeApp(AppRow row, bool value)
        {
            if (!choicesLoaded || promoting || closingAfterSave || closed) { row.RefreshSelection(); return; }
            if (!(value ? chosen.Add(row.Path) : chosen.Remove(row.Path))) return;
            choiceRevision++;
            if (value) recentStellarPath = row.Path;
            if (!value) SignalStellarApp(row,false);
            row.Selected = value;
            if (selectedOnly)
            {
                if (value) CancelRowRemoval(row);
                else HoldRow(row);
            }
            UpdateStellarApplications();
            UpdateListSummary();
            if (value) SignalStellarApp(row,true);
            if (pendingSave.IsCompleted) pendingSave = SaveChanges();
        }

        sealed class RollbackFailed : IOException
        {
            internal RollbackFailed(Exception cause) : base(cause.Message, cause) { }
        }

        static void PersistChoices(string[] paths, string[] previous, string rulesPath)
        {
            Files.Write(Files.Settings, paths);
            if (rulesPath == null) return;
            try { EngineConfig.WriteRules(rulesPath, paths); }
            catch
            {
                try { Files.Write(Files.Settings, previous); }
                catch (Exception rollbackError) { throw new RollbackFailed(rollbackError); }
                throw;
            }
        }

        async Task<bool> SaveChanges()
        {
            while (true)
            {
                long revision = choiceRevision;
                var desired = chosen.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                var previous = committedChoices;
                string rulesPath = worker != null && !restoring && !stopping ? RouterSession.RulesPath(sessionId) : null;
                try { await Task.Run(() => PersistChoices(desired, previous, rulesPath)); }
                catch (Exception ex)
                {
                    if (closed) return false;
                    bool uncertain = ex is RollbackFailed;
                    if (!uncertain && revision != choiceRevision) continue;
                    choicesLoaded = !uncertain;
                    lastSaveError = message = uncertain ? "实时规则未更新，已保存的选择无法还原。点「刷新」重新同步。 " + ex.Message : "无法保存应用选择，已恢复上次保存的选择。 " + ex.Message;
                    if (uncertain) selectionIssue = message;
                    chosen.Clear();
                    chosen.UnionWith(committedChoices);
                    foreach (var existing in rows) existing.Selected = chosen.Contains(existing.Path);
                    CancelRowRemovals();
                    UpdateStellarApplications();
                    RefreshView();
                    UpdateState();
                    return false;
                }
                if (closed) return true;
                committedChoices = desired;
                if (revision != choiceRevision) continue;
                if (lastSaveError != null && ReferenceEquals(message, lastSaveError)) message = null;
                lastSaveError = null;
                UpdateState();
                return true;
            }
        }

        async Task<Task> LaunchPrepared(string id, string[] initial, bool recoverOnly)
        {
            Task launched = null;
            try
            {
                if (!recoverOnly)
                    await Task.Run(() => EngineConfig.WriteRules(RouterSession.RulesPath(id), initial)).ConfigureAwait(false);
                if (Volatile.Read(ref closed)) return null;
                launched = await Task.Factory.StartNew(() => SessionAuthorization.Current.StartSession(id, recoverOnly),
                    CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
                if (launched == null) throw new InvalidOperationException("Windows 没有启动分流进程。");
                if (!Volatile.Read(ref closed)) return launched;
                return null;
            }
            finally
            {
                if (launched == null && !recoverOnly)
                    await Task.Run(() => DiscardPreparedSelection(id)).ConfigureAwait(false);
            }
        }

        async Task Start(bool recoverOnly)
        {
            if (App.BrowseOnly || TrialWorkerPhase!=null) return;
            if (closed || closingAfterSave || (!recoverOnly && !choicesLoaded)) { UpdateState(); return; }
            sessionId = Guid.NewGuid().ToString("N");
            message = null;
            promoting = true;
            restoring = recoverOnly;
            UpdateState();
            try
            {
                if (!pendingSave.IsCompleted && !await pendingSave) return;
                if (closed || closingAfterSave || (!recoverOnly && !choicesLoaded)) return;
                if (!recoverOnly) stop = RouterSession.CreateStopEvent(sessionId);
                var launched = await LaunchPrepared(sessionId, committedChoices, recoverOnly);
                if (launched == null) return;
                if (closed) return;
                worker = launched;
                nextWorker = DateTime.MinValue;
                startupAge.Restart();
                phase = "starting";
                phaseDetail = null;
                stopping = false;
            }
            catch (Exception ex) { if (!closed) message = (recoverOnly ? "无法启动网络恢复。 " : "无法启动分流。 ") + ex.Message; }
            finally
            {
                promoting = false;
                if (worker == null)
                {
                    restoring = false;
                    if (stop != null) { if (!closed) stop.Set(); stop.Dispose(); stop = null; }
                }
                if (!closed) UpdateState();
            }
        }

        sealed class WorkerSnapshot
        {
            internal bool Exited, ReadError, RecoveryPending;
            internal SessionStatus Status;
        }

        static SessionStatus ReadStatus(string path)
        {
            var json = Files.Read<object>(path) as IDictionary<string, object>;
            if (json == null) return null;
            var fields = new Dictionary<string, object>(json, StringComparer.OrdinalIgnoreCase);
            object phaseValue, detailValue;
            if (!fields.TryGetValue("Phase", out phaseValue) || !(phaseValue is string)) return null;
            fields.TryGetValue("Detail", out detailValue);
            if (detailValue != null && !(detailValue is string)) return null;
            return new SessionStatus { Phase = (string)phaseValue, Detail = (string)detailValue };
        }

        static WorkerSnapshot ReadWorker(Task session, string id)
        {
            var snapshot = new WorkerSnapshot();
            try
            {
                snapshot.Exited = session.IsCompleted;
                if (snapshot.Exited) snapshot.RecoveryPending = Recovery.Pending;
                string path = RouterSession.StatusPath(id);
                if (File.Exists(path))
                {
                    snapshot.Status = ReadStatus(path);
                    snapshot.ReadError = snapshot.Status == null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException) { snapshot.ReadError = true; }
            return snapshot;
        }

        async void PollWorker()
        {
            if (readingWorker || closed || worker == null) return;
            readingWorker = true;
            var expected = worker;
            string id = sessionId;
            WorkerSnapshot snapshot;
            try { snapshot = await Task.Run(() => ReadWorker(expected, id)); }
            finally { readingWorker = false; }
            if (closed || worker != expected || id != sessionId) return;
            var state = snapshot.Status;
            if (!snapshot.Exited)
            {
                if (state == null && !snapshot.ReadError && phase == "starting" && startupAge.Elapsed < TimeSpan.FromSeconds(3)) return;
                string reported = state == null ? null : state.Phase;
                string next = reported == "active" || reported == "starting" || reported == "reconnecting" || reported == "waiting" ? reported :
                    reported == "stopped" || reported == "error" ? "finishing" : "unknown";
                string explanation = state == null ? null : state.Detail;
                if (next != phase || explanation != phaseDetail)
                {
                    phase = next;
                    phaseDetail = explanation;
                    nextNetwork = DateTime.MinValue;
                    UpdateState();
                }
                nextWorker = DateTime.UtcNow.AddMilliseconds(phase == "active" && !stopping ? 1000 : 0);
                return;
            }

            message = state == null || (state.Phase != "stopped" && state.Phase != "error") ? "后台已退出，未能读取最终状态。" :
                state.Phase == "error" ? state.Detail : message;
            if (expected.IsFaulted) message = expected.Exception.GetBaseException().Message;
            worker = null;
            workerExitRevision++;
            recoveryPending = snapshot.RecoveryPending;
            if (stop != null) { stop.Dispose(); stop = null; }
            if (restoring && state != null && state.Phase == "stopped") message = "已恢复原有网络路由。";
            phase = "stopped";
            phaseDetail = null;
            stopping = restoring = false;
            nextNetwork = DateTime.MinValue;
            UpdateState();
            _ = Task.Run(() => TrimSessionHistory());
        }

        void UpdateState()
        {
            var astral = network == null ? null : network.Astral;
            var physical = network == null ? null : network.Physical4;
            bool ready = network != null && network.Ready;
            bool running = phase == "active" && !stopping;
            string trial=TrialWorkerPhase;
            bool trialRunning=trial=="active";
            bool recovering = trial==null && worker == null && !promoting && recoveryPending;
            bool checkingNetwork = network == null && (networkInProgress || nextNetwork == DateTime.MinValue);
            if (rowsActive != (running && choicesLoaded))
            {
                rowsActive = running && choicesLoaded;
                foreach (var row in rows) row.SetActive(rowsActive);
            }
            string title, description;
            string statusColor = "StatusBusy";
            bool listUnavailable = !choicesLoaded && !loadingChoices && selectionIssue != null;
            string listStatus = listUnavailable ? "暂时无法确认应用选择，点「刷新」重试。" : "正在读取你选的应用…";
            string reason = null;
            if(trial!=null)
            {
                switch(trial){
                    case "active": title="分流试用中";statusColor="StatusOn";break;
                    case "waiting": title="试用等待网络";statusColor="StatusWarning";break;
                    case "reconnecting": title="试用正在重连";break;
                    case "finishing": title="正在结束试用";break;
                    case "unknown": title="试用状态待确认";statusColor="StatusWarning";break;
                    default: title="准备分流试用";break;
                }
                description=trialText??"正在读取试用状态…";
            }
            else if (promoting)
            {
                title = restoring ? "准备恢复网络" : "准备启用分流";
                description = restoring ? "正在准备恢复原来的连接方式…" : "正在准备你选的应用和线路…";
            }
            else if (restoring || stopping)
            {
                title = restoring ? "正在恢复网络" : "正在关闭分流";
                description = "正在恢复原来的连接方式…";
            }
            else if (running)
            {
                title = "分流已启用";
                description = !choicesLoaded ? listStatus : committedChoices.Length == 0
                    ? "选中下方的应用，它下次连接时就会交给星云。"
                    : "你选的应用交给星云，其他应用照常直连。";
                statusColor = listUnavailable ? "StatusWarning" : choicesLoaded ? "StatusOn" : "StatusBusy";
                if (!String.IsNullOrWhiteSpace(phaseDetail)) {
                    description = phaseDetail;
                    reason = phaseDetail;
                    statusColor = "StatusWarning";
                }
            }
            else if (phase == "waiting" || phase == "reconnecting")
            {
                title = phase == "waiting" ? "等待网络" : "正在重新连接";
                description = "正在等线路就绪，这段时间应用可能会直连。";
                reason = phaseDetail;
                if (phase == "waiting") statusColor = "StatusWarning";
            }
            else if (phase == "unknown")
            {
                title = "分流状态未知";
                description = "暂时无法确认应用是否正经过星云。";
                reason = phaseDetail;
                statusColor = "StatusWarning";
            }
            else if (phase == "finishing" || worker != null)
            {
                title = phase == "finishing" ? "正在结束分流" : "正在启用分流";
                description = phase == "finishing" ? "正在做最后的清理…" : "正在准备你选的应用和线路…";
                reason = phaseDetail;
            }
            else if (recovering)
            {
                title = "网络待恢复";
                description = "点「恢复网络」，找回原来的连接方式。";
                statusColor = "StatusWarning";
            }
            else
            {
                title = "分流已关闭";
                if (!choicesLoaded) description = listStatus;
                else if (network == null) description = checkingNetwork ? "正在检查网络连接…" : "暂时读不到网络信息，点「刷新」重试。";
                else if (astral == null) description = "先在星云加速器里打开 TUN，再连接一个节点。";
                else if (physical == null) description = "正在等待可以直连的网络。";
                else description = ready ? "打开分流后，你选的应用会交给星云。" : "已找到星云，正在等节点连接就绪。";
                statusColor = listUnavailable ? "StatusWarning" : "StatusOff";
            }
            UpdateStellarPhase(running, recovering);
            bool connected = (running || trialRunning) && !restoring && !promoting && !exitInProgress;
            taskbarSweep.Update(connected, (trial??phase) == "unknown" && !stopping);
            ShowStatus(title, description, statusColor);
            SyncTrayState(title, statusColor, trial!=null?trialPlan?.Count??-1:choicesLoaded?committedChoices.Length:-1, connected);
            if (listUnavailable)
                reason = String.IsNullOrWhiteSpace(reason) ? selectionIssue : reason + Environment.NewLine + selectionIssue;
            detailReason.Text = reason ?? String.Empty;
            detailReason.Visibility = String.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible;
            master.IsChecked = running || (worker != null && !stopping && !restoring) || (promoting && !restoring);
            bool starting = worker != null && phase == "starting" && !restoring;
            bool busy = stopping || starting || (promoting && !restoring);
            master.SetBusy(busy);
            busyPressShield.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            master.Visibility=trial==null?Visibility.Visible:Visibility.Collapsed;
            endTrial.Visibility=trial==null?Visibility.Collapsed:Visibility.Visible;
            endTrial.IsEnabled=!App.BrowseOnly && trial!="finishing";
            master.IsEnabled = !App.BrowseOnly && trial==null && !closingAfterSave && !promoting && !restoring && !stopping && !starting &&
                (worker != null || (choicesLoaded && ready));
            applications.IsEnabled = choicesLoaded && !promoting && !closingAfterSave;
            networkText.Text = network == null ? (checkingNetwork ? "正在检查网卡" : "暂时无法读取网卡") :
                (astral == null ? "未检测到星云 TUN" : "星云网卡 " + astral.Name) + "  ·  " +
                (physical == null ? "无 IPv4 直连网络" : "直连网卡 " + physical.Name);
            bool animateDot = UiMotion.CanAnimate(dot);
            SetColor(networkFill, Theme(ready ? "SkyStatusOn" : "SkyStatusOff"), 250, animateDot ? DisplayRefresh.For(dot) : null, animateDot);
            recover.Visibility = recovering ? Visibility.Visible : Visibility.Collapsed;
            recover.IsEnabled = !App.BrowseOnly && !trialInProgress;
            bool showMessage = !String.IsNullOrWhiteSpace(message) &&
                !(ReferenceEquals(message, selectionIssue) && (description == listStatus || loadingChoices));
            bool showLegacy = legacyRecoveryPresent && worker == null && !promoting && trial == null;
            string recoveryNotice = recovering ? "发现上个会话的恢复记录。" :
                showLegacy ? "检测到旧版网络恢复记录，文件已保留。若网络异常，请按使用说明检查旧会话。" : "";
            bool showNotice = saveWaitExpired || recovering || showMessage || showLegacy;
            ShowNotice(showNotice, saveWaitExpired ? "应用选择仍在保存。可以继续等待，也可再次右键托盘图标选择「退出」，最近的选择可能未保存。" :
                showMessage ? message : recoveryNotice);
        }
    }
}
