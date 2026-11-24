using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AstralSplit
{
    // The main HWND remains cloaked until its own live image has finished expanding.
    // This HWND supplies only the transparent area outside the final window bounds.
    internal sealed class ColdWindowLaunch : IDisposable
    {
        readonly Window window;
        readonly FrameworkElement surface;
        readonly ScenePresenter presenter;
        readonly Action<bool> completed;
        readonly DispatcherTimer timer;
        readonly Stopwatch clock=Stopwatch.StartNew();
        SceneDevice graphics;
        IntPtr overlay;
        bool disposed,shown;

        internal ColdWindowLaunch(Window window,FrameworkElement surface,ScenePresenter presenter,Action<bool> completed)
        {
            this.window=window;this.surface=surface;this.presenter=presenter;this.completed=completed;
            timer=new DispatcherTimer(DispatcherPriority.Render,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(12)};
            timer.Tick+=Advance;
            presenter.FirstFrameReady+=Advance;
            timer.Start();
        }
        void Advance(object sender,EventArgs e)
        {
            if(disposed)return;
            if(!window.IsVisible || window.WindowState==WindowState.Minimized || SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation){completed(false);return;}
            try
            {
                if(graphics==null)
                {
                    var ready=presenter.ReadyDevice;
                    if(ready!=null && Start(ready))return;
                    if(presenter.Failure!=null || clock.ElapsedMilliseconds>2200)completed(false);
                    return;
                }
                // If a monitor or device goes away mid-flight, reveal the regular
                // window directly; a second intake of particles would replay the birth.
                if(graphics!=presenter.ReadyDevice || clock.ElapsedMilliseconds>StellarSingularity.OpenMilliseconds+1400){completed(shown);return;}
                if(graphics.LaunchFinished)completed(true);
            }
            catch(Exception ex) when(ex is ExternalException || ex is InvalidOperationException || ex is Win32Exception ||
                                    ex is EntryPointNotFoundException || ex is DllNotFoundException || ex is OutOfMemoryException)
            {
                Trace.WriteLine("AstralSplit startup surface: "+ex.Message);completed(shown);
            }
        }
        bool Start(SceneDevice ready)
        {
            var hwnd=new WindowInteropHelper(window).Handle;
            double monitorScale;
            var monitor=StellarWindowFlight.LifecycleMonitor(hwnd,null,out monitorScale);
            if(monitor.IsEmpty)throw new InvalidOperationException("The startup monitor is unavailable.");
            Point corner=surface.PointToScreen(new Point());
            Point physical=surface.PointToScreen(new Point(surface.ActualWidth,surface.ActualHeight));
            var body=new Rect(corner,physical);
            if(!presenter.FitsFlight(body))return false;
            double scale=body.Width/surface.ActualWidth;
            var center=new Point(body.Left-monitor.Left+body.Width/2,body.Top-monitor.Top+body.Height/2);
            var area=new Rect(new Point(),monitor.Size);
            var seeds=new StellarSingularity(area,center,scale,body).OpeningGrains();
            var bounds=new[]{(float)monitor.Width,(float)monitor.Height,(float)center.X,(float)center.Y,
                (float)ready.Width,(float)ready.Height,(float)scale,0f};
            overlay=CreateOverlay(monitor);
            graphics=ready;
            presenter.Flying=true;
            graphics.BeginLaunch(overlay,bounds,seeds);
            ShowOverlay(overlay);
            shown=true;
            clock.Restart();
            return true;
        }
        internal static IntPtr CreateOverlay(Rect monitor)
        {
            const uint popup=0x80000000, disabled=0x08000000;
            const uint noRedirection=0x00200000, noActivate=0x08000000, toolWindow=0x80, transparent=0x20;
            var overlay=CreateWindowEx(noRedirection|noActivate|toolWindow|transparent,"STATIC","",popup|disabled,
                (int)monitor.X,(int)monitor.Y,(int)monitor.Width,(int)monitor.Height,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(overlay==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            return overlay;
        }
        internal static void ShowOverlay(IntPtr overlay){
            if(!SetWindowPos(overlay,new IntPtr(-1),0,0,0,0,0x0053))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            if(disposed)return;
            disposed=true;timer.Stop();timer.Tick-=Advance;presenter.FirstFrameReady-=Advance;
            if(graphics!=null){graphics.EndLaunch();presenter.Flying=false;graphics=null;}
            if(overlay!=IntPtr.Zero){DestroyWindow(overlay);overlay=IntPtr.Zero;}
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,
            IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
        [DllImport("user32.dll")]internal static extern bool DestroyWindow(IntPtr hwnd);
    }
}
