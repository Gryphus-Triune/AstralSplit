using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Dictionary<IntPtr, Rect> taskButtons = new Dictionary<IntPtr, Rect>();
        bool readingTaskButtons;
        long lastButtonRead, lastBarPress;
        Point? deactivationPoint;

        void StartTaskbarTargetTracking()
        {
            Window.Activated += (s,e) => RefreshTaskButtons();
            Window.ContentRendered += (s,e) => RefreshTaskButtons();
            Find<UIElement>("Minimize").MouseEnter += (s,e) => RefreshTaskButtons();
            Window.Deactivated += (s,e) => {
                var physical = TaskbarGeometry.Pointer();
                deactivationPoint = null;
                if (!physical.HasValue) return;
                foreach (var bar in TaskbarGeometry.ReadBars())
                    if (bar.Bounds.Contains(physical.Value))
                    {
                        deactivationPoint = physical;
                        lastBarPress = Stopwatch.GetTimestamp();
                        break;
                    }
            };
        }

        async void RefreshTaskButtons()
        {
            long now = Stopwatch.GetTimestamp();
            if (closed || readingTaskButtons || (now-lastButtonRead) < Stopwatch.Frequency/2) return;
            readingTaskButtons = true;
            lastButtonRead = now;
            try
            {
                // Explorer registers and moves the button after the first WM_SHOWWINDOW.
                if (taskButtons.Count == 0) await Task.Delay(180);
                if (!closed)
                {
                    var buttons = await Task.Run(() => TaskbarGeometry.ReadButtons());
                    if (!closed) taskButtons = buttons;
                }
            }
            finally { readingTaskButtons = false; }
        }

        Point TaskbarPoint(bool fromShell=false, IntPtr commandPosition = default(IntPtr))
        {
            var handle = new WindowInteropHelper(Window).Handle;
            var bars = TaskbarGeometry.ReadBars();
            var monitor = TaskbarGeometry.MonitorFromWindow(handle,2);
            Point? command = null;
            if (commandPosition != IntPtr.Zero && commandPosition != new IntPtr(-1))
            {
                long packed = commandPosition.ToInt64();
                command = new Point(unchecked((short)packed),unchecked((short)(packed >> 16)));
            }
            var recent = fromShell && Stopwatch.GetTimestamp()-lastBarPress < Stopwatch.Frequency*3/4 ? deactivationPoint : null;
            return TaskbarGeometry.Resolve(bars,taskButtons,monitor,command,fromShell ? TaskbarGeometry.Pointer() : null,recent);
        }
    }

    internal static class TaskbarGeometry
    {
        internal sealed class Bar { internal IntPtr Handle,Monitor; internal Rect Bounds,ShownBounds; }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { internal int x,y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { internal int left,top,right,bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { internal int size; internal NativeRect bounds,work; internal int flags; }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string name,string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect bounds);
        [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out NativePoint point);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window,int options);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("shell32.dll")] static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

        struct PhysicalCoordinates : IDisposable
        {
            readonly IntPtr previous;
            internal PhysicalCoordinates(IntPtr mode)
            {
                try { previous = SetThreadDpiAwarenessContext(mode); }
                catch (EntryPointNotFoundException) { previous = IntPtr.Zero; }
            }
            public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        internal static Point? Pointer()
        {
            NativePoint p;
            return GetPhysicalCursorPos(out p) ? new Point(p.x,p.y) : (Point?)null;
        }

        internal static List<Bar> ReadBars()
        {
            var bars = new List<Bar>();
            using (new PhysicalCoordinates(new IntPtr(-4)))
            {
                foreach (string name in new[]{"Shell_TrayWnd","Shell_SecondaryTrayWnd"})
                {
                    IntPtr bar = IntPtr.Zero;
                    while ((bar = FindWindowEx(IntPtr.Zero,bar,name,null)) != IntPtr.Zero)
                    {
                        NativeRect box;
                        if (GetWindowRect(bar,out box) && box.right > box.left && box.bottom > box.top)
                        {
                            var bounds = new Rect(box.left,box.top,box.right-box.left,box.bottom-box.top);
                            var shown = bounds;
                            var monitor = MonitorFromWindow(bar,2);
                            var info = new MonitorInfo { size=Marshal.SizeOf(typeof(MonitorInfo)) };
                            if (GetMonitorInfo(monitor,ref info))
                            {
                                // With auto-hide Explorer parks most of the bar beyond the edge.
                                shown.X = Math.Max(info.bounds.left,Math.Min(bounds.X,info.bounds.right-bounds.Width));
                                shown.Y = Math.Max(info.bounds.top,Math.Min(bounds.Y,info.bounds.bottom-bounds.Height));
                            }
                            bars.Add(new Bar { Handle=bar, Monitor=monitor, Bounds=bounds, ShownBounds=shown });
                        }
                    }
                }
            }
            return bars;
        }

        // Search only the shell taskbars and only for the current executable's identity.
        // UI Automation can wait for Explorer; callers run this on an MTA worker.
        internal static Dictionary<IntPtr,Rect> ReadButtons()
        {
            var buttons = new Dictionary<IntPtr,Rect>();
            using (new PhysicalCoordinates(new IntPtr(-4)))
            try
            {
                IntPtr explicitId;
                string id;
                if (GetCurrentProcessExplicitAppUserModelID(out explicitId) >= 0 && explicitId != IntPtr.Zero)
                {
                    try { id = Marshal.PtrToStringUni(explicitId); }
                    finally { Marshal.FreeCoTaskMem(explicitId); }
                }
                else using (var process=Process.GetCurrentProcess()) id=process.MainModule.FileName;
                var match = new PropertyCondition(AutomationElement.AutomationIdProperty,"Appid: "+id,PropertyConditionFlags.IgnoreCase);
                foreach (var bar in ReadBars())
                {
                    var button=AutomationElement.FromHandle(bar.Handle).FindFirst(TreeScope.Descendants,match);
                    if (button == null) continue;
                    var rect = button.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2 || !rect.IntersectsWith(bar.Bounds)) continue;
                    rect.Offset(-bar.Bounds.Left,-bar.Bounds.Top);
                    buttons[bar.Handle] = rect;
                }
            }
            catch (Exception ex) when (ex is TimeoutException || ex is NotSupportedException ||
                ex is System.Security.SecurityException || ex is InvalidOperationException || ex is COMException ||
                ex is ElementNotAvailableException || ex is UnauthorizedAccessException ||
                ex is System.ComponentModel.Win32Exception || ex is ArgumentException) { }
            return buttons;
        }

        internal static Point Resolve(List<Bar> bars, Dictionary<IntPtr,Rect> buttons, IntPtr monitor,
            Point? command, Point? pointer, Point? recent)
        {
            Bar preferred=null, firstWithButton=null;
            foreach (Point? hint in new[]{command,pointer,recent})
                if (hint.HasValue)
                    foreach (var bar in bars)
                        if (bar.Bounds.Contains(hint.Value) ||
                            ((command==hint || recent==hint) && bar.ShownBounds.Contains(hint.Value)))
                        {
                            // Explorer may have reordered the buttons since the last lookup.
                            // The physical taskbar click takes precedence over any cached button.
                            return hint.Value;
                        }
            foreach (var bar in bars)
            {
                if (bar.Monitor==monitor) preferred=bar;
                if (firstWithButton==null && buttons.ContainsKey(bar.Handle)) firstWithButton=bar;
            }
            Rect ownButton;
            if (preferred!=null && TryButton(preferred,buttons,out ownButton)) return Center(ownButton);
            if (firstWithButton!=null && TryButton(firstWithButton,buttons,out ownButton)) return Center(ownButton);
            if (preferred!=null || bars.Count>0) return Center((preferred ?? bars[0]).ShownBounds);
            var info = new MonitorInfo { size=Marshal.SizeOf(typeof(MonitorInfo)) };
            using (new PhysicalCoordinates(new IntPtr(-4)))
                if (GetMonitorInfo(monitor,ref info))
                    return new Point((info.work.left+info.work.right)*.5,info.work.bottom-1);
            return pointer ?? new Point();
        }

        static bool TryButton(Bar bar,Dictionary<IntPtr,Rect> buttons,out Rect rect)
        {
            if (!buttons.TryGetValue(bar.Handle,out rect)) return false;
            rect.Offset(bar.ShownBounds.Left,bar.ShownBounds.Top);
            return bar.ShownBounds.Contains(Center(rect));
        }
        static Point Center(Rect bounds) { return new Point(bounds.X+bounds.Width*.5,bounds.Y+bounds.Height*.5); }
    }
}
