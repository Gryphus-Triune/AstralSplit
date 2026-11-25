using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        int waveRevision, fadeRevision, meteorRevision;
        static readonly LinearGradientBrush Tail = MeteorTail();
        readonly Layer clickStars = new Layer();
        int clickBearing;
        static readonly DrawingGroup ClickStarArt = CreateClickStar();

        static DrawingGroup CreateClickStar()
        {
            var art = new DrawingGroup();
            using (var dc = art.Open())
            {
                dc.DrawEllipse(Glow(Color.FromArgb(185,255,205,142)),null,new Point(),14,14);
                var rays = Geometry.Parse("M -7,0 Q -1,0 -0.7,-0.8 L 0,-6 Q 0.3,-0.9 0.9,-0.5 L 7,0 Q 1,0.4 0.6,0.9 L 0,6 Q -0.3,1 -0.8,0.5 Z");
                rays.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255,238,204)),null,rays);
                dc.DrawEllipse(Brushes.White,null,new Point(),1.25,1.25);
            }
            art.Freeze();
            return art;
        }

        internal DrawingGroup LightClickStar(Point point)
        {
            if (!running || disposed || highContrast || !IsVisible || !SystemParameters.ClientAreaAnimation ||
                !new Rect(RenderSize).Contains(point)) return null;
            var lights = clickStars.Art.Children;
            // Leave the new star at the pointer. A previous flash at the same
            // spot drifts just far enough for another press to read as a new star.
            for (int i=lights.Count-1;i>=0;i--) {
                var previous=(DrawingGroup)lights[i];
                var anchor=previous.Transform as TranslateTransform;
                if (anchor==null || !anchor.IsFrozen || (new Point(anchor.X,anchor.Y)-point).LengthSquared>81) continue;
                double angle=(++clickBearing%233)*2.39996323;
                double dx=15*Math.Cos(angle),dy=15*Math.Sin(angle);
                if(anchor.X+dx<0 || anchor.X+dx>RenderSize.Width)dx=-dx;
                if(anchor.Y+dy<0 || anchor.Y+dy>RenderSize.Height)dy=-dy;
                double x=Math.Max(0,Math.Min(RenderSize.Width,anchor.X+dx));
                double y=Math.Max(0,Math.Min(RenderSize.Height,anchor.Y+dy));
                var drift=new TranslateTransform(x,y);
                previous.Transform=drift;
                drift.BeginAnimation(TranslateTransform.XProperty,UiMotion.Ease(anchor.X,x,160,refreshRate,.2,0,.2,1));
                drift.BeginAnimation(TranslateTransform.YProperty,UiMotion.Ease(anchor.Y,y,160,refreshRate,.2,0,.2,1));
                break;
            }
            var offset = new TranslateTransform(point.X,point.Y);
            offset.Freeze();
            var star = new DrawingGroup { Transform = offset, Opacity = .75 };
            star.Children.Add(ClickStarArt);
            lights.Add(star);
            var envelope = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), FillBehavior = FillBehavior.HoldEnd };
            envelope.KeyFrames.Add(new LinearDoubleKeyFrame(.75,TimeSpan.Zero));
            envelope.KeyFrames.Add(new SplineDoubleKeyFrame(1,TimeSpan.FromMilliseconds(42),new KeySpline(.2,0,.2,1)));
            envelope.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromSeconds(1),new KeySpline(.32,0,.36,1)));
            envelope.Completed += (s,e) => RemoveClickStar(star);
            star.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(envelope,refreshRate));
            return star;
        }

        internal void RemoveClickStar(DrawingGroup star)
        {
            if (star!=null && clickStars.Art.Children.Remove(star))
                star.BeginAnimation(DrawingGroup.OpacityProperty,null);
        }

        void ClearClickStars()
        {
            var lights=clickStars.Art.Children;
            while(lights.Count>0)RemoveClickStar((DrawingGroup)lights[lights.Count-1]);
        }

        static LinearGradientBrush MeteorTail()
        {
            var b = new LinearGradientBrush(Color.FromArgb(0,140,195,255),Color.FromRgb(232,246,255),new Point(0,0),new Point(1,0));
            b.Freeze(); return b;
        }

        void ScheduleMeteor()
        {
            if (!running || state != "StatusOn" || playingMeteor || meteorTimer.IsEnabled || atlas == null) return;
            meteorTimer.Interval = TimeSpan.FromSeconds(8 + weather.NextDouble() * 12);
            meteorTimer.Start();
        }

        void MeteorDue(object sender, EventArgs args)
        {
            meteorTimer.Stop();
            if (!running || state != "StatusOn" || playingMeteor || ActualWidth < 1) return;
            playingMeteor = true;
            int revision = ++meteorRevision;
            double length = 70 + 20 * weather.NextDouble(), distance = 200 + 60 * weather.NextDouble();
            double angle = 28 + 6 * weather.NextDouble(), rad = angle * Math.PI / 180;
            double x = 24 + weather.NextDouble() * Math.Max(1,ActualWidth - distance - 48), y = 8 + weather.NextDouble() * 30;
            using (var dc = meteor.Art.Open())
            {
                dc.PushTransform(new RotateTransform(angle));
                // A tapered wedge gives the brush two dimensions and keeps the tail visible at fractional DPI.
                var stream = new StreamGeometry();
                using (var g = stream.Open()) { g.BeginFigure(new Point(-length,0),true,true); g.LineTo(new Point(0,-0.75),true,false); g.LineTo(new Point(0,0.75),true,false); }
                stream.Freeze();
                dc.DrawGeometry(Tail,null,stream); dc.DrawEllipse(Brushes.White,null,new Point(),0.9,0.9); dc.Pop();
            }
            var rate = DisplayRefresh.For(this);
            meteor.Slide.X = x + distance*Math.Cos(rad); meteor.Slide.Y = y + distance*Math.Sin(rad);
            meteor.Slide.BeginAnimation(TranslateTransform.XProperty,UiMotion.Ease(x,meteor.Slide.X,800,rate));
            meteor.Slide.BeginAnimation(TranslateTransform.YProperty,UiMotion.Ease(y,meteor.Slide.Y,800,rate));
            var opacity = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(800), FillBehavior = FillBehavior.Stop };
            foreach (var frame in new[] { new LinearDoubleKeyFrame(0,TimeSpan.Zero), new LinearDoubleKeyFrame(1,TimeSpan.FromMilliseconds(80)),
                new LinearDoubleKeyFrame(1,TimeSpan.FromMilliseconds(480)), new LinearDoubleKeyFrame(0,TimeSpan.FromMilliseconds(800)) }) opacity.KeyFrames.Add(frame);
            opacity.Completed += (s,e) => { if (revision == meteorRevision) { playingMeteor = false; ScheduleMeteor(); } };
            meteor.Art.Opacity = 0;
            meteor.Art.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(opacity,rate));
        }

        void StopMeteor()
        {
            meteorTimer.Stop();
            if (!playingMeteor) return;
            meteorRevision++; playingMeteor = false;
            meteor.Art.Opacity = 0; meteor.Art.BeginAnimation(DrawingGroup.OpacityProperty,null);
            meteor.Slide.BeginAnimation(TranslateTransform.XProperty,null); meteor.Slide.BeginAnimation(TranslateTransform.YProperty,null);
        }

        void StartWave(Point origin)
        {
            if (atlas == null || ActualWidth < 1 || origin.X <= 0) return;
            fadeRevision++;
            if (waving)
            {
                OpacityTo(ripple.Art,1,100,true,DisplayRefresh.For(this));
                return;
            }
            waving = true;
            int revision = ++waveRevision;
            double farX = Math.Max(origin.X,ActualWidth-origin.X), farY = Math.Max(origin.Y,ActualHeight-origin.Y);
            double radius = 1.26*Math.Sqrt(farX*farX + farY*farY) + 20;
            var mask = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = origin, GradientOrigin = origin, RadiusX = radius, RadiusY = radius };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent,0.72));
            mask.GradientStops.Add(new GradientStop(Colors.White,0.95));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent,1));
            var size = new ScaleTransform(.001,.001,origin.X,origin.Y);
            mask.Transform = size;
            // The moving mask illuminates the same stars, including the near layer, as the ring reaches them.
            ripple.Art.OpacityMask = mask;
            using (var dc = ripple.Art.Open())
            {
                dc.DrawRectangle(SkyAtlas.Brush(Color.FromArgb(24,106,219,255)),null,new Rect(0,0,ActualWidth,headerHeight));
                dc.PushTransform(distant.Slide); dc.DrawDrawing(distant.Art); dc.Pop();
                dc.PushTransform(middle.Slide); dc.DrawDrawing(middle.Art); dc.Pop();
                dc.PushTransform(close.Slide);
                foreach (var star in atlas.Near) DrawStar(dc,star.Point,star.Radius*1.48,Colors.White,star.Spikes,false);
                dc.Pop();
            }
            OpacityTo(ripple.Art,1,0,false,null);
            var rate = DisplayRefresh.For(this);
            var grow = UiMotion.Ease(.001,1,1320,rate,.32,.42,.65,.81);
            grow.BeginTime=TimeSpan.FromMilliseconds(75);
            grow.Completed += (s,e) => { if (revision == waveRevision) FinishWave(); };
            size.BeginAnimation(ScaleTransform.ScaleXProperty,grow);
            size.BeginAnimation(ScaleTransform.ScaleYProperty,grow);
        }

        void StopWave(bool fade)
        {
            if (!waving) return;
            if (!fade) { FinishWave(); return; }
            int revision = ++fadeRevision;
            var anim = UiMotion.Linear(ripple.Art.Opacity,0,150,DisplayRefresh.For(this));
            anim.Completed += (s,e) => { if (revision == fadeRevision) FinishWave(); };
            ripple.Art.Opacity = 0;
            ripple.Art.BeginAnimation(DrawingGroup.OpacityProperty,anim);
        }

        void FinishWave()
        {
            waving = false; waveRevision++; fadeRevision++;
            ripple.Art.BeginAnimation(DrawingGroup.OpacityProperty,null);
            ripple.Art.OpacityMask = null; ripple.Art.Children.Clear();
        }

        internal static uint PathSeed(string path)
        {
            unchecked {
                uint hash = 2166136261;
                foreach (char c in path) { hash ^= char.ToUpperInvariant(c == '/' ? '\\' : c); hash *= 16777619; }
                return hash;
            }
        }

    }
}
