using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        readonly ScaleTransform segmentScale = new ScaleTransform(1, 1);
        readonly TranslateTransform listEntrance = new TranslateTransform();
        readonly SpringProgress segmentPress = new SpringProgress();
        double runningGlassLift, selectedGlassLift;
        Grid surface, listViewport;
        bool hidingToTray;
        bool? flightContentVisible;
        StellarWindowFlight windowFlight;
        ColdWindowLaunch coldOpen;
        bool openingFlight,meteorTargetsTaskbar;
        DispatcherTimer warmRevealTimeout;
        void StopWarmWait(){
            if(warmRevealTimeout==null)return;
            warmRevealTimeout.Stop();warmRevealTimeout.Tick-=WarmRevealReady;
            nativeScene.FirstFrameReady-=WarmRevealReady;warmRevealTimeout=null;
        }
        void WarmRevealReady(object sender,EventArgs e){
            StopWarmWait();
            if(!closed && !exitInProgress && pendingReveal!=null && Window.IsVisible && Window.WindowState!=WindowState.Minimized)
                PlayFlightTo(false,revealFromTray,preparedTaskbarPoint);
        }
        StellarWindowFlight.HostCloak pendingReveal;
        bool revealFromTray, startupReveal, startupForegroundPending;
        Point? trayExitOrigin;

        bool CanPresentNativeScene()
        {
            if(nativeScene?.Flying==true)return true;
            if(zenPlaying || exitInProgress)return false;
            if(nativeScene?.Retained==true)return true;
            if(pendingReveal!=null && (warmRevealTimeout!=null || nativeScene?.ReadyDevice!=null))return true;
            return !hidingToTray && !openingFlight && (coldOpen!=null ||
                (pendingReveal==null && windowFlight==null && !flightContentVisible.HasValue && !startupReveal));
        }

        void InitializeTransitions()
        {
            surface = Find<Grid>("WindowSurface");
            listViewport = Find<Grid>("ListViewport");
            listViewport.RenderTransform = listEntrance;
            filterIndicator.RenderTransformOrigin = new Point(0.5, 0.5);
            filterIndicator.RenderTransform = new TransformGroup { Children = { segmentScale, filterSlide } };
            Find<LiquidLens>("FilterGlass").FollowTransform(filterIndicator.RenderTransform);
            segmentPress.ValueChanged += () => segmentScale.ScaleX = segmentScale.ScaleY = 1 - 0.03 * segmentPress.Value;
            HeldButtonGlass.EnableHoldActivation(all, () => SwitchFromHold(false), lift => {
                runningGlassLift = lift;
                DrawFilterIndicator();
            });
            HeldButtonGlass.EnableHoldActivation(selected, () => SwitchFromHold(true), lift => {
                selectedGlassLift = lift;
                DrawFilterIndicator();
            });

            var pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ToggleButton));
            EventHandler onPress = (s, e) => PaintSegmentPress();
            pressed.AddValueChanged(all, onPress);
            pressed.AddValueChanged(selected, onPress);
            Window.Closed += (s, e) => {
                startupForegroundPending=false;
                pressed.RemoveValueChanged(all, onPress);
                pressed.RemoveValueChanged(selected, onPress);
                CancelWindowFlight();
            };
            surface.Loaded += (s, e) => StartPreparedReveal();
            Window.StateChanged += TaskbarStateChanged;
        }

        bool SwitchFromHold(bool onlySelected)
        {
            if (closed || exitInProgress || selectedOnly == onlySelected) return false;
            SetFilter(onlySelected);
            return true;
        }

        void PaintSegmentPress()
        {
            bool pressed = all.IsPressed || selected.IsPressed;
            bool animate = UiMotion.CanAnimate(filterIndicator);
            segmentPress.MoveTo(pressed ? 1 : 0, animate, pressed ? 40 : 26,
                animate ? DisplayRefresh.For(filterIndicator) : null);
        }

        void TransitionList()
        {
            // Commit the tab and its rows together. Rendering can delay a
            // queued callback indefinitely after an outgoing fade.
            RefreshView();
            listScroller?.ScrollToTop();
            if (contrastTheme || !UiMotion.CanAnimate(listViewport))
            {
                ResetListTransition();
                return;
            }

            double x = listEntrance.X;
            if (Math.Abs(x) < .05) x = selectedOnly ? 6 : -6;
            var rate = DisplayRefresh.For(listViewport);
            listViewport.BeginAnimation(UIElement.OpacityProperty, UiMotion.Ease(Math.Min(.84, listViewport.Opacity), 1, 140, rate));
            listEntrance.BeginAnimation(TranslateTransform.XProperty, UiMotion.Ease(x, 0, 140, rate));
        }

        void ResetListTransition()
        {
            listViewport.BeginAnimation(UIElement.OpacityProperty, null);
            listEntrance.BeginAnimation(TranslateTransform.XProperty, null);
        }

        void CancelWindowFlight()
        {
            StopWarmWait();
            coldOpen?.Dispose();coldOpen=null;
            windowFlight?.Cancel();windowFlight=null;hidingToTray=false;
            landingOnTaskbar=false;preparedTaskbarPoint=null;
            startupReveal=false;
            pendingReveal?.Dispose();pendingReveal=null;
        }

        void SetFlightContentVisible(bool? visible)
        {
            if(flightContentVisible==visible)return;
            bool finishMinimizing = ProjectingMinimizedWindow && visible != true;
            flightContentVisible=visible;
            if(closed)return;
            if (finishMinimizing) { ResetStellarTraffic(); ResetPresentation(); }
            UpdateAmbientMotion();
            UpdateLoadingMotion();
        }

        bool ProjectingMinimizedWindow => flightContentVisible == true && Window.WindowState == WindowState.Minimized;

        bool CanAnimateWindowContent(FrameworkElement element)
        {
            if (flightContentVisible.HasValue)
                return flightContentVisible.Value && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            return UiMotion.CanAnimate(element);
        }

        bool ReverseOutgoingFlight()
        {
            if (windowFlight?.ReturnToWindow(() => windowFlight = null) != true) return false;
            hidingToTray = false;
            landingOnTaskbar = false;
            return true;
        }

        internal void PrepareWindowReveal(bool fromTray)
        {
            if (exitInProgress || pendingReveal!=null) return;
            if (!fromTray && !Window.IsLoaded && !startupForegroundPending)
            {
                startupForegroundPending=true;
                Window.ContentRendered += FirstStartupContentRendered;
            }
            if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
            // Even a cold tray instance needs a cloaked HWND before WPF maps or activates it.
            revealFromTray=fromTray;
            startupReveal=!fromTray && !Window.IsLoaded;
            pendingReveal=StellarWindowFlight.HostCloak.Acquire(Window);
            if(startupReveal && pendingReveal!=null && nativeScene!=null)sky.PrepareOpeningAtlas(Window);
        }

        void FirstStartupContentRendered(object sender,EventArgs e)
        {
            Window.ContentRendered -= FirstStartupContentRendered;
            if (pendingReveal==null && windowFlight==null) QueueStartupForeground();
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);

        void QueueStartupForeground()
        {
            if (!startupForegroundPending || !Window.IsVisible || !Window.IsLoaded) return;
            // The flight's completion callback runs before its HWND closes and the host
            // is uncloaked. Move the real window forward after that handoff.
            Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => {
                if (!startupForegroundPending || pendingReveal!=null || windowFlight!=null) return;
                startupForegroundPending=false;
                if (closed || exitInProgress || hidingToTray || !Window.IsVisible || Window.WindowState==WindowState.Minimized) return;
                var hwnd=new WindowInteropHelper(Window).Handle;
                if (hwnd==IntPtr.Zero) return;
                const uint keepBoundsAndFocus=0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER
                var topmost=new IntPtr(-1);
                var ordinary=new IntPtr(-2);
                // A launch can return from UAC without keyboard foreground permission.
                // Promote once for visibility; drop back into the normal band immediately.
                bool promoted=SetWindowPos(hwnd,topmost,0,0,0,0,keepBoundsAndFocus);
                try { Window.Activate(); }
                finally { if (promoted) SetWindowPos(hwnd,ordinary,0,0,0,0,keepBoundsAndFocus); }
            }));
        }

        void StartPreparedReveal()
        {
            if(coldOpen!=null || warmRevealTimeout!=null)return;
            if (exitInProgress || pendingReveal==null || !surface.IsLoaded || !Window.IsVisible || Window.WindowState==WindowState.Minimized) return;
            if(startupReveal && nativeScene!=null && GlassEffect.Hardware3(sky))
                coldOpen=new ColdWindowLaunch(Window,surface,nativeScene,FinishColdOpening);
            else if (startupReveal) PlayLifecycle(false);
            else if(nativeScene!=null && nativeScene.ReadyDevice==null && nativeScene.Failure==null && GlassEffect.Hardware3(sky)){
                // A tray-only launch may not have rendered its first home frame yet.
                warmRevealTimeout=new DispatcherTimer(DispatcherPriority.Render,Window.Dispatcher){Interval=TimeSpan.FromSeconds(2.2)};
                warmRevealTimeout.Tick+=WarmRevealReady;nativeScene.FirstFrameReady+=WarmRevealReady;
                warmRevealTimeout.Start();
            }else PlayFlightTo(false,revealFromTray,preparedTaskbarPoint);
        }

        void FinishColdOpening(bool landed)
        {
            var flight=coldOpen;
            if(flight==null)return;
            coldOpen=null;
            if(landed){
                startupReveal=false;
                pendingReveal?.Dispose();pendingReveal=null;
                flight.Dispose();
                QueueStartupForeground();
            }else{
                flight.Dispose();
                if(!closed && !exitInProgress && Window.IsVisible && Window.WindowState!=WindowState.Minimized)
                    PlayLifecycle(false);
                else CancelWindowFlight();
            }
        }

        void BeginAnimatedExit()
        {
            if(coldOpen!=null)CancelWindowFlight();
            taskbarSweep.Stop();
            startupForegroundPending=false;
            exitInProgress=true;
            CloseMilestoneMenu();
            surface.IsHitTestVisible=false;
            CancelPendingTaskbarMinimize();
            pendingTrayHide?.Abort();
            pendingTrayHide=null;
            if (windowFlight == null) { PlayLifecycle(true); return; }
            // Complete the current handoff first so exiting mid-minimize starts from
            // the icon and exiting mid-startup collapses the fully formed window.
            windowFlight.FlightEnded += (s,e) => Window.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,new Action(() => { if (!closed) PlayLifecycle(true); }));
        }

        void PlayLifecycle(bool exiting)
        {
            var prepared=pendingReveal;
            pendingReveal=null;
            startupReveal=false;
            void Finished()
            {
                windowFlight=null;
                if (!exiting) { QueueStartupForeground(); return; }
                if (closed) return;
                closingForExit=true;
                try { Window.Close(); }
                finally { closingForExit=false; }
            }
            try
            {
                bool trayOnly = exiting && !Window.IsVisible;
                bool body = Window.IsVisible && Window.WindowState != WindowState.Minimized;
                Point? origin = null;
                if (trayOnly) origin = trayExitOrigin ?? StellarWindowFlight.TrayPoint(tray);
                else if (!body && exiting) origin = TaskbarPoint();
                openingFlight=true;
                windowFlight = StellarWindowFlight.OpenLifecycle(Window,surface,exiting,body,prepared,
                    Finished,SetFlightContentVisible,origin,trayOnly);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException ||
                ex is System.Runtime.InteropServices.ExternalException || ex is System.ComponentModel.Win32Exception || ex is OutOfMemoryException) { }
            finally { openingFlight=false; }
            if (windowFlight == null)
            {
                try { Finished(); }
                finally { prepared?.Dispose(); }
            }
        }

        void PlayWindowFlight(bool hiding,bool fromTray=false)
        {
            PlayFlightTo(hiding,fromTray,null);
        }

        void PlayFlightTo(bool hiding,bool fromTray,Point? taskbarPoint,bool alreadyMinimizing=false)
        {
            if (exitInProgress) return;
            if (hiding) { startupForegroundPending=false; CloseMilestoneMenu(); }
            var prepared=hiding?null:pendingReveal;
            if(!hiding)pendingReveal=null;
            
            void Finished(bool landed){
                windowFlight=null;hidingToTray=false;landingOnTaskbar=false;
                revealFromTray=false;
                if(closed || !hiding)return;
                if(taskbarPoint.HasValue){if(!alreadyMinimizing)CommitTaskbarMinimize();return;}
                Window.Hide();
                if(landed)(tray?.ContextMenuStrip as GlassTrayMenu)?.CatchStar();
            }
            if(hiding && windowFlight!=null && taskbarPoint.HasValue==meteorTargetsTaskbar &&
                windowFlight.RetargetMeteor(true,()=>Finished(true))){
                hidingToTray=true;landingOnTaskbar=meteorTargetsTaskbar;return;
            }
            CancelWindowFlight();hidingToTray=hiding;landingOnTaskbar=hiding && taskbarPoint.HasValue;
            meteorTargetsTaskbar=taskbarPoint.HasValue;
            try { openingFlight=true;windowFlight=StellarWindowFlight.Open(Window,surface,tray,hiding,fromTray,prepared,()=>Finished(true),SetFlightContentVisible,taskbarPoint,nativeScene); }
            catch(Exception ex) when(ex is InvalidOperationException || ex is ArgumentException ||
                ex is System.Runtime.InteropServices.ExternalException || ex is System.ComponentModel.Win32Exception || ex is OutOfMemoryException) { }
            finally { openingFlight=false;if(windowFlight==null)prepared?.Dispose(); }
            if(windowFlight==null)Finished(false);
        }

        void HideToTray()
        {
            if (closed || exitInProgress || hidingToTray || !Window.IsVisible) return;
            PlayWindowFlight(true);
        }

        void ResetIdleTransitions()
        {
            if (SystemParameters.HighContrast || !UiMotion.CanAnimate(listViewport)) ResetListTransition();
            if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
            {
                bool hide=hidingToTray, minimize=landingOnTaskbar;
                if (!exitInProgress)
                {
                    CancelWindowFlight();
                    if(minimize)CommitTaskbarMinimize();
                    else if(hide)Window.Hide();
                    else QueueStartupForeground();
                }
                segmentPress.MoveTo(all.IsPressed || selected.IsPressed ? 1 : 0, false, 26, null);
            }
        }
    }
}
