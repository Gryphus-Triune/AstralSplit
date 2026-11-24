using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        sealed class Infall
        {
            internal const double Approach=420,Orbit=1100,Plunge=160,Duration=Approach+Orbit+Plunge;
            const int Count=14;
            internal readonly DrawingGroup Art=new DrawingGroup(),Rear=new DrawingGroup();
            internal readonly AccretionPath Channel;
            internal double Start;
            internal double Scale {get{return Channel.Scale;}}
            readonly Color source;
            readonly Point[] left=new Point[Count],right=new Point[Count];
            readonly bool[] far=new bool[Count];
            readonly Point[] samples=new Point[Count];
            double retractAt,heldAge,resumeAt=Double.NegativeInfinity,drainAt=Double.PositiveInfinity,drainAge;
            Vector correction;
            SelectionFlight returning;
            internal bool Retracting {get{return returning!=null;}}
            internal bool Impacted;
            internal Infall(AccretionPath channel,Color color,double now)
            {
                Channel=channel;source=color;Start=now;Draw(now);
            }
            internal void Retract(SelectionFlight flyer,double now)
            {
                heldAge=Age(now);retractAt=now;returning=flyer;
            }
            internal void Resume(double now)
            {
                var at=returning.Position(now);
                returning=null;Start=now-heldAge;resumeAt=now;
                correction=at-Raw(heldAge);
            }
            internal void Drain(double now)
            {
                if(Retracting || drainAt!=Double.PositiveInfinity)return;
                drainAge=Age(now);drainAt=now;
            }
            internal bool Done(double now){return Retracting?now-retractAt>=180:Age(now)>=Duration;}
            internal double Age(double now)
            {
                if(Retracting)return heldAge;
                return drainAt==Double.PositiveInfinity?Math.Max(0,now-Start):drainAge+(now-drainAt)*Math.Max(1,(Duration-drainAge)/400);
            }
            Point Raw(double age){return Channel.Seen(Channel.Position(age,Approach,Orbit,Plunge,1.75),age<Approach);}
            Point Corrected(double age,double now)
            {
                double blend=1-UiMotion.Unit((now-resumeAt)/180);
                return Raw(age)+correction*(blend*blend*(3-2*blend));
            }
            internal Point Position(double now){return Retracting?returning.Position(now):Corrected(Age(now),now);}
            internal bool IsBehind(Point at,double now){bool entry=Age(now)<Approach;return Channel.Behind(Channel.InPlane(at,entry),entry);}
            double DiskHeat(double age){return age<=Approach+Orbit?.9*UiMotion.Unit((age-Approach)/Orbit):.9+.1*UiMotion.Unit((age-Approach-Orbit)/Plunge);}
            internal Color Tint(double now)
            {
                double age=Age(now);return ThermalColor(source,UiMotion.Unit(age/Approach),DiskHeat(age));
            }
            internal void Draw(double now)
            {
                Channel.UpdateProjection();
                double age=Age(now),heat=DiskHeat(age),redshift=UiMotion.Unit((age-Approach-Orbit)/Plunge);
                double shrink=Retracting?UiMotion.Unit((now-retractAt)/180):0;
                double growth=UiMotion.Unit((now-resumeAt)/145);
                var head=Position(now);
                double delay=10*(1-shrink)*growth;
                Vector translation=Retracting?head-Corrected(age,retractAt):new Vector();
                for(int i=0;i<Count;i++){
                    double behind=Count-1-i,part=Math.Max(0,age-behind*delay);
                    // Expand the head-to-tail distance as tidal shear grows. Sampling the actual
                    // curve (rather than drawing a long ellipse) keeps the band wrapped on the orbit.
                    part=Math.Max(0,age-(age-part)*(1+2.4*heat*heat));
                    samples[i]=Corrected(part,Retracting?retractAt:now)+translation;
                    far[i]=Channel.Behind(Channel.InPlane(samples[i],part<Approach),part<Approach);
                }
                for(int i=0;i<Count;i++){
                    var v=UnitTangent(samples[Math.Min(Count-1,i+1)]-samples[Math.Max(0,i-1)]);
                    double width=(.6+4.2*i/(Count-1.0))*.5*Scale/CoreMagnification*(1-.36*heat)*(1-shrink);
                    var n=new Vector(-v.Y,v.X)*width;left[i]=samples[i]+n;right[i]=samples[i]-n;
                }
                using(var back=Rear.Open())using(var front=Art.Open()){
                    int start=0;
                    for(int i=1;i<Count;i++)if(far[i]!=far[i-1]){
                        Band(far[i-1]?back:front,start,i,age,redshift,shrink);start=i;
                    }
                    Band(far[Count-1]?back:front,start,Count-1,age,redshift,shrink);
                    if(!Retracting){
                        var dc=far[Count-1]?back:front;
                        var dir=UnitTangent(Raw(age+1)-Raw(age-1));
                        double side=age<Approach?0:Channel.Doppler(Channel.InPlane(head,false));
                        var tint=DopplerColor(Tint(now),side,heat>0?1:0);double radius=Scale/CoreMagnification;
                        dc.PushOpacity(Math.Min(1,.8*DopplerGain(side))*(1-redshift));dc.PushTransform(Streak(head,dir,(1+2.4*heat*heat)*radius,(1-.36*heat)*radius));
                        dc.DrawEllipse(Glow(A(tint,.76)),null,new Point(),4,3.2);
                        dc.DrawEllipse(Ink(HeatMix(tint,Colors.White,.23)),null,new Point(),1.9,1.4);dc.Pop();dc.Pop();
                    }
                }
            }
            void Band(DrawingContext dc,int first,int last,double age,double redshift,double shrink)
            {
                if(first==last || redshift>=1 || shrink>=1)return;
                var geometry=new StreamGeometry();
                using(var g=geometry.Open()){
                    g.BeginFigure(left[first],true,true);
                    for(int i=first+1;i<=last;i++)g.LineTo(left[i],true,true);
                    for(int i=last;i>=first;i--)g.LineTo(right[i],true,true);
                }
                geometry.Freeze();
                double heat=DiskHeat(age),side=age<Approach?0:Channel.Doppler(Channel.InPlane(samples[last],false));
                var color=DopplerColor(ThermalColor(source,UiMotion.Unit(age/Approach),heat),side,heat>0?1:0);
                double strength=Math.Min(1,.82*DopplerGain(side))*(1-redshift)*(1-shrink);
                double a=first/(Count-1.0),b=last/(Count-1.0);
                var brush=new LinearGradientBrush(A(color,strength*(.12+.88*a)),A(color,strength*(.12+.88*b)),0){
                    MappingMode=BrushMappingMode.Absolute,StartPoint=samples[first],EndPoint=samples[last]};
                brush.Freeze();dc.DrawGeometry(brush,null,geometry);
            }
        }

        sealed class DiskImpact
        {
            internal readonly DrawingGroup Near=new DrawingGroup(),Far=new DrawingGroup();
            readonly AccretionPath path;
            readonly double start;
            readonly Color source;
            const double Lifetime=4200;
            double drainTime=Double.PositiveInfinity;
            static readonly Brush ember=Glow(Color.FromRgb(255,146,70)),white=Glow(Colors.White);
            internal DiskImpact(AccretionPath path,double now,Color pigment){this.path=path;start=now;source=pigment;Draw(now);}
            internal void Drain(double now){if(drainTime==Double.PositiveInfinity)drainTime=now;}
            double Elapsed(double now){return drainTime==Double.PositiveInfinity?now-start:drainTime-start+(now-drainTime)*Math.Max(1,(Lifetime-(drainTime-start))/400);}
            internal bool Done(double now){return Elapsed(now)>=Lifetime;}
            void Debris(DrawingContext back,DrawingContext front,double elapsed){
                double u=UiMotion.Unit(elapsed/Lifetime);
                if(u<=0 || u>=1)return;
                double warmth=UiMotion.Unit((u-.10)/.72),alpha=Math.Min(1,elapsed/150)*Math.Pow(1-u,.82);
                var pigment=HeatMix(source,Color.FromRgb(255,233,192),warmth);
                var line=Stroke(pigment,.79*alpha,path.Scale*(1.58-1.30*u));
                var haze=Stroke(pigment,.17*alpha,path.Scale*(3.75-2.90*u));
                var rear=new StreamGeometry();var near=new StreamGeometry();
                using(var far=rear.Open())using(var close=near.Open()){
                    Point prev=path.Seen(path.Entry,false);bool prior=false;
                    for(int i=0;i<=54;i++){
                        double q=i/54.0,lead=UiMotion.Unit(elapsed/440),r=AccretionPath.Outer-(6+11*q)*u-2*q*lead;
                        // Inner material overtakes the outer end and winds the same ribbon thinner.
                        double a=path.EntryAngle+u*(6.8+4.7*q)*Math.Pow(AccretionPath.Outer/r,1.5)+.83*q*lead;
                        var p=path.DiskPoint(r,a);bool behind=path.Behind(p,false);
                        p=path.Seen(p,false);var target=behind?far:close;
                        if(i==0 || behind!=prior)target.BeginFigure(i==0?p:prev,false,false);
                        if(i!=0)target.LineTo(p,true,true);prior=behind;prev=p;
                    }
                }
                rear.Freeze();near.Freeze();
                back.DrawGeometry(null,haze,rear);back.DrawGeometry(null,line,rear);
                front.DrawGeometry(null,haze,near);front.DrawGeometry(null,line,near);
                if(elapsed<1350){
                    double t=elapsed/1350;Vector tangent=UnitTangent(path.DiskPoint(AccretionPath.Outer,path.EntryAngle+.015)-path.Entry);
                    var tail=new StreamGeometry();var outward=UnitTangent(path.Entry-path.Gate);
                    using(var p=tail.Open()){
                        Point basePoint=path.Entry+(t*t*31*path.Scale)*outward;
                        p.BeginFigure(basePoint,false,false);
                        p.QuadraticBezierTo(basePoint+tangent*(18*t*path.Scale),basePoint+(tangent*37+outward*44)*(t*path.Scale),true,true);
                    }
                    tail.Freeze();back.PushTransform(path.DiskView);back.DrawGeometry(null,Stroke(source,.48*Math.Sin(Math.PI*t),(.8-.5*t)*path.Scale),tail);back.Pop();
                }
            }
            internal void Draw(double now)
            {
                path.UpdateProjection();double elapsed=Elapsed(now),t=UiMotion.Unit((elapsed-270)/600);
                using(var back=Far.Open())using(var front=Near.Open()){
                    Debris(back,front,elapsed);
                    if(elapsed<270){
                        var dc=path.Behind(path.Entry,false)?back:front;
                        dc.PushOpacity(elapsed<39?UiMotion.Unit(elapsed/39):Math.Pow(1-UiMotion.Unit((elapsed-39)/231),2));
                        dc.PushTransform(path.DiskView);var tilt=new RotateTransform(-11,path.Entry.X,path.Entry.Y);tilt.Freeze();dc.PushTransform(tilt);
                        dc.DrawEllipse(ember,null,path.Entry,14*path.Scale,5*path.Scale);dc.DrawEllipse(white,null,path.Entry,6*path.Scale,2*path.Scale);
                        dc.Pop();dc.Pop();dc.Pop();
                    }
                    if(elapsed<270 || elapsed>=870)return;
                    var at=path.DiskPoint(AccretionPath.Outer, path.EntryAngle+t*2*Math.PI);
                    var active=path.Behind(at,false)?back:front;
                    at=path.Seen(at,false);active.PushOpacity(.95*(1-t));active.DrawEllipse(ember,null,at,4.1*path.Scale,2.7*path.Scale);
                    active.DrawEllipse(white,null,at,2.3*path.Scale,1.4*path.Scale);active.Pop();
                }
            }
        }

        readonly Dictionary<string,Infall> infalls=new Dictionary<string,Infall>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,DiskImpact> diskHits=new Dictionary<string,DiskImpact>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> fallOnArrival=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DrawingGroup infallLayer=new DrawingGroup(),rearTransit=new DrawingGroup();
        readonly List<string> arrivedAtDisk=new List<string>();
        void AdvanceInfall(double now)
        {
            foreach(var pair in infalls){
                var f=pair.Value;
                if(!f.Retracting && !f.Impacted && f.Age(now)>=Infall.Approach && Live && slots.ContainsKey(pair.Key)){
                    f.Impacted=true;FlashDisk(pair.Key,f.Channel,now);
                }
                if(f.Done(now))arrivedAtDisk.Add(pair.Key);else f.Draw(now);
            }
            bool settled=arrivedAtDisk.Count>0;
            foreach(string path in arrivedAtDisk)RemoveFall(path);arrivedAtDisk.Clear();
            foreach(var pair in diskHits){if(pair.Value.Done(now))arrivedAtDisk.Add(pair.Key);else pair.Value.Draw(now);}
            foreach(string path in arrivedAtDisk)RemoveHit(path);arrivedAtDisk.Clear();
            if(settled)QueueChart();
        }
        void FlashDisk(string path,AccretionPath channel,double now)
        {
            RemoveHit(path);
            var hit=new DiskImpact(channel,now,AppInk(path));diskHits[path]=hit;
            rearTransit.Children.Add(hit.Far);infallLayer.Children.Add(hit.Near);
        }
        void RemoveFall(string path)
        {
            if(!infalls.TryGetValue(path,out var f))return;
            infallLayer.Children.Remove(f.Art);rearTransit.Children.Remove(f.Rear);infalls.Remove(path);
        }
        void RemoveHit(string path)
        {
            if(!diskHits.TryGetValue(path,out var hit))return;
            rearTransit.Children.Remove(hit.Far);infallLayer.Children.Remove(hit.Near);diskHits.Remove(path);
        }
        void CancelInfall(string path){RemoveFall(path);RemoveHit(path);fallOnArrival.Remove(path);}
        void SettleInfall()
        {
            foreach(var path in diskHits.Keys.ToArray())RemoveHit(path);
            foreach(var path in infalls.Keys.ToArray())RemoveFall(path);
            fallOnArrival.Clear();arrivedAtDisk.Clear();
            if(appFlights.Count==0)StopFlightFrames();
        }
    }
}
