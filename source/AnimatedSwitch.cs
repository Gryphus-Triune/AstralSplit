using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed class AnimatedSwitch : ToggleButton
    {
        const double SwitchFrequency = 23;
        internal const double TapPress = .12, RestThumbAspect = 1.54;
        const double RestTrackAspect = 2.25;
        internal const double RaisedWidthToTrackHeight = 2.10, RaisedHeightScale = 1.37;
        const double RaisedEndOffset = 0.42;
        const int ColorMilliseconds = 360;
        public static readonly DependencyProperty ThumbDiameterProperty = DependencyProperty.Register(nameof(ThumbDiameter),
            typeof(double), typeof(AnimatedSwitch), new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsArrange));
        public static readonly DependencyProperty ThumbWidthProperty = DependencyProperty.Register(nameof(ThumbWidth),
            typeof(double), typeof(AnimatedSwitch), new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsArrange));
        public static readonly DependencyProperty OffBrushProperty = DependencyProperty.Register(nameof(OffBrush),
            typeof(SolidColorBrush), typeof(AnimatedSwitch), new PropertyMetadata(Brushes.LightGray, OnPaletteChanged));
        public static readonly DependencyProperty OnBrushProperty = DependencyProperty.Register(nameof(OnBrush),
            typeof(SolidColorBrush), typeof(AnimatedSwitch), new PropertyMetadata(Brushes.DodgerBlue, OnPaletteChanged));
        static readonly DependencyProperty NudgeProperty = DependencyProperty.Register("Nudge", typeof(double),
            typeof(AnimatedSwitch), new PropertyMetadata(0.0, (element, args) => ((AnimatedSwitch)element).PaintThumb()));
        static readonly DependencyProperty TrackMixProperty = DependencyProperty.Register("TrackMix", typeof(double),
            typeof(AnimatedSwitch), new PropertyMetadata(0.0, (element, args) => ((AnimatedSwitch)element).PaintTrack()));
        readonly SpringProgress progress = new SpringProgress(), pressProgress = new SpringProgress();
        readonly RectangleGeometry trackClip = new RectangleGeometry();
        FrameworkElement chrome, knob, spinner;
        Border track, outline;
        CapsuleThumb face;
        SolidColorBrush trackFill;
        TranslateTransform thumb, spinnerShift;
        RotateTransform spin;
        object previousContext;
        DispatcherOperation rebound;
        DispatcherTimer pressHold;
        double travel;
        double opacityTarget = 1;
        Point pressPoint;
        double dragOrigin, dragSpan;
        bool busy, rejecting, pointerHeld, dragging, pressEngaged, longPress;
        bool? dragChoice;

        public double ThumbDiameter { get { return (double)GetValue(ThumbDiameterProperty); } set { SetValue(ThumbDiameterProperty, value); } }
        public double ThumbWidth { get { return (double)GetValue(ThumbWidthProperty); } set { SetValue(ThumbWidthProperty, value); } }
        public SolidColorBrush OffBrush { get { return (SolidColorBrush)GetValue(OffBrushProperty); } set { SetValue(OffBrushProperty, value); } }
        public SolidColorBrush OnBrush { get { return (SolidColorBrush)GetValue(OnBrushProperty); } set { SetValue(OnBrushProperty, value); } }
        static void OnPaletteChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) { ((AnimatedSwitch)target).PaintTrack(); }

        public AnimatedSwitch()
        {
            progress.ValueChanged += PositionChanged;
            pressProgress.ValueChanged += PaintThumb;
            DataContextChanged += (sender, args) => SnapForContext();
            IsVisibleChanged += (sender, args) => ResetMotion();
            Loaded += (sender, args) => {
                SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
                ResetMotion();
            };
            Unloaded += (sender, args) => {
                SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
                ResetMotion();
            };
        }

        internal bool IsMoving { get { return pointerHeld || progress.IsMoving || pressProgress.IsMoving; } }
        internal static double GlassLift(double press){return UiMotion.Unit((press-TapPress)/(1-TapPress));}
        internal static Size ThumbAtLift(double width,double height,double railHeight,double extraLabel,double lift){
            double raised=Math.Max(width,railHeight*RaisedWidthToTrackHeight+extraLabel);
            return new Size(width+(raised-width)*lift,height+(railHeight*RaisedHeightScale-height)*lift);
        }
        double Lift { get { return GlassLift(pressProgress.Value); } }

        public override void OnApplyTemplate()
        {
            CancelPointerGesture(false);
            if (spin != null) spin.BeginAnimation(RotateTransform.AngleProperty, null);
            base.OnApplyTemplate();
            chrome = GetTemplateChild("SwitchChrome") as FrameworkElement;
            track = GetTemplateChild("Track") as Border;
            outline = GetTemplateChild("TrackOutline") as Border;
            knob = GetTemplateChild("Thumb") as FrameworkElement;
            spinner = GetTemplateChild("BusyGlyph") as FrameworkElement;
            face = GetTemplateChild("ThumbFace") as CapsuleThumb;
            trackFill = new SolidColorBrush(OffBrush.Color);
            if (track != null) { track.Background = trackFill; track.Clip = trackClip; }
            thumb = new TranslateTransform();
            if (knob != null) knob.RenderTransform = thumb;
            spin = new RotateTransform();
            spinnerShift = new TranslateTransform();
            if (spinner != null)
                spinner.RenderTransform = new TransformGroup { Children = { spin, spinnerShift } };
            previousContext = DataContext;
            ResetMotion();
        }

        protected override Size ArrangeOverride(Size bounds)
        {
            var size = base.ArrangeOverride(bounds);
            if (track != null && knob != null)
            {
                var radius = new CornerRadius(track.RenderSize.Height / 2);
                track.CornerRadius = radius;
                if (outline != null) outline.CornerRadius = radius;
                // Reserve the expanded lens once per layout. WPF clips oversized ink at a fixed resting-size slot.
                knob.Width = Math.Ceiling(Math.Max(ThumbWidth, track.RenderSize.Height * RaisedWidthToTrackHeight)) + 8;
                knob.Height = Math.Ceiling(track.RenderSize.Height * RaisedHeightScale) + 8;
                Canvas.SetTop(knob, (track.RenderSize.Height - ThumbDiameter) / 2);
                double distance = track.RenderSize.Width - ThumbWidth - 2 * knob.Margin.Left;
                travel = UiMotion.IsFinite(distance) ? Math.Max(0, distance) : 0;
                PaintThumb();
            }
            return size;
        }

        protected override void OnChecked(RoutedEventArgs e) { CancelPointerGesture(false); base.OnChecked(e); Paint(true); }
        protected override void OnUnchecked(RoutedEventArgs e) { CancelPointerGesture(false); base.OnUnchecked(e); Paint(true); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var context = DataContext;
            base.OnMouseLeftButtonDown(e);
            if (ClickMode != ClickMode.Release || !IsEnabled || busy || !IsMouseCaptured || travel <= 0) return;
            if (!ReferenceEquals(context, DataContext)) { ReleaseMouseCapture(); return; }
            pressPoint = e.GetPosition(this);
            dragOrigin = progress.Value;
            pointerHeld = true;
            longPress = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pointerHeld && IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && DragTo(e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (pointerHeld && IsMouseCaptured && (DragTo(e.GetPosition(this)) || longPress))
            {
                e.Handled = true;
                bool choice = IsChecked == true;
                if(dragging){
                    if(GesturePreview==null)choice=progress.Value>=.5;
                    else if(choice)choice=progress.Value>.05;
                    else choice=progress.Value>=.95;
                }
                ReleasePointerGesture();
                PaintPress(true);
                if (IsEnabled && !busy && IsChecked != choice)
                {
                    dragChoice = choice;
                    try { OnClick(); }
                    finally { dragChoice = null; }
                }
                Paint(true);
                return;
            }
            pointerHeld = false;
            base.OnMouseLeftButtonUp(e);
        }

        protected override void OnToggle()
        {
            if (dragChoice.HasValue) SetCurrentValue(IsCheckedProperty, dragChoice.Value);
            else base.OnToggle();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (!IsMouseCaptured) CancelPointerGesture(true);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (pointerHeld && e.Key == Key.Escape)
            {
                CancelPointerGesture(true);
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        bool DragTo(Point cursor)
        {
            Vector delta = cursor - pressPoint;
            if (!dragging)
            {
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.X) < Math.Abs(delta.Y))
                    return false;
                dragging = true;
                dragSpan = Math.Max(1, travel + 2 * RaisedEndOffset * track.ActualHeight * Lift);
                if (!IsPressed) PaintPress(true);
            }
            double normalized = UiMotion.Unit(dragOrigin + delta.X / dragSpan);
            if (progress.IsMoving || progress.Value != normalized)
                progress.MoveTo(normalized, false, SwitchFrequency, null);
            else TrackAtEndpoint();
            GesturePreview?.Invoke(normalized);
            return true;
        }

        internal event Action<double?> GesturePreview;
        void ReleasePointerGesture()
        {
            if(dragging)GesturePreview?.Invoke(null);
            pointerHeld = dragging = longPress = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        void CancelPointerGesture(bool animate)
        {
            if (!pointerHeld) return;
            ReleasePointerGesture();
            PaintPress(animate);
            Paint(animate);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsPressedProperty) PaintPress(true);
            if (e.Property == IsEnabledProperty)
            {
                if (!IsEnabled) CancelPointerGesture(true);
                PaintAvailability(true);
                PaintPress(true);
            }
        }

        void SnapForContext()
        {
            CancelPointerGesture(false);
            previousContext = DataContext;
            Paint(false);
            PaintPress(false);
            if (rebound != null || !IsLoaded || Dispatcher.HasShutdownStarted) return;
            // Inherited DataContext and IsChecked can propagate in either order.
            rebound = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                rebound = null;
                Paint(false);
            }));
        }

        void Paint(bool animate)
        {
            if (thumb == null) return;
            bool sameRow = ReferenceEquals(previousContext, DataContext) && rebound == null;
            bool motion = animate && sameRow && travel > 0 && UiMotion.CanAnimate(this);
            progress.MoveTo(IsChecked == true ? 1 : 0, motion, SwitchFrequency, motion ? DisplayRefresh.For(this) : null);
            PaintThumb();
            if (motion) TrackAtEndpoint();
            else SetTrackMix(IsChecked == true ? 1 : 0, false);
        }

        void PositionChanged()
        {
            PaintThumb();
            TrackAtEndpoint();
        }

        void TrackAtEndpoint()
        {
            if (trackFill == null || travel <= 0) return;
            double p = progress.Value, target = IsChecked == true ? 1 : 0;
            bool arrived = dragging ? p == 0 || p == 1 : Math.Abs(target - p) * travel <= 0.12;
            if (arrived) SetTrackMix(dragging ? p : target, true);
            else
            {
                // Leaving an endpoint mid-fade holds its current color until the next landing.
                double current = (double)GetValue(TrackMixProperty);
                if (current != (double)GetAnimationBaseValue(TrackMixProperty)) SetTrackMix(current, false);
            }
        }

        void SetTrackMix(double target, bool animate)
        {
            if (animate && target == (double)GetAnimationBaseValue(TrackMixProperty)) return;
            double from = (double)GetValue(TrackMixProperty);
            var motion = animate && from != target && UiMotion.CanAnimate(this)
                ? UiMotion.Ease(from, target, ColorMilliseconds, DisplayRefresh.For(this), 0.38, 0, 0.22, 1) : null;
            SetValue(TrackMixProperty, target);
            BeginAnimation(TrackMixProperty, motion);
            PaintTrack();
        }

        void PaintThumb()
        {
            if (face == null || knob == null || thumb == null) return;
            if (track == null || track.ActualHeight < 1) return;
            double lift = Lift;
            bool contrast = !LiquidLens.GetGlassEnabled(face);
            double restRailHeight = contrast ? track.ActualHeight : Math.Min(track.ActualHeight, track.ActualWidth / RestTrackAspect);
            double railHeight = restRailHeight + (track.ActualHeight - restRailHeight) * lift;
            var rail = new Rect(0, (track.ActualHeight - railHeight) / 2, track.ActualWidth, railHeight);
            if (trackClip.Rect != rail)
            {
                trackClip.Rect = rail;
                trackClip.RadiusX = trackClip.RadiusY = railHeight / 2;
            }
            if (outline != null) outline.Visibility = contrast || lift > 0 ? Visibility.Visible : Visibility.Hidden;
            double restThumbHeight = Math.Min(ThumbDiameter, ThumbWidth / RestThumbAspect);
            Size size=ThumbAtLift(ThumbWidth,restThumbHeight,track.ActualHeight,0,lift);
            double width=size.Width,height=size.Height;
            double extraX = (width - ThumbWidth) / 2, extraY = (height - ThumbDiameter) / 2;
            double center = Math.Max(0, Math.Min(travel, progress.Value * travel + (double)GetValue(NudgeProperty)));
            double endOffset = track.ActualHeight * RaisedEndOffset * lift * (2 * progress.Value - 1);
            thumb.X = center + endOffset - extraX;
            thumb.Y = -extraY;
            var aperture = new Rect(knob.Margin.Left + thumb.X, (track.ActualHeight - height) / 2, width, height);
            face.DrawCapsule(track, aperture, lift);
            if (spinner != null)
            {
                spinnerShift.X = (width - spinner.Width) / 2;
                spinnerShift.Y = (height - spinner.Height) / 2;
            }
        }

        void PaintTrack()
        {
            if (trackFill == null) return;
            double colorProgress = (double)GetValue(TrackMixProperty);
            if (outline != null) outline.Opacity = 1 - colorProgress;
            Color offColor = OffBrush.Color, onColor = OnBrush.Color;
            trackFill.Color = Color.FromRgb(
                Blend(offColor.R, onColor.R, colorProgress), Blend(offColor.G, onColor.G, colorProgress), Blend(offColor.B, onColor.B, colorProgress));
        }

        static byte Blend(byte from, byte to, double progress) { return (byte)Math.Round(from + (to - from) * progress); }

        void PaintPress(bool animate)
        {
            if (face == null) return;
            bool pressed = (IsPressed || dragging) && IsEnabled && !busy && IsLoaded && IsVisible;
            if (animate && pressed == pressEngaged) return;
            pressEngaged = pressed;
            pressHold?.Stop();
            bool motion = animate && UiMotion.CanAnimate(this);
            pressProgress.MoveTo(pressed ? TapPress : 0, motion, pressed ? 38 : UiMotion.GlassReleaseFrequency, motion ? DisplayRefresh.For(this) : null);
            if (!pressed) return;
            if (pressHold == null)
            {
                pressHold = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(UiMotion.GlassHoldMilliseconds) };
                pressHold.Tick += (s, e) => {
                    pressHold.Stop();
                    if (pressEngaged && IsEnabled && !busy && IsLoaded && IsVisible)
                    {
                        longPress = pointerHeld;
                        bool continueMotion = UiMotion.CanAnimate(this);
                        pressProgress.MoveTo(1, continueMotion, UiMotion.GlassLiftFrequency, continueMotion ? DisplayRefresh.For(this) : null);
                    }
                };
            }
            pressHold.Start();
        }

        void PaintAvailability(bool animate)
        {
            if (chrome == null) return;
            double target = IsEnabled || busy ? 1 : 0.48;
            if (animate && opacityTarget == target) return;
            opacityTarget = target;
            double current = chrome.Opacity;
            chrome.Opacity = target;
            if (!animate || current == target || !UiMotion.CanAnimate(this))
            {
                chrome.BeginAnimation(OpacityProperty, null);
                return;
            }
            int delay = target < current ? 250 : 0;
            chrome.BeginAnimation(OpacityProperty,
                UiMotion.Ease(current, target, target < current ? 180 : 150, DisplayRefresh.For(this), 0.25, 0.1, 0.25, 1, delay));
        }

        internal void SetBusy(bool value)
        {
            if (busy == value) return;
            busy = value;
            if (busy) CancelPointerGesture(true);
            else StopNudge();
            PaintPress(true);
            PaintAvailability(true);
            PaintSpinner(true);
        }

        void PaintSpinner(bool animate)
        {
            if (spinner == null || spin == null) return;
            bool motion = UiMotion.CanAnimate(this);
            int? rate = motion ? DisplayRefresh.For(this) : null;
            double target = busy ? 1 : 0, from = spinner.Opacity;
            spinner.Opacity = target;
            spinner.BeginAnimation(OpacityProperty, animate && motion && from != target ? UiMotion.Linear(from, target, 120, rate) : null);
            spin.BeginAnimation(RotateTransform.AngleProperty, busy && motion
                ? UiMotion.AtRefresh(new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }, rate)
                : null);
        }

        internal void RejectBusyPress()
        {
            if (!busy || rejecting || !UiMotion.CanAnimate(this)) return;
            rejecting = true;
            double direction = IsChecked == true ? -1 : 1;
            var nudge = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(200), FillBehavior = FillBehavior.Stop };
            nudge.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            nudge.KeyFrames.Add(new SplineDoubleKeyFrame(2 * direction, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40)), new KeySpline(0.1,0.7,0.2,1)));
            nudge.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new KeySpline(0.3,0,0.1,1)));
            nudge.Completed += (s, e) => rejecting = false;
            BeginAnimation(NudgeProperty, UiMotion.AtRefresh(nudge, DisplayRefresh.For(this)));
        }

        void StopNudge()
        {
            rejecting = false;
            BeginAnimation(NudgeProperty, null);
        }

        void ResetMotion()
        {
            CancelPointerGesture(false);
            StopNudge();
            Paint(false);
            PaintPress(false);
            PaintAvailability(false);
            PaintSpinner(false);
        }

        void MotionPreferenceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) ResetMotion();
        }
    }

    public sealed class CapsuleThumb : LiquidLens
    {
        static readonly Brush[] nearShadow = ShadowBrushes(25, 3), farShadow = ShadowBrushes(36, 5);
        double diameter;

        internal double CapsuleWidth { get; private set; }
        protected override bool ManualSize { get { return true; } }

        internal void DrawCapsule(Border track, Rect aperture, double lift)
        {
            bool resized = CapsuleWidth != aperture.Width || diameter != aperture.Height;
            CapsuleWidth = aperture.Width;
            diameter = aperture.Height;
            double clear = lift * lift * (3 - 2 * lift);
            SampleTrack(track, aperture, clear);
            if (resized) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext context)
        {
            if (CapsuleWidth <= 0 || diameter <= 0 || KnobClarity <= 0 || !GetGlassEnabled(this)) return;
            context.PushOpacity(KnobClarity);
            DrawShadow(context, new Rect(0,3,CapsuleWidth,diameter),farShadow,6);
            DrawShadow(context, new Rect(0,1,CapsuleWidth,diameter),nearShadow,2);
            context.Pop();
        }

        static Brush[] ShadowBrushes(int alpha, int count)
        {
            var brushes = new Brush[count];
            for (int i = 0; i < count; i++)
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * (i + 1) / (count * (count + 1) / 2)), 0, 0, 0));
                brush.Freeze();
                brushes[i] = brush;
            }
            return brushes;
        }

        static void DrawShadow(DrawingContext context, Rect face, Brush[] shades, double blur)
        {
            for (int i = 0; i < shades.Length; i++)
            {
                var bounds = face;
                double spread = blur * (shades.Length - i - 1) / (shades.Length - 1);
                bounds.Inflate(spread, spread);
                double radius = Math.Min(bounds.Width, bounds.Height) / 2;
                context.DrawRoundedRectangle(shades[i], null, bounds, radius, radius);
            }
        }
    }
}
