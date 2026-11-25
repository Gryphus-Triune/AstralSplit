using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        bool zen,eclipse;
        AppRow illuminatedRow;
        FrameworkElement[] folding;
        Border observingInput;
        Button observatoryButton;
        WindowState savedZenState;
        Rect savedZenBounds;
        ResizeMode savedZenResize;
        Point? movingOrigin;
        long moveStamp;
        Button[] moonButtons;
        // The desktop reveals a region of one monitor-sized drawing surface.
        const double ZenFadeSeconds=.14, ZenTravelSeconds=.40;
        double zenTime,zenExpansion,zenHomeScale,zenHomeExpansion,zenHomeHeight;
        TimeSpan? zenStamp;
        bool zenPlaced,zenTravel,zenPlaying,zenChangingHost;
        Rect zenWindow,zenHome,zenMonitor,zenStage,zenChartPixels,zenFromChart,zenToChart;
        Point zenSkyPixels,zenFocusPixels,zenFromFocus;
        double zenHomeDpi,zenScaleRatio;
        Rect lastZenFrame=Rect.Empty;
        int lastZenRadius=-1;
        IntPtr zenHomeRegion;
        Vector zenHomeOffset;
        TranslateTransform zenButtonShift;
        [StructLayout(LayoutKind.Sequential)] struct ZenRect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct ZenPoint {public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] struct ZenPlacement {
            public int Length,Flags,Show; public ZenPoint Minimum,Maximum; public ZenRect Normal;
        }
        ZenPlacement zenRestore;
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd,ref ZenPlacement placement);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hwnd,ref ZenPlacement placement);
        [DllImport("user32.dll",EntryPoint="GetWindowRect")] static extern bool ReadZenFrame(IntPtr hwnd,out ZenRect frame);

        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr hwnd,IntPtr region);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

        void BindObservatory(){
            master.GesturePreview+=sky.Gesture;
            sky.GlassMotion+=Find<LiquidSheet>("StatusFrost").WindowLight;
            sky.GlassMotion+=Find<LiquidSheet>("ListFrost").WindowLight;
            sky.GlassWave+=(gate,radius,power)=>{
                Illuminate(statusGlass,gate,radius,power);
                Illuminate(listGlass,gate,radius,power);
            };
            folding=new FrameworkElement[]{Find<Grid>("ObservatoryCaption"),Find<StackPanel>("HeaderStack"),Find<Grid>("ListInteractionHost")};
            observingInput=Find<Border>("ObservatoryInput");
            observatoryButton=Find<Button>("ObservatoryButton");
            zenButtonShift=new TranslateTransform();
            observatoryButton.RenderTransform=zenButtonShift;
            ((ObservatoryGlassButton)observatoryButton).Switch=master;
            observatoryButton.Click+=(s,e)=>SetZen(!zen);
            BindSkyClicks(observingInput,sky);
            Window.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Escape && ((ObservatoryGlassButton)observatoryButton).CancelPress())e.Handled=true;
            };
            Window.IsVisibleChanged+=(s,e)=>ContinueZen();
            Window.StateChanged+=(s,e)=>ContinueZen();
            Window.Closed+=(s,e)=>{
                StopComposition();StopZenClock();
                if(zenHomeRegion!=IntPtr.Zero){DeleteObject(zenHomeRegion);zenHomeRegion=IntPtr.Zero;}
            };
            sky.Inspected+=path=>{
                var next=path!=null && byPath.TryGetValue(path,out var app)?app:null;
                sky.SurveyRowIcon=null;
                if(next!=null && applications.ItemContainerGenerator.ContainerFromItem(next) is FrameworkElement row){
                    var at=new Point(24,row.ActualHeight*.5);
                    var local=row.TranslatePoint(at,applications);
                    if(new Rect(applications.RenderSize).Contains(local))sky.SurveyRowIcon=row.TranslatePoint(at,sky);
                }
                if(next==illuminatedRow)return;illuminatedRow?.FocusStar(false);illuminatedRow=next;next?.FocusStar(true);
            };
            applications.MouseMove+=(s,e)=>{
                var source=e.OriginalSource as DependencyObject;
                var item=source==null?null:ItemsControl.ContainerFromElement(applications,source) as ListBoxItem;
                sky.AimFromRow((item?.DataContext as AppRow)?.Path,item==null?new Point():item.TranslatePoint(new Point(24,0),sky));
            };
            applications.MouseLeave+=(s,e)=>sky.AimFromRow(null,new Point());
            var input=Find<Border>("SkyInput");
            input.PreviewMouseLeftButtonDown+=(s,e)=>{
                var hit=sky.ApplicationAt(e.GetPosition(sky),null);
                if(hit.HasValue){LocateApplication(hit.Value.Path);ShowAppConnections(hit.Value.Path);e.Handled=true;}
            };
            observingInput.PreviewMouseLeftButtonDown+=(s,e)=>{
                var hit=sky.ApplicationAt(e.GetPosition(sky),null);
                if(hit.HasValue){ShowAppConnections(hit.Value.Path);e.Handled=true;}
            };
            moonButtons=new[]{Find<Button>("HideToTray"),Find<Button>("Minimize"),Find<Button>("ZoomWindow")};
            foreach(var button in moonButtons)button.ApplyTemplate();
            ShowCaptionMarks(captionButtons.IsMouseOver);
        }
        void Illuminate(GateGlassPanel pane,Point gate,double radius,double power){
            if(!pane.IsArrangeValid || sky.FindCommonVisualAncestor(pane)==null)return;
            pane.Receive(sky,sky.TranslatePoint(gate,pane),radius,power);
        }
        void ShowEclipses(bool active){
            if(eclipse==active || moonButtons==null)return;eclipse=active;ShowCaptionMarks(captionButtons.IsMouseOver);
        }
        void LocateApplication(string path){
            if(path==null || !byPath.TryGetValue(path,out var row))return;
            if(selectedOnly && !row.Selected)SetFilter(false);
            else if(!selectedOnly && row.Count==0 && row.Selected)SetFilter(true);
            if(!Matches(row,search.Text.Trim()) && search.Text.Length>0)search.Clear();
            Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
                int index=visibleRows.IndexOf(row);if(index<0 || closed || listScroller==null)return;
                var item=applications.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
                double top=item==null?index*52.0:listScroller.VerticalOffset+item.TranslatePoint(new Point(),listScroller).Y;
                listScroller.Locate(top-(listScroller.ViewportHeight-52)*.50);
            }));
        }
        void SetZen(bool value){
            if(value==zen || closed || value && contrastTheme)return;
            if(value && !zenPlaced){
                var hwnd=new WindowInteropHelper(Window).Handle;
                if(hwnd==IntPtr.Zero || !ReadZenFrame(hwnd,out var r) || r.Right<=r.Left || r.Bottom<=r.Top)return;
                zenWindow=new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);
                var monitor=System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                zenMonitor=new Rect(monitor.Left,monitor.Top,monitor.Width,monitor.Height);
                savedZenState=Window.WindowState;savedZenResize=Window.ResizeMode;savedZenBounds=Window.RestoreBounds;
                zenRestore=new ZenPlacement{Length=Marshal.SizeOf(typeof(ZenPlacement))};
                if(!GetWindowPlacement(hwnd,ref zenRestore))zenRestore.Length=0;
                zenHome=zenWindow;
                if(savedZenState==WindowState.Maximized)zenHome.Intersect(zenMonitor);
                zenStage=Rect.Union(zenHome,zenMonitor);
                Window.UpdateLayout();
                var chart=mapViewport.TransformToVisual(sky).TransformBounds(new Rect(mapViewport.RenderSize));
                zenChartPixels=new Rect(sky.PointToScreen(chart.TopLeft),sky.PointToScreen(chart.BottomRight));
                var focus=sky.ObservationFocus;
                if(!UiMotion.IsFinite(focus.X) || !UiMotion.IsFinite(focus.Y))
                    focus=new Point(chart.Left+.834*chart.Width,chart.Top+.405*chart.Height);
                zenFocusPixels=sky.PointToScreen(focus);
                zenSkyPixels=sky.PointToScreen(new Point());
                zenHomeDpi=VisualTreeHelper.GetDpi(sky).DpiScaleX;
                zenHomeScale=stellarCoreScale;zenHomeExpansion=sky.ViewportExpansion;zenHomeHeight=expandedChartHeight;
                zenHomeRegion=CreateRectRgn(0,0,0,0);
                if(zenHomeRegion!=IntPtr.Zero && GetWindowRgn(hwnd,zenHomeRegion)==0){
                    DeleteObject(zenHomeRegion);zenHomeRegion=IntPtr.Zero;
                }
                zenPlaced=true;
                // Drop the system material while the host is still at its original bounds.
                ApplyWindowBackdrop();
                sky.PrepareObservationPhoto(LocalFrame(zenMonitor));
            }
            zen=value;sky.HoldObservationAim(true);
            observatoryButton.Content=value?"退出观测":"星海观测";
            observatoryButton.IsEnabled=zen || !contrastTheme;
            observingInput.Visibility=Visibility.Visible;
            foreach(var part in folding)part.IsHitTestVisible=false;
            if(value){CloseMilestoneMenu();sky.Inspect(null);sky.AimFromRow(null,new Point());UpdateStellarApplications();}
            if(Window.IsActive && Window.IsVisible)observatoryButton.Focus();
            StartZenClock();
        }
        double ZenDestination {get{return zen?ZenFadeSeconds+ZenTravelSeconds:0;}}
        void StartZenClock(){
            if(!SystemParameters.ClientAreaAnimation || contrastTheme || !UiMotion.CanAnimate(observatoryButton)){
                StopZenClock();PaintZen(ZenDestination);return;
            }
            if(!zenPlaying){zenStamp=null;UiFrames.Rendering+=ZenFrame;zenPlaying=true;}
        }
        void StopZenClock(){
            if(!zenPlaying)return;UiFrames.Rendering-=ZenFrame;zenPlaying=false;
        }
        void ContinueZen(){
            if(!zenPlaced || closed || zenChangingHost)return;
            if(!Window.IsVisible || Window.WindowState==WindowState.Minimized){StopZenClock();return;}
            if(zenTime!=ZenDestination && !zenPlaying)StartZenClock();
            else if(zen && zenTime==ZenDestination)StartComposition();
        }
        void ZenFrame(object sender,EventArgs args){
            if(closed || !Window.IsVisible || Window.WindowState==WindowState.Minimized){StopZenClock();return;}
            var now=((RenderingEventArgs)args).RenderingTime;
            if(!zenStamp.HasValue){zenStamp=now;return;}
            if(now==zenStamp.Value)return;
            if(now<zenStamp.Value){zenStamp=now;return;}
            double dt=(now-zenStamp.Value).TotalSeconds;
            zenStamp=now;
            double next=zen?Math.Min(ZenDestination,zenTime+dt):Math.Max(0,zenTime-dt);
            PaintZen(SystemParameters.ClientAreaAnimation && !contrastTheme?next:ZenDestination);
            if(zenTime==ZenDestination)StopZenClock();
        }
        static double ZenEase(double t){t=UiMotion.Unit(t);return t*t*(3-2*t);}
        internal static Rect InterpolateRect(Rect from,Rect to,double t){
            return new Rect(from.X+(to.X-from.X)*t,from.Y+(to.Y-from.Y)*t,
                from.Width+(to.Width-from.Width)*t,from.Height+(to.Height-from.Height)*t);
        }
        Rect LocalFrame(Rect pixels){
            return new Rect(sky.PointFromScreen(pixels.TopLeft),sky.PointFromScreen(pixels.BottomRight));
        }
        static Rect PixelFrame(Rect frame){
            return new Rect(new Point(Math.Round(frame.Left),Math.Round(frame.Top)),new Point(Math.Round(frame.Right),Math.Round(frame.Bottom)));
        }
        void PlaceZenWindow(IntPtr hwnd,Rect frame){
            SetWindowPos(hwnd,IntPtr.Zero,(int)frame.Left,(int)frame.Top,(int)frame.Width,(int)frame.Height,0x0014);
        }
        void ApplyAperture(IntPtr hwnd,Rect host,Rect frame,int radius,bool repaint){
            frame=PixelFrame(frame);
            if(frame==lastZenFrame && radius==lastZenRadius)return;
            int x=(int)(frame.Left-host.Left),y=(int)(frame.Top-host.Top);
            int right=x+(int)frame.Width,bottom=y+(int)frame.Height;
            var region=radius==0?CreateRectRgn(x,y,right,bottom):CreateRoundRectRgn(x,y,right+1,bottom+1,2*radius,2*radius);
            if(region==IntPtr.Zero)return;
            // USER owns the region only after SetWindowRgn succeeds.
            if(SetWindowRgn(hwnd,region,repaint)==0){DeleteObject(region);return;}
            lastZenFrame=frame;lastZenRadius=radius;
        }
        Rect zenHost;
        void StageObservation(Rect host,Rect frame){
            zenChangingHost=true;
            try{
                var hwnd=new WindowInteropHelper(Window).Handle;
                lastZenRadius=-1;
                if(!zenTravel){
                    zenTravel=true;
                    if(Window.WindowState==WindowState.Maximized)Window.WindowState=WindowState.Normal;
                    Window.ResizeMode=ResizeMode.NoResize;
                }
                zenHost=host;
                ApplyAperture(hwnd,host,frame,0,true);
                PlaceZenWindow(hwnd,host);
                Window.UpdateLayout();
                // WindowChrome may replace the region during a native resize.
                lastZenRadius=-1;ApplyAperture(hwnd,host,frame,0,true);
                zenFromChart=LocalFrame(zenChartPixels);zenToChart=LocalFrame(zenMonitor);
                zenHomeOffset=(Vector)sky.PointFromScreen(zenSkyPixels);
                zenFromFocus=sky.PointFromScreen(zenFocusPixels);
                zenScaleRatio=zenHomeDpi/VisualTreeHelper.GetDpi(sky).DpiScaleX;
                sky.PrepareObservationPhoto(zenToChart);
            }finally{zenChangingHost=false;}
        }
        void RefreshObservationDisplay(){
            if(!zen || !zenTravel || zenPlaying || zenChangingHost || !Window.IsVisible || Window.WindowState==WindowState.Minimized)return;
            var hwnd=new WindowInteropHelper(Window).Handle;
            var screen=System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            var monitor=new Rect(screen.X,screen.Y,screen.Width,screen.Height);
            if(monitor==zenMonitor && LocalFrame(monitor)==zenToChart)return;
            zenMonitor=monitor;zenStage=Rect.Union(zenHome,zenMonitor);
            StageObservation(zenMonitor,zenMonitor);PoseObservation();RevealZenFrame(1);
        }
        void RestoreZenWindow(){
            var hwnd=new WindowInteropHelper(Window).Handle;
            zenChangingHost=true;
            try{
                // WindowChrome resets the native region on a style change. Shrink the
                // actual HWND first so even that intermediate frame stays inside home.
                PlaceZenWindow(hwnd,zenWindow);
                Window.ResizeMode=savedZenResize;
                if(savedZenState==WindowState.Maximized){
                    if((zenRestore.Length==0 || !SetWindowPlacement(hwnd,ref zenRestore)) && !savedZenBounds.IsEmpty){
                        Window.Left=savedZenBounds.Left;Window.Top=savedZenBounds.Top;
                        Window.Width=savedZenBounds.Width;Window.Height=savedZenBounds.Height;
                    }
                    // Equal native bounds can suppress WM_SIZE during the hand-off.
                    Window.WindowState=WindowState.Maximized;
                }
                SetWindowRgn(hwnd,IntPtr.Zero,true);
                lastZenFrame=Rect.Empty;lastZenRadius=-1;
                zenTravel=false;zenExpansion=0;
                zenButtonShift.X=zenButtonShift.Y=0;
                sky.SetObservationCamera(null,new Vector(),false);
                Window.UpdateLayout();
                sky.PrepareObservationPhoto(LocalFrame(zenMonitor));
                sky.PresentObservation(false,0);
            }finally{zenChangingHost=false;}
            SizeStellarViewport();Window.UpdateLayout();
            InvalidateStellarBounds();UpdateStellarBounds(null,EventArgs.Empty);
        }
        void PoseObservation(){
            double t=zenExpansion;
            var bounds=InterpolateRect(zenFromChart,zenToChart,t);
            var center=new Point(zenToChart.X+.5*zenToChart.Width,zenToChart.Y+.49*zenToChart.Height);
            var focus=zenFromFocus+(center-zenFromFocus)*t;
            // A shallow arc separates the foreground lens from the distant field.
            focus.Y-=Math.Min(12,(center-zenFromFocus).Length*.045)*4*t*(1-t);
            sky.SetObservationCamera(focus,zenHomeOffset*(1-t),t<1);
            double from=zenHomeScale*zenScaleRatio;
            double to=Math.Min(4.8,Math.Min(zenToChart.Width/220,zenToChart.Height/175));
            sky.SetStellarViewport(bounds,from+(to-from)*t,zenHomeExpansion+(1-zenHomeExpansion)*t,
                zenHomeHeight*zenScaleRatio*(1-t)+zenToChart.Height*t);
        }
        void RevealZenFrame(double travel){
            var frame=InterpolateRect(zenHome,zenMonitor,travel);
            var dpi=VisualTreeHelper.GetDpi(sky);
            zenButtonShift.X=(frame.Right-zenHost.Right)/dpi.DpiScaleX;
            zenButtonShift.Y=(frame.Top-zenHost.Top)/dpi.DpiScaleY;
            var hwnd=new WindowInteropHelper(Window).Handle;
            if(travel==1 && lastZenRadius!=-1){
                SetWindowRgn(hwnd,IntPtr.Zero,true);lastZenRadius=-1;lastZenFrame=zenMonitor;
            }else if(travel<1){
                // DWM already has the entire galaxy surface. A native erase/paint
                // here would race the WPF frame and repaint every underlying HWND.
                ApplyAperture(hwnd,zenHost,frame,(int)Math.Round(10*(1-travel)*dpi.DpiScaleX),false);
            }
        }
        CompositionObservatory composition;
        void StopComposition(){composition?.Dispose();composition=null;}
        void StartComposition(){
            if(ScenePresenter.Requested || !CompositionObservatory.Requested || composition!=null && !composition.Closed || !Window.IsVisible ||
               Window.WindowState==WindowState.Minimized || contrastTheme || !SystemParameters.ClientAreaAnimation)return;
            try{composition=new CompositionObservatory(Window,sky);}
            catch(Exception ex) when(ex is InvalidOperationException || ex is System.Runtime.InteropServices.ExternalException ||
                ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is NotSupportedException){StopComposition();}
        }
        void PaintZen(double time){
            if(!zen || time<ZenDestination)StopComposition();
            // Present the contracted home aperture before switching back buffers.
            bool handoff=zenTravel && !zen && time<=ZenFadeSeconds && zenPlaying &&
                SystemParameters.ClientAreaAnimation && !contrastTheme;
            if(handoff)time=ZenFadeSeconds;
            zenTime=time;
            double fade=1-ZenEase(time/ZenFadeSeconds);
            double travel=ZenEase((time-ZenFadeSeconds)/ZenTravelSeconds);
            foreach(var part in folding){
                part.Opacity=fade;
                part.Visibility=fade>0?Visibility.Visible:Visibility.Hidden;
                part.IsHitTestVisible=!zen && time==0;
            }
            sky.RevealObservationPhoto(1-fade);
            if(travel>0){
                bool preparing=!zenTravel;
                var host=travel==1?zenMonitor:zenStage;
                if(preparing || host!=zenHost)StageObservation(host,preparing?zenHome:zenMonitor);
                // Present one complete monitor-sized scene behind the original aperture first.
                if(preparing && zenPlaying && time<ZenDestination){
                    zenTime=ZenFadeSeconds;travel=0;
                }
                zenExpansion=travel+.17*travel*(1-travel);
                sky.PresentObservation(true,zenExpansion);
                PoseObservation();
                RevealZenFrame(travel);
            }else if(zenTravel){
                if(handoff && zenExpansion>0){
                    zenExpansion=0;sky.PresentObservation(true,0);PoseObservation();RevealZenFrame(0);
                }else RestoreZenWindow();
            }
            if(time==0 && !zen){
                observingInput.Visibility=Visibility.Collapsed;zenPlaced=false;
                // Reattach Mica and the original shape only after the host is home.
                ApplyWindowBackdrop();
                var hwnd=new WindowInteropHelper(Window).Handle;
                if(SetWindowRgn(hwnd,zenHomeRegion,true)==0 && zenHomeRegion!=IntPtr.Zero)DeleteObject(zenHomeRegion);
                zenHomeRegion=IntPtr.Zero;
                sky.PrepareObservationPhoto(Rect.Empty);
                observatoryButton.IsEnabled=!contrastTheme;
            }
            if(zenTime==ZenDestination){sky.HoldObservationAim(false);if(zen)StartComposition();}
        }
        void MoveObservatory(int message,IntPtr bounds){
            if(message==0x0231 || message==0x0232){movingOrigin=null;return;}
            if(message!=0x0216 || bounds==IntPtr.Zero || sky==null)return;
            var p=new Point(Marshal.ReadInt32(bounds),Marshal.ReadInt32(bounds,4));
            long stamp=Stopwatch.GetTimestamp();
            if(movingOrigin.HasValue && stamp>moveStamp){
                var delta=p-movingOrigin.Value;var dpi=VisualTreeHelper.GetDpi(sky);
                sky.WindowImpulse(new Vector(delta.X/dpi.DpiScaleX,delta.Y/dpi.DpiScaleY),(stamp-moveStamp)/(double)Stopwatch.Frequency);
            }
            movingOrigin=p;moveStamp=stamp;
        }
    }
}
