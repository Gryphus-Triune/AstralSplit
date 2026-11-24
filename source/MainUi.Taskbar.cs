using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;


namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        WindowState taskbarRestoreState = WindowState.Normal;
        bool wasIconic, landingOnTaskbar, committingTaskbarMinimize;
        Point? preparedTaskbarPoint;
        IntPtr taskbarHost, minimizeHook;
        NativeWindowHook minimizeCallback;
        DispatcherOperation pendingNativeMinimize;

        delegate IntPtr NativeWindowHook(int code, IntPtr window, IntPtr operation);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", EntryPoint="SetWindowsHookExW")]
        static extern IntPtr SetWindowsHookEx(int kind, NativeWindowHook callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr window, IntPtr operation);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        void AttachTaskbarInput(HwndSource source)
        {
            if (source == null) return;
            source.AddHook(TaskbarWindowMessage);
            StartTaskbarTargetTracking();
            taskbarHost = source.Handle;
            minimizeCallback = BeforeNativeMinimize;
            const int threadCbtHook = 5;
            minimizeHook = SetWindowsHookEx(threadCbtHook, minimizeCallback, IntPtr.Zero, GetCurrentThreadId());
        }

        void DetachTaskbarInput(HwndSource source)
        {
            CancelPendingTaskbarMinimize();
            taskbarHost = IntPtr.Zero;
            if (minimizeHook != IntPtr.Zero) UnhookWindowsHookEx(minimizeHook);
            minimizeHook = IntPtr.Zero;
            source?.RemoveHook(TaskbarWindowMessage);
        }

        void CancelPendingTaskbarMinimize()
        {
            pendingNativeMinimize?.Abort();
            pendingNativeMinimize = null;
        }

        IntPtr BeforeNativeMinimize(int code, IntPtr window, IntPtr operation)
        {
            const int aboutToMinimizeOrMaximize = 1;
            if (code == aboutToMinimizeOrMaximize && window == taskbarHost && !committingTaskbarMinimize)
            {
                int show = (int)(operation.ToInt64() & 0xffff);
                const int showMinimized = 2, minimize = 6, showMinimizedWithoutActivation = 7;
                if ((show == showMinimized || show == minimize || show == showMinimizedWithoutActivation) &&
                    !closed && Window.IsVisible && Window.WindowState != WindowState.Minimized &&
                    SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && !Window.Dispatcher.HasShutdownStarted)
                {
                    // Explorer may minimize the HWND directly without sending SC_MINIMIZE.
                    // Defer creating the overlay until the native show-state call has unwound.
                    if (!exitInProgress && !hidingToTray && pendingNativeMinimize == null)
                    {
                        Point landing = TaskbarPoint(true);
                        pendingNativeMinimize = Window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => {
                            pendingNativeMinimize = null;
                            MinimizeToTaskbarAt(landing);
                        }));
                    }
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(minimizeHook, code, window, operation);
        }

        void MinimizeToTaskbar() { MinimizeToTaskbarAt(TaskbarPoint()); }

        void MinimizeToTaskbarAt(Point landing)
        {
            if(closed || exitInProgress || !Window.IsVisible || Window.WindowState==WindowState.Minimized || hidingToTray)return;
            taskbarRestoreState=Window.WindowState;
            PlayFlightTo(true,false,landing);
        }

        void CommitTaskbarMinimize()
        {
            committingTaskbarMinimize=true;
            try { Window.WindowState=WindowState.Minimized; }
            finally { committingTaskbarMinimize=false; }
        }

        void PrepareTaskbarReveal()
        {
            if(exitInProgress || pendingReveal!=null || windowFlight!=null || !Window.IsVisible)return;
            PrepareWindowReveal(true);
            if(pendingReveal!=null)preparedTaskbarPoint=TaskbarPoint(true);
        }

        void TaskbarStateChanged(object sender,EventArgs e)
        {
            if (exitInProgress) return;
            if(Window.WindowState==WindowState.Minimized){wasIconic=true;return;}
            taskbarRestoreState=Window.WindowState;
            if(!wasIconic)return;
            wasIconic=false;
            if (landingOnTaskbar && ReverseOutgoingFlight()) return;
            if(closed || !Window.IsVisible || windowFlight!=null)return;
            PrepareTaskbarReveal();
            // WPF restores its layout during WM_SIZE. Start the projection with those final bounds.
            if(pendingReveal!=null)Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(StartPreparedReveal));
        }

        IntPtr TaskbarWindowMessage(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
        {
            if(closed || !Window.IsVisible || committingTaskbarMinimize)return IntPtr.Zero;
            const int systemCommand=0x0112, queryOpen=0x0013, positionChanging=0x0046, minimize=0xF020, restore=0xF120;
            int command=(int)(wp.ToInt64() & 0xFFF0);
            if (exitInProgress && (message == queryOpen || message == systemCommand && (command == minimize || command == restore)))
            {
                handled = true;
                return IntPtr.Zero;
            }
            if(message==systemCommand && command==minimize && Window.WindowState!=WindowState.Minimized)
            {
                handled=true;
                MinimizeToTaskbarAt(TaskbarPoint(true,lp));
            }
            else if (message == systemCommand && command == restore && Window.WindowState != WindowState.Minimized &&
                (pendingNativeMinimize != null || landingOnTaskbar))
            {
                CancelPendingTaskbarMinimize();
                ReverseOutgoingFlight();
                handled = true;
            }
            else if (message == positionChanging && !hidingToTray && !wasIconic &&
                Window.WindowState != WindowState.Minimized && IsIconic(hwnd) && UiMotion.CanAnimate(surface) && !SystemParameters.HighContrast)
            {
                // Synchronous cross-thread minimization bypasses the owner's CBT hook. The HWND
                // is already iconic, but its client origin and WPF layout still have their old bounds.
                taskbarRestoreState = Window.WindowState;
                PlayFlightTo(true, false, TaskbarPoint(true), true);
            }
            else if((message==queryOpen || (message==systemCommand && command==restore)) &&
                Window.WindowState==WindowState.Minimized)PrepareTaskbarReveal();
            return IntPtr.Zero;
        }
    }
}
