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
        readonly Dictionary<string,ApplicationStar> appStars=new Dictionary<string,ApplicationStar>(StringComparer.OrdinalIgnoreCase);
        bool CanAnimateAppStars { get { return running && !disposed && !highContrast && !viewport.IsEmpty && SystemParameters.ClientAreaAnimation; } }

        static double NormalizedAppRate(double bytes)
        {
            return bytes>0 && UiMotion.IsFinite(bytes) ? UiMotion.Unit(Math.Log10(1+bytes)/6.4) : 0;
        }

        double AppStarRadius(string path)
        {
            if(!slots.TryGetValue(path,out int slot))return 1.06;
            return !Live?1.55:slot<3?2.65-.36*slot:1.62;
        }

        void PaintAppStar(DrawingContext dc,string path,Point center,bool selected,double radius,double power)
        {
            double now=flightClock.Elapsed.TotalSeconds;
            bool created=!appStars.TryGetValue(path,out var star);
            if(created)appStars.Add(path,star=new ApplicationStar(PathSeed(path)));
            bool measured=appRates.TryGetValue(path,out double bytes);
            if(chartSearch.Length!=0)power=SurveyMatch(path)?Math.Min(1,power+.36):power*.21;
            star.Place(center,GateCenter,AppInk(path),selected,radius,power,now);
            bool animate=CanAnimateAppStars;
            star.SetMotion(animate,refreshRate,now);
            star.SetFlow(Live && selected?bytes:0,animate && selected,refreshRate);
            if(created || !Live || !selected)
                star.ObserveTemperature(!Live || !selected?0:measured?(double?)bytes:null,now,animate,refreshRate);
            if(!selected && !IsFlyingIn(path))return; // Direct stars are drawn once in the lensed background.
            dc.DrawDrawing(star.Art);
        }

        void UpdateApplicationStarRates()
        {
            bool animate=CanAnimateAppStars;
            double now=flightClock.Elapsed.TotalSeconds;
            foreach(var pair in appStars){
                bool selected=slots.ContainsKey(pair.Key);
                if(!selected && !directNames.ContainsKey(pair.Key))continue;
                var star=pair.Value;
                bool known=appRates.TryGetValue(pair.Key,out double bytes);
                star.SetFlow(Live && selected?bytes:0,animate,refreshRate);
                star.ObserveTemperature(!Live || !selected?0:known?(double?)bytes:null,now,animate,refreshRate);
            }
            PruneApplicationStars(now);
        }

        void PruneApplicationStars(double now)
        {
            // An icon flight can be reversed before the next process scan. Keep its
            // original visual and oscillator through that interval, with bounded retention.
            var detached=appStars.Where(p=>!slots.ContainsKey(p.Key) && !directNames.ContainsKey(p.Key))
                .OrderByDescending(p=>p.Value.LastPlaced).ToArray();
            for(int i=0;i<detached.Length;i++){
                var pair=detached[i];
                if(i<MaximumNamedStars && now-pair.Value.LastPlaced<3)continue;
                pair.Value.Settle();appStars.Remove(pair.Key);
            }
        }

        void SettleApplicationStars()
        {
            foreach(var star in appStars.Values)star.Settle();
            if(disposed)appStars.Clear();
        }

        void SyncAppStarRefresh()
        {
            double now=flightClock.Elapsed.TotalSeconds;
            foreach(var star in appStars.Values)star.SetMotion(CanAnimateAppStars,refreshRate,now);
        }

        sealed class ApplicationStar
        {
            static readonly Color[] Temperatures={Color.FromRgb(255,176,122),Color.FromRgb(255,241,214),Color.FromRgb(207,224,255)};
            static readonly Spectrum[] Spectra=Array.ConvertAll(Temperatures,color=>new Spectrum(color));
            readonly DrawingGroup atmosphere=new DrawingGroup(),streams=new DrawingGroup(),pulse=new DrawingGroup();
            readonly DrawingGroup spikes=new DrawingGroup(),tongue=new DrawingGroup();
            readonly DrawingGroup[][] bands=new DrawingGroup[3][];
            readonly GeometryDrawing corona=new GeometryDrawing();
            readonly MatrixTransform placement=new MatrixTransform();
            readonly ScaleTransform tideScale=new ScaleTransform(),twinkleScale=new ScaleTransform(),rayLength=new ScaleTransform();
            readonly RotateTransform forward=new RotateTransform(),inverse=new RotateTransform(),orbit=new RotateTransform();
            readonly double twinklePeriod,twinklePhase,orbitPeriod,orbitPhase;
            AnimationClock twinkleCycle;
            bool moving, routed;
            int? frameRate;
            Color? appColor;
            int temperatureBand, pendingBand=-1;
            double pendingSince,sourceRadius;
            internal readonly DrawingGroup Art=new DrawingGroup();
            internal double LastPlaced {get;private set;}

            internal ApplicationStar(uint seed)
            {
                var random=new Random(unchecked((int)seed));
                twinklePeriod=2.6+2.2*random.NextDouble();twinklePhase=random.NextDouble();
                orbitPeriod=40+30*random.NextDouble();orbitPhase=random.NextDouble();
                Art.Transform=placement;
                var body=new DrawingGroup {Transform=new TransformGroup{Children={inverse,tideScale,forward}}};
                Art.Children.Add(body);
                body.Children.Add(atmosphere);atmosphere.Children.Add(corona);atmosphere.Children.Add(streams);
                corona.Geometry=Disk(9,9);streams.Transform=orbit;
                for(int i=0;i<3;i++){
                    var angle=new RotateTransform(random.NextDouble()*180);angle.Freeze();
                    var plume=new DrawingGroup{Transform=angle};
                    plume.Children.Add(new GeometryDrawing(null,null,Disk(6,1.1)));
                    streams.Children.Add(plume);
                }
                var inner=new DrawingGroup();var stellarSurface=new DrawingGroup();
                pulse.Transform=twinkleScale;pulse.Children.Add(inner);pulse.Children.Add(spikes);
                spikes.Transform=rayLength;tongue.Transform=forward;
                body.Children.Add(pulse);body.Children.Add(tongue);body.Children.Add(stellarSurface);
                for(int i=0;i<Spectra.Length;i++){
                    var art=Spectra[i];
                    bands[i]=new[]{Put(inner,art.Inner),Put(spikes,art.Rays),Put(tongue,art.Tip),Put(stellarSurface,art.Surface)};
                    if(i>0)foreach(var layer in bands[i])layer.Opacity=0;
                }
                tongue.Opacity=0;
            }

            static DrawingGroup Put(DrawingGroup parent,Drawing art)
            {
                var layer=new DrawingGroup();layer.Children.Add(art);parent.Children.Add(layer);return layer;
            }

            static EllipseGeometry Disk(double rx,double ry)
            {
                var circle=new EllipseGeometry(new Point(),rx,ry);circle.Freeze();return circle;
            }

            static RadialGradientBrush Radial(params GradientStop[] stops)
            {
                var brush=new RadialGradientBrush {ColorInterpolationMode=ColorInterpolationMode.SRgbLinearInterpolation};
                foreach(var stop in stops)brush.GradientStops.Add(stop);
                brush.Freeze();return brush;
            }

            static Color Shade(Color c,double factor)
            {
                return Color.FromRgb((byte)(c.R*factor),(byte)(c.G*factor),(byte)(c.B*factor));
            }

            sealed class Spectrum
            {
                internal readonly GeometryDrawing Inner,Surface,Tip;
                internal readonly DrawingGroup Rays=new DrawingGroup();

                internal Spectrum(Color color)
                {
                    Inner=new GeometryDrawing(Radial(new GradientStop(A(Colors.White,.87),0),
                        new GradientStop(A(color,.34),.27),new GradientStop(A(color,.12),.56),new GradientStop(A(color,0),1)),null,Disk(3.6,3.6));
                    var sphere=Radial(new GradientStop(Colors.White,0),new GradientStop(color,.85),new GradientStop(Shade(color,.60),1));
                    Surface=new GeometryDrawing(sphere,null,Disk(1.15,1.15));
                    // This cusp reaches 2.2 radii after the tidal stretch (1.18x).
                    var cusp=Geometry.Parse("M -.25,-.88 C .53,-.97 1.02,-.56 1.865,0 C 1.02,.56 .53,.97 -.25,.88 Z");
                    cusp.Freeze();
                    var tipPaint=new LinearGradientBrush(color,A(Shade(color,.70),.68),0);tipPaint.Freeze();
                    Tip=new GeometryDrawing(tipPaint,null,cusp);
                    for(int i=0;i<8;i++){
                        bool minor=i>=4;int axis=i%4;
                        double length=minor?2.2:axis%2==0?5.2:4.2;
                        double width=minor?.20:.32;
                        var shape=new StreamGeometry();
                        using(var g=shape.Open()){
                            g.BeginFigure(new Point(0,-width),true,true);
                            g.QuadraticBezierTo(new Point(.36*length,-width*.22),new Point(length,0),true,true);
                            g.QuadraticBezierTo(new Point(.36*length,width*.22),new Point(0,width),true,true);
                        }
                        shape.Freeze();
                        var gradient=new LinearGradientBrush{MappingMode=BrushMappingMode.Absolute,StartPoint=new Point(),EndPoint=new Point(length,0)};
                        gradient.GradientStops.Add(new GradientStop(A(Colors.White,minor?.35:.91),0));
                        gradient.GradientStops.Add(new GradientStop(A(color,minor?.26:.67),.32));
                        gradient.GradientStops.Add(new GradientStop(A(color,0),1));gradient.Freeze();
                        var ray=new DrawingGroup{Transform=new RotateTransform(90*axis+(minor?45:0))};
                        ray.Children.Add(new GeometryDrawing(gradient,null,shape));Rays.Children.Add(ray);
                    }
                    Inner.Freeze();Surface.Freeze();Tip.Freeze();Rays.Freeze();
                }
            }

            internal void Place(Point at,Point gate,Color pigment,bool selected,double radius,double power,double now)
            {
                LastPlaced=now;sourceRadius=radius;
                Reposition(at,gate);
                if(!Equals(Art.GetAnimationBaseValue(DrawingGroup.OpacityProperty),power)){
                    double visible=Art.Opacity;Art.Opacity=power;
                    Art.BeginAnimation(DrawingGroup.OpacityProperty,appColor!=default(Color) && moving && SystemParameters.ClientAreaAnimation?
                        UiMotion.Ease(visible,power,220,frameRate):null);
                }
                routed=selected;
                atmosphere.Opacity=spikes.Opacity=selected?1:0;
                if(appColor==pigment)return;
                appColor=pigment;
                corona.Brush=Radial(new GradientStop(A(pigment,.50),0),new GradientStop(A(pigment,.20),.25),
                    new GradientStop(A(pigment,.06),.60),new GradientStop(A(pigment,0),1));
                var streak=Radial(new GradientStop(A(pigment,.10),0),new GradientStop(A(pigment,.055),.48),
                    new GradientStop(A(pigment,0),1));
                foreach(DrawingGroup beam in streams.Children)((GeometryDrawing)beam.Children[0]).Brush=streak;
            }

            internal void Reposition(Point at,Point gate)
            {
                placement.Matrix=new Matrix(sourceRadius,0,0,sourceRadius,at.X,at.Y);
                double angle=Math.Atan2(gate.Y-at.Y,gate.X-at.X)*180/Math.PI;
                forward.Angle=angle;inverse.Angle=-angle;
            }

            internal void SetMotion(bool enable,int? rate,double now)
            {
                if(moving==enable && (!enable || frameRate==rate))return;
                moving=enable;frameRate=rate;
                double phase=twinklePhase+now/twinklePeriod;
                twinkleCycle=enable?Cycle(1,.72,twinklePeriod,phase,true,rate):null;
                pulse.ApplyAnimationClock(DrawingGroup.OpacityProperty,twinkleCycle);
                var size=enable?Cycle(1,1.06,twinklePeriod,phase,true,rate):null;
                twinkleScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty,size);
                twinkleScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty,size);
                orbit.ApplyAnimationClock(RotateTransform.AngleProperty,enable?Cycle(0,360,orbitPeriod,orbitPhase+now/orbitPeriod,false,rate):null);
            }

            static AnimationClock Cycle(double start,double end,double seconds,double phase,bool reverse,int? rate)
            {
                var turn=new DoubleAnimation(start,end,TimeSpan.FromSeconds(seconds/(reverse?2:1))){
                    AutoReverse=reverse,RepeatBehavior=RepeatBehavior.Forever,
                    BeginTime=TimeSpan.FromSeconds(-(phase-Math.Floor(phase))*seconds)};
                if(reverse)turn.EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut};
                UiMotion.AtRefresh(turn,rate);turn.Freeze();return turn.CreateClock();
            }

            internal void SetFlow(double bytes,bool animate,int? rate)
            {
                bool stretched=bytes>0 && routed;
                double length=1+.35*NormalizedAppRate(bytes);
                Move(rayLength,ScaleTransform.ScaleXProperty,length,animate,rate);
                Move(rayLength,ScaleTransform.ScaleYProperty,length,animate,rate);
                Move(tideScale,ScaleTransform.ScaleXProperty,stretched?1.18:1,animate,rate);
                Move(tideScale,ScaleTransform.ScaleYProperty,stretched?.94:1,animate,rate);
                Move(tongue,DrawingGroup.OpacityProperty,stretched?1:0,animate,rate);
            }

            static void Move(Animatable item,DependencyProperty property,double target,bool animate,int? rate)
            {
                if(animate && (double)item.GetAnimationBaseValue(property)==target)return;
                double from=(double)item.GetValue(property);
                item.SetValue(property,target);
                item.BeginAnimation(property,animate && from!=target?UiMotion.Ease(from,target,400,rate):null);
            }

            internal void ObserveTemperature(double? bytes,double now,bool animate,int? rate)
            {
                if(!bytes.HasValue){pendingBand=-1;return;}
                double heat=NormalizedAppRate(bytes.Value);
                int band=heat<1.0/3?0:heat<2.0/3?1:2;
                if(band==temperatureBand){pendingBand=-1;return;}
                if(band!=pendingBand){pendingBand=band;pendingSince=now;return;}
                if(now-pendingSince<3)return;
                temperatureBand=band;pendingBand=-1;
                for(int i=0;i<bands.Length;i++)foreach(var layer in bands[i]){
                    double from=layer.Opacity,target=i==band?1:0;
                    layer.Opacity=target;
                    layer.BeginAnimation(DrawingGroup.OpacityProperty,animate && from!=target?UiMotion.Linear(from,target,1200,rate):null);
                }
            }

            internal void Settle()
            {
                SetMotion(false,null,0);pendingBand=-1;
                foreach(var family in bands)foreach(var layer in family)layer.BeginAnimation(DrawingGroup.OpacityProperty,null);
                foreach(var scale in new[]{rayLength,tideScale}){
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
                }
                tongue.BeginAnimation(DrawingGroup.OpacityProperty,null);
            }
        }
    }
}
