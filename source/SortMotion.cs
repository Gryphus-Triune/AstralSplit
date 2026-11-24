using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed class SortStarTrail : FrameworkElement
    {
        const double FlightMs = 245, TailMs = 55;
        static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double),
            typeof(SortStarTrail), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
        static readonly KeySpline arcTiming = (KeySpline)new KeySpline(.32,.08,.18,1).GetAsFrozen();
        static readonly Brush halo = MakeHalo();
        static readonly Brush dust = (Brush)new SolidColorBrush(Color.FromRgb(178,213,255)).GetAsFrozen();
        Point from, to;
        double lift, launchOpacity;

        public SortStarTrail() { Unloaded += (s,e) => Clear(); }

        static Brush MakeHalo()
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Colors.White, 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(202,168,225,255), .22));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(68,157,162,250), .58));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0,157,162,250), 1));
            glow.Freeze();
            return glow;
        }

        internal double Progress { get { return (double)GetValue(ProgressProperty); } }
        internal Point Head { get { return Position(UiMotion.Unit(Progress * (FlightMs + TailMs) / FlightMs)); } }

        Point Position(double time)
        {
            double t = arcTiming.GetSplineProgress(time);
            return new Point(from.X + (to.X-from.X)*t, from.Y + (to.Y-from.Y)*t - 4*lift*t*(1-t));
        }

        internal void Fly(Point origin, Point destination, int? rate)
        {
            // On reversal the head already on screen becomes the new launch point.
            double time = UiMotion.Unit(Progress*(FlightMs+TailMs)/FlightMs);
            bool inFlight = Progress < 1;
            var start = inFlight ? Head : origin;
            launchOpacity = inFlight ? Intensity(time,launchOpacity) : 0;
            from = start;
            to = destination;
            lift = Math.Min(19, Math.Min(Math.Abs(to.X-from.X)*.24, Math.Max(0, Math.Min(from.Y,to.Y)-3)));
            BeginAnimation(ProgressProperty, UiMotion.Linear(0,1,FlightMs+TailMs,rate));
        }

        internal void Clear() { BeginAnimation(ProgressProperty, null); }

        static double Intensity(double time,double initial)
        {
            return Math.Min(1,Math.Min(initial+(1-initial)*time*10,(1-time)*8));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double clock = Progress*(FlightMs+TailMs);
            if (Progress >= 1) return;
            for (int i=5; i>=0; i--)
            {
                double time = (clock-i*11)/FlightMs;
                if (time<0 || time>=1) continue;
                double weight = 1-i*.13;
                var p = Position(time);
                dc.PushOpacity(Intensity(time,i==0 ? launchOpacity : 0)*weight);
                if (i == 0) dc.DrawEllipse(halo,null,p,8,8);
                dc.DrawEllipse(i==0 ? Brushes.White : dust,null,p,weight*2.25,weight*2.25);
                dc.Pop();
            }
        }
    }

    // The item container can be recycled while this translation is still running.
    // Keep these transforms outside the content's independent removal/selection animations.
    public sealed class SortRowFace : Grid
    {
        readonly TranslateTransform shift = new TranslateTransform();
        readonly ScaleTransform zoom = new ScaleTransform();
        Action finished;
        int revision;

        public SortRowFace()
        {
            RenderTransform = new TransformGroup { Children = { zoom, shift } };
            RenderTransformOrigin = new Point(.42,.5);
            DataContextChanged += (s,e) => Stop();
            Unloaded += (s,e) => Stop();
        }

        internal struct Pose
        {
            internal Point Origin;
            internal double Scale, Opacity, Glint;
        }

        internal Pose CurrentPose(UIElement viewport)
        {
            // Translate the transform origin, which is invariant under scaling.
            var anchor = new Point(ActualWidth*.42,ActualHeight*.5);
            Point p = TranslatePoint(anchor,viewport);
            return new Pose { Origin=new Point(p.X-anchor.X,p.Y-anchor.Y), Scale=zoom.ScaleX,
                Opacity=Opacity, Glint=Children[0].Opacity };
        }

        internal void Move(Vector offset, Pose start, int delay, int? rate, Action ended)
        {
            Stop();
            finished = ended;
            int turn = ++revision;
            bool rising = offset.Y > 0;
            double shoulder = Math.Min(35,Math.Abs(offset.Y))/35;
            var container = TemplatedParent as ListBoxItem;
            if (container != null) Panel.SetZIndex(container,rising ? 2 : 1);
            var height = UiMotion.Ease(offset.Y,0,400,rate,.21,.78,.24,1,delay);
            height.Completed += (s,e) => { if(turn==revision) Stop(); };
            shift.BeginAnimation(TranslateTransform.XProperty,Shape(offset.X,(rising ? -5 : 5)*shoulder,0,delay,rate));
            var scale = Shape(start.Scale,1+(rising ? .014 : -.012)*shoulder,1,delay,rate);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty,scale);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty,scale);
            BeginAnimation(OpacityProperty,Shape(start.Opacity,rising?1:.78,1,delay,rate));
            if (rising || start.Glint>.001) Children[0].BeginAnimation(OpacityProperty,Shape(start.Glint,rising ? .68 : 0,0,delay,rate));
            shift.BeginAnimation(TranslateTransform.YProperty,height);
        }

        static DoubleAnimationUsingKeyFrames Shape(double start,double peak,double end,int delay,int? rate)
        {
            var motion = new DoubleAnimationUsingKeyFrames { Duration=TimeSpan.FromMilliseconds(delay+400), FillBehavior=FillBehavior.Stop };
            motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(start,TimeSpan.Zero));
            if(delay>0) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(start,TimeSpan.FromMilliseconds(delay)));
            motion.KeyFrames.Add(new SplineDoubleKeyFrame(peak,TimeSpan.FromMilliseconds(delay+114),new KeySpline(.2,0,.3,1)));
            motion.KeyFrames.Add(new SplineDoubleKeyFrame(end,TimeSpan.FromMilliseconds(delay+400),new KeySpline(.22,.65,.25,1)));
            return UiMotion.AtRefresh(motion,rate);
        }

        internal void Stop()
        {
            if (finished == null) return;
            revision++;
            var notify = finished;
            finished = null;
            shift.BeginAnimation(TranslateTransform.XProperty,null);
            shift.BeginAnimation(TranslateTransform.YProperty,null);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty,null);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            BeginAnimation(OpacityProperty,null);
            Children[0].BeginAnimation(OpacityProperty,null);
            var container = TemplatedParent as ListBoxItem;
            if(container!=null) container.ClearValue(Panel.ZIndexProperty);
            notify();
        }
    }
}
