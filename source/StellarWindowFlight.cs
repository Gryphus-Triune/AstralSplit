using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AstralSplit
{
    // A click-through desktop surface lets the star leave the real HWND and reach the shell's icon.
    internal sealed class StellarWindowFlight : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct IconId { public int size; public IntPtr window; public uint id; public Guid guid; }
        [StructLayout(LayoutKind.Sequential)] struct Box { public int left,top,right,bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int size; public Box bounds,work; public int flags; }
        [StructLayout(LayoutKind.Sequential)] struct PixelPoint { public int x,y; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public PixelPoint reserved,maxSize,maxPosition,minTrack,maxTrack; }
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId id,out Box box);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PixelPoint point,uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr handle,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetStyle(IntPtr h,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetStyle(IntPtr h,int index,int style);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int key,ref int value,int bytes);
        internal sealed class HostCloak : IDisposable
        {
            IntPtr handle;
            HostCloak(IntPtr handle) { this.handle = handle; }

            internal static HostCloak Acquire(Window window) => AcquireHandle(new WindowInteropHelper(window).EnsureHandle());

            internal static HostCloak AcquireHandle(IntPtr handle)
            {
                if(handle==IntPtr.Zero)return null;
                int cloak = 1;
                return DwmSetWindowAttribute(handle,13,ref cloak,4)==0 ? new HostCloak(handle) : null;
            }
            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                int cloak = 0;
                DwmSetWindowAttribute(handle,13,ref cloak,4);
                handle = IntPtr.Zero;
            }
        }
        HostCloak cloak;
        readonly FlightDrawing picture;
        readonly Rect bounds;
        readonly Stopwatch elapsed = new Stopwatch();
        readonly Stopwatch landed = new Stopwatch(), finalFrameWait = new Stopwatch();
        readonly DispatcherTimer deadline;
        int duration;
        Action finish;
        readonly Action<bool?> contentVisibility;
        bool? lastVisible;
        internal event EventHandler FlightEnded;
        ScenePresenter scene;
        SceneDevice gpu;
        IntPtr overlay;
        bool armed;
        int direction=1;
        Window ownerWindow;
        bool ended,windowClosed,launched,waitingForFirstFrame;
        double reverseFrom=-1;

        internal static Point TrayPoint(Forms.NotifyIcon icon,Point? fallbackOrigin=null)
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var native=icon==null?null:typeof(Forms.NotifyIcon).GetField("window",fields)?.GetValue(icon) as Forms.NativeWindow;
            var number=icon==null?null:typeof(Forms.NotifyIcon).GetField("id",fields)?.GetValue(icon);
            if(native!=null && number is int id){
                var identifier=new IconId{size=Marshal.SizeOf(typeof(IconId)),window=native.Handle,id=(uint)id};
                Box r;
                if(Shell_NotifyIconGetRect(ref identifier,out r)==0 && r.right>r.left && r.bottom>r.top){
                    if(fallbackOrigin.HasValue && !new Rect(r.left,r.top,r.right-r.left,r.bottom-r.top).Contains(fallbackOrigin.Value))
                        return fallbackOrigin.Value;
                    int x=r.left+(r.right-r.left)/2,y=r.top+(r.bottom-r.top)/2;
                    foreach(var screen in Forms.Screen.AllScreens)if(screen.Bounds.Contains(x,y))return new Point(x,y);
                }
            }
            if(fallbackOrigin.HasValue)return fallbackOrigin.Value;
            var primary=Forms.Screen.PrimaryScreen.Bounds;
            return new Point(primary.Right-26,primary.Bottom-22);
        }

        internal static StellarWindowFlight Open(Window window,FrameworkElement surface,Forms.NotifyIcon tray,bool hiding,bool fromTray,
            HostCloak prepared,Action done,Action<bool?> contentVisibility=null,Point? destination=null,ScenePresenter scene=null)
        {
            if(!UiMotion.CanAnimate(surface) || SystemParameters.HighContrast || surface.ActualWidth<2 || surface.ActualHeight<2)return null;
            var origin=surface.PointToScreen(new Point());
            var far=surface.PointToScreen(new Point(surface.ActualWidth,surface.ActualHeight));
            var windowRect=new Rect(origin,far);
            if(windowRect.Width<2 || windowRect.Height<2)return null;
            Point target=destination ?? (hiding || fromTray?TrayPoint(tray):origin);
            var flight=new StellarWindowFlight(window,windowRect,target,surface,hiding,fromTray,done,contentVisibility);
            if(scene?.FitsFlight(windowRect)==true && (hiding || fromTray) && window.WindowState!=WindowState.Minimized){
                try{
                    flight.BeginMeteor(window,scene,prepared);
                    return flight;
                }catch(Exception ex) when(ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception ||
                    ex is ExternalException || ex is EntryPointNotFoundException || ex is OutOfMemoryException){
                    flight.ReleaseMeteor(); // Keep the incoming host cloaked for the WPF fallback.
                }
            }
            scene?.WpfFlight();
            return Start(flight,window,prepared);
        }

        void BeginMeteor(Window host,ScenePresenter renderer,HostCloak prepared){
            scene=renderer;scene.Flying=true;gpu=scene.ReadyDevice;ownerWindow=host;
            scene.Resume();
            overlay=ColdWindowLaunch.CreateOverlay(bounds);
            picture.StartMeteor(gpu,overlay,bounds,duration);
            cloak=prepared;
            elapsed.Start();
            deadline.Tick-=FallbackDeadline;
            deadline.Interval=TimeSpan.FromMilliseconds(8);
            deadline.Tick+=MeteorTick;
            deadline.Start();
            launched=true;
        }
        void MeteorTick(object sender,EventArgs e){
            try{AdvanceMeteor();}
            catch(Exception ex) when(ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is ExternalException){
                End(true);
            }
        }
        void AdvanceMeteor(){
            if(ended)return;
            int progress=gpu.Meteor();
            if(progress < -1 || elapsed.ElapsedMilliseconds>duration+1800 || SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation){
                End(true);return;
            }
            if(progress<0)return;
            if(!armed){
                ColdWindowLaunch.ShowOverlay(overlay);
                if(cloak==null)cloak=HostCloak.Acquire(ownerWindow);
                if(cloak==null){End(true);return;}
                gpu.Meteor(direction);armed=true;
            }
            picture.Time=progress/100000.0;
            UpdateContentVisibility();
            if(gpu.Landing==direction && (direction>0?progress==100000:progress==0))End(true);
        }
        void ReleaseMeteor(){
            if(gpu!=null){gpu.EndLaunch();gpu=null;}
            if(overlay!=IntPtr.Zero){ColdWindowLaunch.DestroyWindow(overlay);overlay=IntPtr.Zero;}
            if(scene!=null){scene.Flying=false;scene=null;}
        }
        void FallbackDeadline(object sender,EventArgs e){FinishAfterLanding();}

        internal static StellarWindowFlight OpenLifecycle(Window window,FrameworkElement surface,bool exiting,bool bodyVisible,
            HostCloak prepared,Action done,Action<bool?> contentVisibility,Point? hiddenOrigin=null,bool trayOnly=false)
        {
            if(!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)return null;
            bool drawBody=bodyVisible && UiMotion.CanAnimate(surface) && surface.ActualWidth>=2 && surface.ActualHeight>=2;
            if(!exiting && !drawBody)return null;

            var handle=new WindowInteropHelper(window).Handle; // A tray-only process may never have mapped its main HWND.
            var anchor=drawBody?null:hiddenOrigin;
            double scale;
            var monitor=LifecycleMonitor(handle,anchor,out scale);
            if(monitor.IsEmpty)return null;
            Point center=new Point(monitor.X+monitor.Width/2,monitor.Y+monitor.Height/2);
            Rect hostRect;
            if(drawBody){
                var origin=surface.PointToScreen(new Point());
                hostRect=new Rect(origin,surface.PointToScreen(new Point(surface.ActualWidth,surface.ActualHeight)));
                scale=hostRect.Width/surface.ActualWidth;
                center=new Point(hostRect.X+hostRect.Width/2,hostRect.Y+hostRect.Height/2);
            }else{
                if(!trayOnly && anchor.HasValue)center=anchor.Value;
                hostRect=new Rect(center,new Size());
            }
            Point? emitter=exiting && trayOnly ? (anchor ?? new Point(monitor.Right-24*scale,monitor.Bottom-20*scale)) : (Point?)null;
            var flight=new StellarWindowFlight(window,hostRect,center,drawBody?surface:null,exiting,false,done,contentVisibility,
                monitor,scale,emitter);
            return Start(flight,window,prepared);
        }

        internal static Rect LifecycleMonitor(IntPtr handle,Point? anchor,out double scale)
        {
            scale=1;
            IntPtr previous=IntPtr.Zero;
            try {
                try { previous=SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { }
                var monitor=anchor.HasValue ? MonitorFromPoint(new PixelPoint {
                    x=(int)Math.Round(anchor.Value.X),y=(int)Math.Round(anchor.Value.Y)},2) : MonitorFromWindow(handle,2);
                var info=new MonitorInfo{size=Marshal.SizeOf(typeof(MonitorInfo))};
                if(!GetMonitorInfo(monitor,ref info))return Rect.Empty;
                try {
                    uint x,y;
                    if(GetDpiForMonitor(monitor,0,out x,out y)==0 && x>0)scale=x/96.0;
                }catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
                var r=info.bounds;
                return new Rect(r.left,r.top,r.right-r.left,r.bottom-r.top);
            }finally{if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
        }

        static StellarWindowFlight Start(StellarWindowFlight flight,Window window,HostCloak prepared)
        {
            bool started=false;
            try { started=flight.Launch(window,prepared); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
            catch(ExternalException) { }
            finally { if(!started)flight.Cancel(); }
            return started?flight:null;
        }

        StellarWindowFlight(Window owner,Rect rect,Point target,FrameworkElement surface,bool hiding,bool fromTray,Action done,Action<bool?> contentVisibility,
            Rect? lifecycleScreen=null,double pixelScale=1,Point? emitter=null)
        {
            duration=hiding?820:fromTray?900:400;finish=done;this.contentVisibility=contentVisibility;
            var area=lifecycleScreen ?? rect;
            if(!lifecycleScreen.HasValue){
                if(hiding || fromTray)area.Union(new Rect(target.X-45,Math.Min(target.Y-48,rect.Top-5),90,Math.Abs(target.Y-Math.Min(target.Y-48,rect.Top-5))+96));
                area.Inflate(5,5);
                var desktop = Forms.SystemInformation.VirtualScreen;
                var visible = Rect.Intersect(area,new Rect(desktop.X,desktop.Y,desktop.Width,desktop.Height));
                if (!visible.IsEmpty && visible.Width >= 1 && visible.Height >= 1) area = visible;
            }
            bounds=new Rect(Math.Floor(area.Left),Math.Floor(area.Top),Math.Ceiling(area.Right)-Math.Floor(area.Left),Math.Ceiling(area.Bottom)-Math.Floor(area.Top));
            var corner=new Vector(bounds.X,bounds.Y);
            rect.Offset(-corner.X,-corner.Y);
            Rect? monitor=lifecycleScreen.HasValue?new Rect(new Point(),bounds.Size):(Rect?)null;
            picture=new FlightDrawing(rect,target-corner,bounds.TopLeft,surface,hiding,fromTray,monitor,pixelScale,emitter-corner);
            if(picture.IsLifecycle){
                duration=emitter.HasValue?StellarSingularity.TrayExitMilliseconds:hiding?StellarSingularity.ExitMilliseconds:StellarSingularity.OpenMilliseconds;
                duration=(int)Math.Round(duration*(1-picture.StartTime));
            }
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
            ShowInTaskbar=false;ShowActivated=false;Focusable=false;Topmost=true;IsHitTestVisible=false;
            WindowStartupLocation=WindowStartupLocation.Manual;Width=Height=1;Left=owner.Left;Top=owner.Top;
            Content=picture;
            deadline=new DispatcherTimer(DispatcherPriority.Render,Dispatcher){Interval=TimeSpan.FromMilliseconds(duration+35)};
            deadline.Tick+=FallbackDeadline;
            Closed+=(s,e)=>{windowClosed=true;End(launched && picture.IsLifecycle && picture.Hiding);};
        }
        bool Launch(Window host,HostCloak prepared)
        {
            picture.PrepareProjection();
            var h=new WindowInteropHelper(this).EnsureHandle();
            HwndSource.FromHwnd(h).AddHook(AllowDesktopSpan);
            SetStyle(h,-20,GetStyle(h,-20)|0x08000000|0x80|0x20);
            Show();
            if(!SetWindowPos(h,new IntPtr(-1),(int)bounds.X,(int)bounds.Y,(int)bounds.Width,(int)bounds.Height,0x0010))return false;
            UpdateLayout();
            cloak=prepared;
            if(picture.HasBody && cloak==null){
                cloak=HostCloak.Acquire(host);
                if(cloak==null)return false;
            }
            else if(cloak==null && picture.IsLifecycle && picture.Hiding){
                // A shell restore during exit must not remap a previously hidden host.
                // Reading Handle leaves a never-shown tray instance entirely windowless.
                cloak=HostCloak.AcquireHandle(new WindowInteropHelper(host).Handle);
            }
            UpdateContentVisibility();
            launched=true;
            waitingForFirstFrame=true;
            if(!waitingForFirstFrame)elapsed.Start();
            UiFrames.Rendering+=Frame;deadline.Start();return true;
        }
        IntPtr AllowDesktopSpan(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
        {
            // A flight between monitors can need a larger surface than a normal window.
            if (message == 0x0024 && lp != IntPtr.Zero) // WM_GETMINMAXINFO
            {
                var limits = (MinMaxInfo)Marshal.PtrToStructure(lp,typeof(MinMaxInfo));
                if (limits.maxTrack.x < bounds.Width || limits.maxTrack.y < bounds.Height)
                {
                    limits.maxTrack.x = Math.Max(limits.maxTrack.x,(int)bounds.Width);
                    limits.maxTrack.y = Math.Max(limits.maxTrack.y,(int)bounds.Height);
                    Marshal.StructureToPtr(limits,lp,false);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }
        void Frame(object sender,EventArgs e)
        {
            if(!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast){End(true);return;}
            if(picture.IsLifecycle && finalFrameWait.IsRunning){FinishAfterLanding();return;}
            if(waitingForFirstFrame){
                if(picture.renderedTime<0)return;
                waitingForFirstFrame=false;
                elapsed.Start();
                deadline.Stop();deadline.Start();
            }
            if(!picture.IsLifecycle)ObserveLanding();
            double t=elapsed.Elapsed.TotalMilliseconds/duration;
            if(t>=1){FinishAfterLanding();return;}
            picture.Time=reverseFrom<0?picture.StartTime+(1-picture.StartTime)*t:reverseFrom*(1-t)*(1-t);
            UpdateContentVisibility();
            picture.InvalidateVisual();
        }

        void ObserveLanding()
        {
            if (!landed.IsRunning && picture.renderedTime >= FlightDrawing.ArrivalTime) landed.Start();
        }

        void FinishAfterLanding()
        {
            if(picture.IsLifecycle){
                if(picture.renderedTime>=1 || finalFrameWait.ElapsedMilliseconds>=350 ||
                    !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast){End(true);return;}
                if(!finalFrameWait.IsRunning){
                    waitingForFirstFrame=false;
                    finalFrameWait.Start();
                    picture.Time=1;picture.InvalidateVisual();UpdateContentVisibility();
                    deadline.Interval=TimeSpan.FromMilliseconds(50);
                }
                return;
            }
            ObserveLanding();
            if (!picture.Hiding || reverseFrom >= 0 || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast ||
                landed.ElapsedMilliseconds >= 56 || finalFrameWait.ElapsedMilliseconds >= 650)
            {
                End(true);
                return;
            }
            // The shell can occupy the dispatcher through the last part of the flight.
            // Give the actual head a frame at the icon before retiring the overlay.
            if (finalFrameWait.IsRunning) return;
            finalFrameWait.Start();
            picture.Time=1;
            picture.InvalidateVisual();
            UpdateContentVisibility();
            deadline.Interval=TimeSpan.FromMilliseconds(70);
        }
        void UpdateContentVisibility()
        {
            bool visible=picture.SurfaceOpacity>0;
            if(lastVisible==visible)return;
            lastVisible=visible;contentVisibility?.Invoke(visible);
        }
        internal bool ReturnToWindow(Action restored)
        {
            if(RetargetMeteor(false,restored))return true;
            if(ended || picture.IsLifecycle || !picture.Hiding || reverseFrom>=0)return false;
            reverseFrom=picture.Time;finish=restored;
            duration=(int)Math.Max(100,Math.Min(420,reverseFrom*660));
            elapsed.Restart();deadline.Stop();deadline.Interval=TimeSpan.FromMilliseconds(duration+35);deadline.Start();
            return true;
        }
        internal bool RetargetMeteor(bool hide,Action done){
            if(ended || gpu==null)return false;
            direction=hide==picture.Hiding?1:-1;
            finish=done;elapsed.Restart();
            if(armed)gpu.Meteor(direction);
            return true;
        }
        internal void Cancel(){End(false);}
        void End(bool complete)
        {
            if(ended)return;ended=true;deadline.Stop();UiFrames.Rendering-=Frame;
            var callback=finish;finish=null;
            try { if(complete)callback?.Invoke(); }
            finally {
                cloak?.Dispose();
                ReleaseMeteor();
                // Native flights never show this WPF wrapper, but it still
                // belongs to Application.Windows until it is closed.
                if(!windowClosed)Close();
                picture.Release();
                if(lastVisible.HasValue)contentVisibility?.Invoke(null);
                FlightEnded?.Invoke(this,EventArgs.Empty);
            }
        }

        sealed class FlightDrawing : FrameworkElement
        {
            readonly Rect rect;readonly Point screenOrigin,center,target,control;readonly FrameworkElement bodySurface; VisualBrush liveSurface;readonly bool hide,fromTray;
            readonly StellarSingularity singularity;
            readonly Point? trayOrigin;
            MatrixTransform projection;
            readonly double[] angle=new double[46],radius=new double[46],grain=new double[46];
            internal double Time;
            internal double renderedTime = -1;
            internal const double ArrivalTime = .89;
            internal bool Hiding {get{return hide;}}
            internal bool IsLifecycle => singularity!=null;
            internal bool HasBody => bodySurface!=null;
            internal double StartTime => IsLifecycle && hide && !HasBody && !trayOrigin.HasValue ? StellarSingularity.BurstStart : 0;
            static readonly Brush gold=Halo(255,184,108),ice=Halo(154,205,255),white=Halo(255,248,234);
            static readonly Pen[] trail=TrailPens();
            internal FlightDrawing(Rect area,Point end,Point screenOrigin,FrameworkElement surface,bool hiding,bool returning,
                Rect? monitor,double pixelScale,Point? emitter)
            {
                rect=area;this.screenOrigin=screenOrigin;target=end;hide=hiding;fromTray=returning;center=new Point(rect.X+rect.Width/2,rect.Y+rect.Height/2);
                // Project the existing visual tree. The cloaked HWND keeps its original
                // layout, input owner and animation clocks throughout the handoff.
                bodySurface=surface;
                RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.Linear);
                trayOrigin=emitter;
                if(monitor.HasValue){
                    singularity=new StellarSingularity(monitor.Value,center,pixelScale,hiding && surface!=null ? rect : Rect.Empty);
                    Time=StartTime;
                    return;
                }
                control=new Point(target.X+Math.Sign(target.X-center.X)*32,Math.Min(center.Y-48,(center.Y+target.Y)/2));
                var rand=new Random(701);
                for(int i=0;i<angle.Length;i++){angle[i]=rand.NextDouble()*Math.PI*2;radius[i]=0.26+0.41*rand.NextDouble();grain[i]=0.75+rand.NextDouble()*2.1;}
            }
            internal void PrepareProjection(){
                if(bodySurface!=null)liveSurface=new VisualBrush(bodySurface){AutoLayoutContent=false,Stretch=Stretch.Fill,
                    ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(bodySurface.RenderSize)};
            }
            internal void StartMeteor(SceneDevice gpu,IntPtr overlay,Rect bounds,int duration){
                var seeds=new float[12*grain.Length];
                for(int i=0;i<grain.Length;i++){
                    seeds[12*i]=(float)angle[i];seeds[12*i+1]=(float)radius[i];seeds[12*i+2]=(float)grain[i];
                }
                var args=new[]{(float)bounds.Width,(float)bounds.Height,(float)center.X,(float)center.Y,
                    (float)gpu.Width,(float)gpu.Height,(float)Math.Max(.8,Math.Min(2.4,rect.Width/760)),
                    hide?-1f:-2f,(float)target.X,(float)target.Y};
                gpu.BeginMeteor(overlay,args,seeds,duration);
            }
            internal void Release(){if(liveSurface!=null)liveSurface.Visual=null;}
            double SurfaceScale
            {
                get{
                    if(IsLifecycle)return StellarSingularity.WindowScale(Time,hide);
                    if(hide)return Math.Pow(1-UiMotion.Unit(Time/.62),1.7);
                    double start=fromTray?.4:.12,v=UiMotion.Unit((Time-start)/(1-start))-1;
                    return Math.Max(0,1+2.3*v*v*v+1.3*v*v);
                }
            }
            internal double SurfaceOpacity{get{
                if(!HasBody)return 0;
                if(IsLifecycle)return StellarSingularity.WindowOpacity(Time,hide);
                return UiMotion.Unit((SurfaceScale-.025)/.30);
            }}
            static Brush Halo(byte r,byte g,byte b){
                var brush=new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(r,g,b),0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(112,r,g,b),.23));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0,r,g,b),1));brush.Freeze();return brush;
            }
            static Pen[] TrailPens(){
                var all=new Pen[24];for(int i=0;i<all.Length;i++){double q=(i+1)/24.0;
                    all[i]=new Pen(new SolidColorBrush(Color.FromArgb((byte)(214*q*q),255,(byte)(170+77*q),(byte)(97+136*q))),.5+4*q);
                    all[i].StartLineCap=all[i].EndLineCap=PenLineCap.Round;all[i].Freeze();}
                return all;
            }
            static double OutgoingDistance(double t)
            {
                double phase=UiMotion.Unit(t/ArrivalTime);
                const double join=.70;
                if (phase<=join) return Math.Pow(phase,2.3);
                // Cubic Hermite joins the accelerating arc with its current velocity and
                // slows it to zero at the icon. The remaining time holds that exact point.
                double q=(phase-join)/(1-join), rest=1-q;
                double start=Math.Pow(join,2.3), tangent=2.3*Math.Pow(join,1.3)*(1-join);
                return start+(1-start)*q*q*(3-2*q)+tangent*q*rest*rest;
            }
            Point Along(double t){
                double e=fromTray?Math.Pow(1-UiMotion.Unit(t/.46),2.4):OutgoingDistance(t),u=1-e;
                return new Point(u*u*center.X+2*u*e*control.X+e*e*target.X,u*u*center.Y+2*u*e*control.Y+e*e*target.Y);
            }
            protected override void OnRender(DrawingContext d)
            {
                double t=UiMotion.Unit(Time),s=SurfaceScale;
                // The trajectory is in physical desktop pixels. Clamping an overlay at a
                // monitor edge must clip its margins, never rescale the path or its landing point.
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;
                var transform = source.CompositionTarget.TransformFromDevice;
                Vector offset = transform.Transform(screenOrigin - PointToScreen(new Point()));
                transform.Translate(offset.X,offset.Y);
                if (projection == null || projection.Matrix != transform)
                {
                    projection = new MatrixTransform(transform);
                    projection.Freeze();
                }
                d.PushTransform(projection);
                if(SurfaceOpacity>0){
                    var at=hide && !IsLifecycle?Along(t):center;
                    d.PushOpacity(SurfaceOpacity);
                    d.PushTransform(new ScaleTransform(s,s,at.X,at.Y));
                    var area=new Rect(at.X-rect.Width/2,at.Y-rect.Height/2,rect.Width,rect.Height);
                    d.DrawRoundedRectangle(liveSurface,null,area,10,10);d.Pop();d.Pop();
                }
                if(IsLifecycle){
                    if(trayOrigin.HasValue)singularity.DrawTrayExit(d,t,trayOrigin.Value);
                    else singularity.Draw(d,t,hide);
                    d.Pop();renderedTime=t;return;
                }
                double unit=Math.Max(.8,Math.Min(2.4,rect.Width/760));
                if(hide || (fromTray && t<.62)){
                    double strength=hide?UiMotion.Unit((1-s)*1.5):UiMotion.Unit((.62-t)/.16);d.PushOpacity(strength);
                    Point previous=Along(t-.19);
                    for(int i=0;i<trail.Length;i++){var next=Along(t-.19+.19*(i+1)/trail.Length);d.DrawLine(trail[i],previous,next);previous=next;}
                    var head=Along(t);
                    d.DrawEllipse(gold,null,head,30*unit,30*unit);d.DrawEllipse(white,null,head,7*unit,7*unit);d.Pop();
                }
                if(!hide){
                    double glow=Math.Sin(Math.PI*t),remain=Math.Pow(1-t,1.58);
                    d.PushOpacity(glow);
                    for(int i=0;i<angle.Length;i++){
                        double a=angle[i]+(1-t)*1.5, r=remain*radius[i];
                        var p=new Point(center.X+Math.Cos(a)*r*rect.Width,center.Y+Math.Sin(a)*r*rect.Height);
                        double z=grain[i]*unit;d.DrawEllipse(i%4==0?ice:gold,null,p,3.6*z,3.6*z);d.DrawEllipse(white,null,p,z,z);
                    }
                    d.DrawEllipse(gold,null,center,42*glow*unit,42*glow*unit);d.DrawEllipse(white,null,center,10*glow*unit,10*glow*unit);d.Pop();
                    if(fromTray && t>.46){
                        double wave=(t-.46)/.54, radius=(6+130*wave)*unit;
                        d.PushOpacity(.65*(1-wave));d.DrawEllipse(null,arrivalRing,center,radius,radius);d.Pop();
                    }
                }
                d.Pop();
                renderedTime=t;
            }
            static readonly Pen arrivalRing=MakeRing();
            static Pen MakeRing(){var pen=new Pen(Brushes.Bisque,1.4);pen.Freeze();return pen;}
        }
    }
}
