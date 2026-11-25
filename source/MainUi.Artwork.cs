using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        readonly SpringProgress branchSpread = new SpringProgress();
        readonly TranslateTransform beadMotion = new TranslateTransform();
        StarfieldLayer sky;
        Ellipse bead;
        BranchGlyph arrows;
        string artworkState;
        bool beadRunning;
        int? beadRate;
        readonly Stopwatch beadTime = new Stopwatch();
        DispatcherOperation displayChange;
        DateTime skyDay;
        ScenePresenter nativeScene;

        void InitializeArtwork()
        {
            sky = Find<StarfieldLayer>("Sky");
            if(ScenePresenter.Requested){
                nativeScene=new ScenePresenter(Window,sky,CanPresentNativeScene);
                Window.Closed+=(s,e)=>nativeScene.Dispose();
            }
            var header = Find<StackPanel>("HeaderStack");
            header.SizeChanged += (s,e) => sky.SetHeaderHeight(52 + windowBody.Margin.Top + header.ActualHeight + 16);
            bead = Find<Ellipse>("StatusBead");
            arrows = Find<BranchGlyph>("StatusArrows");
            arrows.Stroke = statusFill;
            branchSpread.ValueChanged += () => arrows.Spread = branchSpread.Value;
            bead.RenderTransform = beadMotion;
            // WPF can activate the tray window before its child visuals become visible.
            sky.IsVisibleChanged += (s,e) => { if (sky.IsVisible) QueueDisplayChange(); };
            surface.PreviewMouseMove += (s,e) => sky.Aim(e.GetPosition(sky));
            surface.MouseLeave += (s,e) => sky.CenterAim();
            var skyInput = Find<Border>("SkyInput");
            skyInput.AddHandler(UIElement.PreviewMouseDownEvent,new MouseButtonEventHandler((s,e) => {
                if (e.ChangedButton==MouseButton.Left && search.IsKeyboardFocusWithin) skyInput.Focus();
            }),true);
            BindSkyClicks(skyInput,sky);
            var dismissHint = BindStarHover(skyInput,sky,Find<Border>("StarHoverCard"),Find<TextBlock>("StarHoverName"),Find<TextBlock>("StarHoverStatus"));
            Window.Deactivated += (s,e) => dismissHint();
            Window.StateChanged += (s,e) => { if (Window.WindowState == WindowState.Minimized) dismissHint(); };
            Window.LocationChanged += (s,e) => QueueDisplayChange();
            RenderCapability.TierChanged += SkyTierChanged;
            Window.Closed += (s,e) => { displayChange?.Abort(); RenderCapability.TierChanged -= SkyTierChanged; sky.Release(); };
        }

        static void BindSkyClicks(Border skyInput, StarfieldLayer sky)
        {
            // A press on the exposed sky starts a complete flash with its own lifetime.
            skyInput.AddHandler(UIElement.PreviewMouseDownEvent,new MouseButtonEventHandler((s,e)=>{
                if(e.ChangedButton==MouseButton.Left)sky.LightClickStar(e.GetPosition(sky));
            }),true);
        }

        void QueueDisplayChange()
        {
            if (closed || displayChange != null || Window.Dispatcher.HasShutdownStarted) return;
            displayChange = Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => {
                displayChange = null;
                if (!closed) { sky.RefreshDisplay(); RefreshObservationDisplay(); UpdateAmbientMotion(); }
            }));
        }

        void SkyTierChanged(object sender, EventArgs e) { UpdateAmbientMotion(); }

        void PaintArtwork(string state, bool animate)
        {
            bool changed = artworkState != state;
            artworkState = state;
            UpdateAmbientMotion();
            if (!changed && animate) return;
            bool motion = animate && UiMotion.CanAnimate(arrows);
            int? rate = motion ? DisplayRefresh.For(arrows) : null;
            UiMotion.PaintColor(statusFill,SolidColorBrush.ColorProperty,Theme("Sky" + state),400,rate,motion);
            branchSpread.MoveTo(state == "StatusOn" ? 1 : state == "StatusBusy" ? 0.65 : 0,motion,23,rate);
            var center = state == "StatusOn" && motion && master.IsArrangeValid ?
                master.TranslatePoint(new Point(master.ActualWidth / 2,master.ActualHeight / 2),sky) : new Point();
            sky.SetState(state,animate,center);
        }

        void UpdateAmbientMotion()
        {
            sky.SetActivity(Window.IsVisible && CanAnimateWindowContent(sky));
            bool busy = sky.AmbientRunning && artworkState == "StatusBusy";
            var rate = busy ? DisplayRefresh.For(bead) : null;
            if (busy == beadRunning && beadRate == rate) return;
            if (busy && !beadRunning) beadTime.Restart();
            else if (!busy) beadTime.Reset();
            beadRunning = busy;
            beadRate = rate;
            bead.Opacity = busy ? 1 : 0;
            var path = new PathGeometry(new[] { BranchGlyph.UpperArm(0.65) }); path.Freeze();
            var offset = TimeSpan.FromMilliseconds(-(beadTime.ElapsedMilliseconds % 1400));
            beadMotion.BeginAnimation(TranslateTransform.XProperty,busy ? FollowPath(path,PathAnimationSource.X,rate,offset) : null);
            beadMotion.BeginAnimation(TranslateTransform.YProperty,busy ? FollowPath(path,PathAnimationSource.Y,rate,offset) : null);
        }

        static DoubleAnimationUsingPath FollowPath(PathGeometry path,PathAnimationSource axis,int? rate,TimeSpan offset)
        {
            return UiMotion.AtRefresh(new DoubleAnimationUsingPath { PathGeometry = path, Source = axis,
                BeginTime=offset, Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = RepeatBehavior.Forever },rate);
        }
    }
}
