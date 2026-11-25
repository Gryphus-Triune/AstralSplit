using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        // The reference canvas advances the flyer's quadratic parameter, not the arc length.
        // Sample the last fourteen 60-Hz instants for the trail, independently of the display refresh.
        sealed class SelectionFlight
        {
            const double TravelMs = 620, TrailStepMs = 1000.0/60;
            const int TrailCount = 14;
            internal readonly DrawingGroup Art = new DrawingGroup();
            readonly TranslateTransform[] offsets = new TranslateTransform[TrailCount+1];
            readonly DrawingGroup[] lights = new DrawingGroup[TrailCount+1];
            readonly double born;
            Point a, bend, b;
            Vector? escapeDirection;
            Point[] history;
            double from, to, start, duration, bendScale;
            internal bool ToStar { get; private set; }
            internal bool BehindGate;
            internal double End { get { return start+duration; } }

            internal SelectionFlight(Point icon, Point star, bool selected, Color color, double now)
            {
                born = start = now;
                SetArc(icon,star,1);
                from = selected ? 0 : 1;
                to = selected ? 1 : 0;
                duration = TravelMs;
                ToStar = selected;
                for (int i=0; i<lights.Length; i++) {
                    offsets[i] = new TranslateTransform();
                    lights[i] = new DrawingGroup { Transform=offsets[i] };
                    Art.Children.Add(lights[i]);
                }
                SetColor(color);
                Draw(now);
            }

            void SetArc(Point first, Point last, double distanceScale)
            {
                a=first; b=last;bendScale=distanceScale;
                bend=new Point((a.X+b.X)/2+90*distanceScale, Math.Min(a.Y,b.Y)-40*distanceScale);
            }

            internal void FollowChart(Point? star,double rowShift)
            {
                if(escapeDirection.HasValue)return;
                var target=to==1?b:a;
                target=star??(target+new Vector(0,rowShift));
                if(to==1)SetArc(a,target,bendScale);else SetArc(target,b,bendScale);
            }

            internal void Slingshot(Vector tangent){
                escapeDirection=tangent;bend=b+tangent*Math.Max(44,Math.Min(160,(a-b).Length*.46));
                duration=810;Draw(start);
            }

            internal void SetColor(Color color)
            {
                for(int i=0;i<lights.Length;i++) {
                    using(var d=lights[i].Open()) {
                        if(i==TrailCount) {
                            Color hot=Color.FromRgb((byte)(.6*color.R+102),(byte)(.6*color.G+102),(byte)(.6*color.B+102));
                            d.DrawEllipse(Glow(hot),null,new Point(),11,11);
                            d.DrawEllipse(Brushes.White,null,new Point(),1.2,1.2);
                        } else {
                            double r=2+i*.45;
                            d.DrawEllipse(Glow(A(color,.07*i)),null,new Point(),r,r);
                        }
                    }
                }
            }

            double Parameter(double now)
            {
                double t=UiMotion.Unit((now-start)/duration);
                double eased=t<.5 ? 2*t*t : 1-2*(1-t)*(1-t);
                return from+(to-from)*eased;
            }

            internal Point Position(double now)
            {
                if(now<start && history!=null) {
                    double sample=Math.Min(TrailCount,(start-now)/TrailStepMs);
                    int i=(int)sample;
                    var next=history[Math.Min(TrailCount,i+1)];
                    return history[i]+(next-history[i])*(sample-i);
                }
                double t=Parameter(now), u=1-t;
                return new Point(u*u*a.X+2*u*t*bend.X+t*t*b.X, u*u*a.Y+2*u*t*bend.Y+t*t*b.Y);
            }

            internal void Turn(Point destination, bool selected, double now)
            {
                // Preserve the visible tail across a reversal. If virtualization or sorting has
                // moved the row, rebase the next arc at the current head instead of its old endpoint.
                var tail=new Point[TrailCount+1];
                for(int i=0;i<tail.Length;i++) tail[i]=Position(now-i*TrailStepMs);
                double current=Parameter(now);
                  escapeDirection=null;
                if((destination-b).LengthSquared<.25) { from=current; to=1; }
                else if((destination-a).LengthSquared<.25) { from=current; to=0; }
                else {
                    double span=UiMotion.Unit((destination-tail[0]).Length/Math.Max(1,(a-b).Length));
                    SetArc(tail[0],destination,span);
                    from=0; to=1;
                }
                start=now; history=tail; ToStar=selected;
                duration=Math.Max(65,TravelMs*Math.Sqrt(Math.Abs(to-from)));
                Draw(now);
            }

            internal void Draw(double now)
            {
                for(int i=0;i<lights.Length;i++) {
                    double t=now-Math.Max(0,TrailCount-1-i)*TrailStepMs;
                    double opacity=t>=born ? 1 : 0;
                    if(lights[i].Opacity!=opacity) lights[i].Opacity=opacity;
                    if(opacity==0) continue;
                    Point p=Position(t);
                    offsets[i].X=p.X; offsets[i].Y=p.Y;
                }
            }

            internal bool ClearOfGate(Point gate,double radius)
            {
                for(int i=0;i<lights.Length;i++){
                    if(lights[i].Opacity==0)continue;
                    var p=new Point(offsets[i].X,offsets[i].Y);
                    if((p-gate).LengthSquared<radius*radius)return false;
                }
                return true;
            }
        }

        sealed class Landing
        {
            internal readonly DrawingGroup Star=new DrawingGroup(), Ring=new DrawingGroup();
            internal readonly EllipseGeometry Circle=new EllipseGeometry();
        }

        readonly Dictionary<string,SelectionFlight> appFlights=new Dictionary<string,SelectionFlight>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Landing> landings=new Dictionary<string,Landing>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> finishedFlights=new List<string>();
        readonly DrawingGroup landingRings=new DrawingGroup();
        readonly Stopwatch flightClock=Stopwatch.StartNew();
        bool flightFrames;
        TimeSpan? lastFlightFrame;

        bool IsFlyingIn(string path)
        {
            return appFlights.TryGetValue(path,out var item) && item.ToStar;
        }

        internal void SignalApplication(string path,bool selected,Point icon)
        {
            if (!running || highContrast || !SystemParameters.ClientAreaAnimation || viewport.IsEmpty ||
                !UiMotion.IsFinite(icon.X) || !UiMotion.IsFinite(icon.Y) || !slots.TryGetValue(path,out int i)) return;
            icon-=new Vector(mapShift.X,mapShift.Y);
            Point star=Project(Anchors[i].X,Anchors[i].Y);
            double now=flightClock.Elapsed.TotalMilliseconds;
            bool behindGate=false;
            infalls.TryGetValue(path,out var fall);
            if(selected && Live && fall!=null && fall.Retracting && appFlights.TryGetValue(path,out var returning)){
                fall.Resume(now);
                (returning.BehindGate?rearTransit:flight).Children.Remove(returning.Art);
                  lensedFlyers.Children.Remove(returning.Art);
                appFlights.Remove(path);QueueChart();return;
            }
            if(!selected && fall!=null){
                star=fall.Position(now);behindGate=fall.IsBehind(star,now);
                RemoveHit(path);fallOnArrival.Remove(path);
            }else CancelInfall(path);
              AccretionPath escape=null;
              if(!selected && Live && fall==null && !appFlights.ContainsKey(path) && riverProgress>.05){
                  escape=CapturePath(path,star);star=escape.Seen(escape.Entry,false);
                  behindGate=escape.Behind(escape.Entry,false);
                  if(String.Equals(path,returningDirect,StringComparison.OrdinalIgnoreCase))icon=Project(.092,.85);
              }
            CancelLanding(path);
            if(selected && Live)fallOnArrival.Add(path);
            if(appFlights.TryGetValue(path,out var item)) {
                if(item.ToStar==selected)return;
                item.Turn(selected?star:icon,selected,now);
            } else {
                item=new SelectionFlight(icon,star,selected,AppInk(path),now){BehindGate=behindGate};
                  if(escape!=null)item.Slingshot(UnitTangent(escape.Seen(escape.DiskPoint(AccretionPath.Outer,escape.EntryAngle+.015),false)-star));
                appFlights.Add(path,item);
                (behindGate?rearTransit:flight).Children.Add(item.Art);
                  if(behindGate)lensedFlyers.Children.Add(item.Art);
            }
            if(!selected && fall!=null)fall.Retract(item,now);
            QueueChart();
            StartFlightFrames();
        }

        void StartFlightFrames()
        {
            if(flightFrames)return;
            flightFrames=true;UiFrames.Rendering+=AdvanceFlights;
        }

        void LaunchReadyInfalls()
        {
            if(!Live || riverProgress<1 || !CanMoveMatter || fallOnArrival.Count==0)return;
            double now=flightClock.Elapsed.TotalMilliseconds;
            foreach(var path in fallOnArrival.ToArray()){
                if(IsFlyingIn(path))continue;
                fallOnArrival.Remove(path);
                if(!slots.TryGetValue(path,out int slot))continue;
                var anchor=Project(Anchors[slot].X,Anchors[slot].Y);
                var fall=new Infall(CapturePath(path,anchor),AppInk(path),now);
                infalls[path]=fall;rearTransit.Children.Add(fall.Rear);infallLayer.Children.Add(fall.Art);
                StartFlightFrames();
            }
        }

        void AdvanceFlights(object sender,EventArgs args)
        {
            var frame=((RenderingEventArgs)args).RenderingTime;
            if(lastFlightFrame==frame)return;
            lastFlightFrame=frame;
            double now=flightClock.Elapsed.TotalMilliseconds;
            foreach(var pair in appFlights) {
                var moving=pair.Value;
                bool valid=slots.ContainsKey(pair.Key)==moving.ToStar;
                if(!valid || now>=moving.End) finishedFlights.Add(pair.Key);
                else {
                    moving.Draw(now);
                    if(moving.BehindGate && (!Live || moving.ClearOfGate(GateCenter,56*CoreScale))){
                        rearTransit.Children.Remove(moving.Art);lensedFlyers.Children.Remove(moving.Art);flight.Children.Add(moving.Art);moving.BehindGate=false;
                    }
                }
            }
            foreach(string path in finishedFlights) {
                var moving=appFlights[path];
                (moving.BehindGate?rearTransit:flight).Children.Remove(moving.Art);
                if(moving.BehindGate)lensedFlyers.Children.Remove(moving.Art);
                appFlights.Remove(path);
                if(moving.ToStar && slots.TryGetValue(path,out int i)){
                    var anchor=Project(Anchors[i].X,Anchors[i].Y);
                    LightArrival(path,anchor);
                }else fallOnArrival.Remove(path);
            }
            if(finishedFlights.Count>0) { finishedFlights.Clear();LaunchReadyInfalls();QueueChart(); }
            AdvanceInfall(now);
            if(appFlights.Count==0 && infalls.Count==0 && diskHits.Count==0) StopFlightFrames();
        }

        void LightArrival(string path, Point center)
        {
            var hit=new Landing();
            landings.Add(path,hit);
            hit.Circle.Center=center;
            hit.Circle.RadiusX=hit.Circle.RadiusY=34;
            using(var d=hit.Ring.Open())d.DrawGeometry(null,Stroke(AppInk(path),.92,1.4),hit.Circle);
            landingRings.Children.Add(hit.Ring);
            hit.Circle.BeginAnimation(EllipseGeometry.RadiusXProperty,UiMotion.Linear(2,34,335,refreshRate));
            hit.Circle.BeginAnimation(EllipseGeometry.RadiusYProperty,UiMotion.Linear(2,34,335,refreshRate));
            hit.Star.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.Ease(.20,1,245,refreshRate));
            var fade=UiMotion.Linear(.90,0,335,refreshRate);
            fade.Completed+=(s,e)=>{
                if(landings.TryGetValue(path,out var current) && current==hit) { CancelLanding(path); QueueChart(); }
            };
            hit.Ring.Opacity=0;
            hit.Ring.BeginAnimation(DrawingGroup.OpacityProperty,fade);
        }

        void CancelLanding(string path)
        {
            if(!landings.TryGetValue(path,out var hit))return;
            landings.Remove(path);
            landingRings.Children.Remove(hit.Ring);
            hit.Ring.BeginAnimation(DrawingGroup.OpacityProperty,null);
            hit.Star.BeginAnimation(DrawingGroup.OpacityProperty,null);
            hit.Circle.BeginAnimation(EllipseGeometry.RadiusXProperty,null);
            hit.Circle.BeginAnimation(EllipseGeometry.RadiusYProperty,null);
        }

        void StopFlightFrames()
        {
            if(!flightFrames)return;
            UiFrames.Rendering-=AdvanceFlights;
            flightFrames=false;lastFlightFrame=null;
        }

        void SettleFlights()
        {
            StopFlightFrames();
            lensedFlyers.Children.Clear();
            SettleInfall();
            rearTransit.Children.Clear();
            rearTransit.Children.Add(streamBack);
            if(appFlights.Count==0 && landings.Count==0)return;
            appFlights.Clear(); finishedFlights.Clear(); flight.Children.Clear();
            foreach(string path in landings.Keys.ToArray()) CancelLanding(path);
            QueueChart();
        }
    }
}
