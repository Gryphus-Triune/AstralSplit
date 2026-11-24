using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    internal static class UiMotion
    {
        internal const int GlassHoldMilliseconds = 150;
        internal const double GlassLiftFrequency = 20, GlassReleaseFrequency = 24;

        internal static bool CanAnimate(FrameworkElement element)
        {
            return SystemParameters.ClientAreaAnimation && element.IsLoaded && element.IsVisible &&
                PresentationSource.FromVisual(element) != null && Window.GetWindow(element)?.WindowState != WindowState.Minimized;
        }

        internal static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        internal static double Unit(double value) { return Math.Min(1, Math.Max(0, value)); }

        internal static DoubleAnimation Linear(double from, double to, double milliseconds, int? rate)
        {
            return AtRefresh(new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) {
                FillBehavior = FillBehavior.Stop
            }, rate);
        }

        internal static DoubleAnimationUsingKeyFrames Ease(double from, double to, double milliseconds, int? rate,
            double x1 = 0.2, double y1 = 0, double x2 = 0, double y2 = 1, int delay = 0)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds + delay);
            var motion = new DoubleAnimationUsingKeyFrames { Duration = time, FillBehavior = FillBehavior.Stop };
            motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > 0) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            motion.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(time), new KeySpline(x1, y1, x2, y2)));
            return AtRefresh(motion, rate);
        }

        internal static T AtRefresh<T>(T animation, int? rate) where T : Timeline
        {
            if (rate.HasValue) Timeline.SetDesiredFrameRate(animation, rate);
            return animation;
        }

        internal static void PaintColor(Animatable target, DependencyProperty property, Color value, double milliseconds, int? rate, bool animate)
        {
            if (animate && Equals(target.GetAnimationBaseValue(property), value)) return;
            var from = (Color)target.GetValue(property);
            target.SetValue(property, value);
            ColorAnimationUsingKeyFrames transition = null;
            if (animate && from != value)
            {
                var time = TimeSpan.FromMilliseconds(milliseconds);
                transition = new ColorAnimationUsingKeyFrames { Duration = time, FillBehavior = FillBehavior.Stop };
                transition.KeyFrames.Add(new DiscreteColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                transition.KeyFrames.Add(new SplineColorKeyFrame(value, KeyTime.FromTimeSpan(time), new KeySpline(0.2, 0, 0, 1)));
                AtRefresh(transition, rate);
            }
            target.BeginAnimation(property, transition);
        }

        internal static void Fade(FrameworkElement element, double to, int milliseconds, Action completed = null)
        {
            double from = element.Opacity;
            element.Opacity = to;
            var motion = from != to && CanAnimate(element) ? Ease(from, to, milliseconds, DisplayRefresh.For(element)) : null;
            if (motion != null && completed != null) motion.Completed += (s, e) => completed();
            element.BeginAnimation(UIElement.OpacityProperty, motion);
            if (motion == null) completed?.Invoke();
        }
    }

    internal sealed class SpringAnimation : DoubleAnimationBase
    {
        const double MaximumSeconds = 0.45;
        readonly double from, to, velocity, omega, displacement, coefficient, endTime;

        internal SpringAnimation(double from, double to, double velocity, double omega)
        {
            this.from = from;
            this.to = to;
            this.velocity = velocity;
            this.omega = omega;
            displacement = from - to;
            coefficient = velocity + omega * displacement;
            Duration = TimeSpan.FromTicks((long)Math.Ceiling(FindSettlement() * TimeSpan.TicksPerSecond));
            endTime = Duration.TimeSpan.TotalSeconds;
            FillBehavior = FillBehavior.HoldEnd;
        }

        protected override Freezable CreateInstanceCore() { return new SpringAnimation(from, to, velocity, omega); }

        double FindSettlement()
        {
            // After one time constant both upper bounds decrease, including on an interrupted spring.
            double low = Math.Min(1 / omega, MaximumSeconds), high = MaximumSeconds;
            double a = Math.Abs(displacement), b = Math.Abs(coefficient), v = Math.Abs(velocity);
            for (int iteration = 0; iteration < 12; iteration++)
            {
                double t = (low + high) / 2, decay = Math.Exp(-omega * t);
                if ((a + b * t) * decay < 0.001 && (v + omega * b * t) * decay < 0.01) high = t;
                else low = t;
            }
            return high;
        }

        internal double Position(double seconds)
        {
            double remaining = displacement + coefficient * seconds;
            if (seconds >= endTime || remaining * displacement <= 0) return to;
            return to + remaining * Math.Exp(-omega * seconds);
        }

        internal double Velocity(double seconds)
        {
            if (seconds >= endTime || (displacement + coefficient * seconds) * displacement <= 0) return 0;
            return (velocity - omega * coefficient * seconds) * Math.Exp(-omega * seconds);
        }

        protected override double GetCurrentValueCore(double origin, double destination, AnimationClock clock)
        {
            return Position(clock.CurrentTime.GetValueOrDefault().TotalSeconds);
        }
    }

    internal sealed class SpringProgress : Animatable
    {
        static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(SpringProgress),
            new PropertyMetadata(0.0, (target, args) => ((SpringProgress)target).ValueChanged?.Invoke()));
        SpringAnimation spring;
        AnimationClock clock;
        double target;

        internal event Action ValueChanged;
        internal double Value { get {
            return UiMotion.Unit(clock?.CurrentState == ClockState.Stopped ? spring.Position(0) : (double)GetValue(ValueProperty));
        } }
        internal bool IsMoving { get { return clock != null; } }

        protected override Freezable CreateInstanceCore() { return new SpringProgress(); }

        internal void MoveTo(double next, bool animate, double omega, int? rate)
        {
            if (animate && target == next) return;
            double position = Value;
            double velocity = 0;
            if (clock != null && clock.CurrentTime.HasValue)
            {
                double seconds = clock.CurrentTime.Value.TotalSeconds;
                double limit = omega / Math.E;
                velocity = Math.Max(-limit, Math.Min(limit, spring.Velocity(seconds)));
            }
            target = next;
            if (!animate || (Math.Abs(next - position) < 0.0001 && Math.Abs(velocity) < 0.0001))
            {
                clock = null;
                spring = null;
                SetValue(ValueProperty, next);
                ApplyAnimationClock(ValueProperty, null);
                return;
            }
            spring = UiMotion.AtRefresh(new SpringAnimation(position, next, velocity, omega), rate);
            var animationClock = spring.CreateClock();
            clock = animationClock;
            animationClock.Completed += (sender, args) => {
                if (clock != animationClock) return;
                clock = null;
                spring = null;
                ApplyAnimationClock(ValueProperty, null);
            };
            ApplyAnimationClock(ValueProperty, animationClock, HandoffBehavior.SnapshotAndReplace);
            SetValue(ValueProperty, next);
        }
    }
}
