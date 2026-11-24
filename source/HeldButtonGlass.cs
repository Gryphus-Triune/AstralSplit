using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AstralSplit
{
    // A template layer. The owning ButtonBase still handles focus, capture and Click.
    public sealed class HeldButtonGlass : Grid
    {
        public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed),
            typeof(bool), typeof(HeldButtonGlass), new PropertyMetadata(false,
                (element, args) => ((HeldButtonGlass)element).UpdatePress()));

        static readonly DependencyProperty HoldBehaviorProperty = DependencyProperty.RegisterAttached("HoldBehavior",
            typeof(HoldBehavior), typeof(HeldButtonGlass));

        sealed class HoldBehavior
        {
            internal readonly Func<bool> Activate;
            internal readonly Action<double> PaintProgress;
            internal HoldBehavior(Func<bool> activate, Action<double> paintProgress)
            {
                Activate = activate;
                PaintProgress = paintProgress;
            }
        }

        internal static void EnableHoldActivation(ButtonBase button, Func<bool> activate, Action<double> paintProgress)
        {
            button.SetValue(HoldBehaviorProperty, new HoldBehavior(activate, paintProgress));
        }

        readonly DispatcherTimer hold;
        readonly SpringProgress press = new SpringProgress();
        readonly Border field = new Border();
        readonly CapsuleThumb face = new CapsuleThumb { RestFill = Brushes.Transparent, Strength = 1.12 };
        readonly TranslateTransform origin = new TranslateTransform();
        ButtonBase button;
        FrameworkElement resting;
        Window window;
        Rect aperture = Rect.Empty;
        Rect? release;
        HoldBehavior holdBehavior;
        bool lifted, pointerHeld, activatedOnHold;
        int restingZ;

        public bool Pressed { get { return (bool)GetValue(PressedProperty); } set { SetValue(PressedProperty, value); } }

        public HeldButtonGlass()
        {
            IsHitTestVisible = false;
            Visibility = Visibility.Hidden;
            field.SetResourceReference(Border.BackgroundProperty, "SegmentFace");
            face.SetResourceReference(LiquidLens.TintProperty, "GlassKnob");
            face.RenderTransform = origin;
            Children.Add(field);
            Children.Add(new Canvas { UseLayoutRounding = false, SnapsToDevicePixels = false, Children = { face } });

            hold = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) {
                Interval = TimeSpan.FromMilliseconds(UiMotion.GlassHoldMilliseconds)
            };
            hold.Tick += (s, e) => {
                hold.Stop();
                if (!CanHold) return;
                if (LiquidLens.GetGlassEnabled(this))
                {
                    lifted = true;
                    face.Pressed = true;
                    MoveTo(1);
                }
                if (pointerHeld && button.IsMouseCaptured && !activatedOnHold)
                    activatedOnHold = holdBehavior?.Activate() == true;
            };
            press.ValueChanged += Paint;
            Loaded += Attach;
            Unloaded += Detach;
        }

        bool CanHold => button != null && button.IsEnabled && button.IsVisible && Pressed;

        void Attach(object sender, RoutedEventArgs e)
        {
            button = TemplatedParent as ButtonBase;
            if (button == null) return;
            resting = button.Template.FindName("RestingSurface", button) as FrameworkElement;
            restingZ = Panel.GetZIndex(button);
            holdBehavior = (HoldBehavior)button.GetValue(HoldBehaviorProperty);
            if (holdBehavior != null)
            {
                button.PreviewMouseLeftButtonDown += PointerPressed;
                button.LostMouseCapture += PointerCaptureLost;
            }
            button.PreviewMouseLeftButtonUp += PointerRelease;
            button.PreviewKeyUp += KeyRelease;
            button.IsEnabledChanged += CheckVisibility;
            button.IsVisibleChanged += CheckVisibility;
            window = Window.GetWindow(button);
            if (window == null) return;
            window.PreviewKeyDown += CancelWithEscape;
            window.Deactivated += Cancel;
            window.StateChanged += CheckWindowState;
        }

        void Detach(object sender, RoutedEventArgs e)
        {
            if (button == null) return;
            if (holdBehavior != null)
            {
                button.PreviewMouseLeftButtonDown -= PointerPressed;
                button.LostMouseCapture -= PointerCaptureLost;
            }
            button.PreviewMouseLeftButtonUp -= PointerRelease;
            button.PreviewKeyUp -= KeyRelease;
            button.IsEnabledChanged -= CheckVisibility;
            button.IsVisibleChanged -= CheckVisibility;
            if (window != null)
            {
                window.PreviewKeyDown -= CancelWithEscape;
                window.Deactivated -= Cancel;
                window.StateChanged -= CheckWindowState;
            }
            Reset(false);
            holdBehavior = null;
            pointerHeld = activatedOnHold = false;
            window = null;
            button = null;
            resting = null;
        }

        void CheckVisibility(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!button.IsVisible || !button.IsEnabled) Reset(false);
        }

        void CheckWindowState(object sender, EventArgs e)
        {
            if (window.WindowState == WindowState.Minimized) Reset(false);
        }

        void Cancel(object sender, EventArgs e) { Reset(true); }

        void CancelWithEscape(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !Pressed) return;
            Reset(true);
            if (button.IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                button.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        void PointerPressed(object sender, MouseButtonEventArgs e)
        {
            pointerHeld = true;
            activatedOnHold = false;
        }

        void PointerCaptureLost(object sender, MouseEventArgs e)
        {
            if (!button.IsMouseCaptured) pointerHeld = activatedOnHold = false;
        }

        void PointerRelease(object sender, MouseButtonEventArgs e)
        {
            bool alreadyActivated = activatedOnHold;
            pointerHeld = activatedOnHold = false;
            if (!button.IsMouseCaptured) return;
            if (alreadyActivated)
            {
                e.Handled = true;
                button.ReleaseMouseCapture();
                return;
            }
            if (!new Rect(button.RenderSize).Contains(e.GetPosition(button)))
            {
                button.ReleaseMouseCapture();
                return;
            }
            CaptureRelease();
        }

        void KeyRelease(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) CaptureRelease();
        }

        void CaptureRelease()
        {
            if (lifted && !aperture.IsEmpty && UiMotion.CanAnimate(button)) release = aperture;
        }

        internal Rect? TakeRelease()
        {
            var result = release;
            release = null;
            return result;
        }

        void UpdatePress()
        {
            hold.Stop();
            lifted = false;
            if (CanHold && (LiquidLens.GetGlassEnabled(this) || pointerHeld))
            {
                release = null;
                hold.Start();
                return;
            }
            face.Pressed = false;
            MoveTo(0);
        }

        void Reset(bool animate)
        {
            release = null;
            lifted = false;
            hold.Stop();
            face.Pressed = false;
            if (animate) MoveTo(0);
            else press.MoveTo(0, false, UiMotion.GlassReleaseFrequency, null);
            Paint();
            if (!animate) holdBehavior?.PaintProgress(0);
        }

        void MoveTo(double target)
        {
            if (button == null) return;
            bool animate = UiMotion.CanAnimate(button);
            press.MoveTo(target, animate, target == 0 ? UiMotion.GlassReleaseFrequency : UiMotion.GlassLiftFrequency,
                animate ? DisplayRefresh.For(button) : null);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo e)
        {
            base.OnRenderSizeChanged(e);
            Paint();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == LiquidLens.GlassEnabledProperty && hold != null && !LiquidLens.GetGlassEnabled(this)) Reset(false);
        }

        void Paint()
        {
            if (button == null || ActualWidth < 1 || ActualHeight < 1) return;
            double lift = AnimatedSwitch.GlassLift(press.Value);
            if (lift == 0 && aperture.IsEmpty) return;
            double rail = ActualHeight * 40 * AnimatedSwitch.RestThumbAspect / 44;
            double label = Math.Max(0, ActualWidth - ActualHeight * AnimatedSwitch.RestThumbAspect);
            Size FaceSize(double amount)
            {
                var capsule = AnimatedSwitch.ThumbAtLift(ActualWidth, ActualHeight, rail, label, amount);
                // Icon-only reload keeps its circular silhouette as the lens lifts.
                return Math.Abs(ActualWidth - ActualHeight) < .5 ? new Size(capsule.Height, capsule.Height) : capsule;
            }
            var maximum = FaceSize(1);
            face.Width = Math.Ceiling(maximum.Width) + 8;
            face.Height = Math.Ceiling(maximum.Height) + 8;
            field.CornerRadius = new CornerRadius(ActualHeight / 2);
            var size = FaceSize(lift);
            var bounds = new Rect((ActualWidth - size.Width) / 2, (ActualHeight - size.Height) / 2, size.Width, size.Height);
            origin.X = bounds.X;
            origin.Y = bounds.Y;
            face.DrawCapsule(field, bounds, lift);

            double mix = lift * lift * (3 - 2 * lift);
            if (resting != null) resting.Opacity = 1 - mix;
            Opacity = mix;
            Visibility = lift > 0 ? Visibility.Visible : Visibility.Hidden;
            if (aperture.IsEmpty != (lift == 0))
                button.SetCurrentValue(Panel.ZIndexProperty, lift > 0 ? restingZ + 1 : restingZ);
            aperture = lift > 0 ? bounds : Rect.Empty;
            holdBehavior?.PaintProgress(mix);
        }
    }
}
