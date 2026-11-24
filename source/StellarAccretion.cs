using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        // Both a persistent emitter and a freshly selected star follow the same disk plane.
        sealed class AccretionPath
        {
            // The outside ten percent of the 77-unit gas texture fades to transparent.
            // Land where it still emits light, using the same projection as the two gas plates.
            internal const double OuterPlate=77, InnerPlate=48, Outer=OuterPlate*.90, Inner=22;
            internal const double OuterMidline=3.9, InnerMidline=4.9, OuterFlattening=.127, InnerFlattening=.15;
            internal const double StreamOrbit=990, StreamPlunge=110, StreamTurns=1, StreamDiskTime=StreamOrbit+StreamPlunge;
            internal const double CosTilt=.981627183447664, SinTilt=.190808995376545;
            internal Point Center,Star,Entry;
            internal Point Gate {get;private set;}
            internal double Scale {get;private set;}
            internal readonly double TipRadius;
            internal readonly bool Orbital;
            internal double EntryAngle;
            internal AccretionCamera Camera;
            internal readonly MatrixTransform ApproachView=new MatrixTransform(),DiskView=new MatrixTransform();
            int cameraRevision=-1;
            Point World(Point local){return Gate+Scale*(local-new Point());}
            Point Local(Point world){return new Point((world.X-Gate.X)/Scale,(world.Y-Gate.Y)/Scale);}
            internal void UpdateProjection(){
                if(Camera==null || Scale<=0 || cameraRevision==Camera.Revision)return;
                cameraRevision=Camera.Revision;
                var at=Local(Entry);var target=World(Camera.Project(at));
                Vector ex=Camera.Project(at+new Vector(.01,0))-Camera.Project(at);
                Vector ey=Camera.Project(at+new Vector(0,.01))-Camera.Project(at);
                var disk=new Matrix(ex.X*100,ex.Y*100,ey.X*100,ey.Y*100,0,0);
                Vector shift=target-disk.Transform(Entry);disk.Translate(shift.X,shift.Y);DiskView.Matrix=disk;
                // Match the projected tangential landing and preserve the star-side anchor.
                Vector travel=Entry-Star,tangent=Entry-second;
                var basis=new Matrix(travel.X,travel.Y,tangent.X,tangent.Y,0,0);
                var contact=target-Star;var endTangent=disk.Transform(tangent);
                if(Math.Abs(basis.Determinant)>travel.Length*tangent.Length*.08){
                    basis.Invert();basis.Append(new Matrix(contact.X,contact.Y,endTangent.X,endTangent.Y,0,0));
                }else{
                    double length=travel.LengthSquared;if(length<.001){ApproachView.Matrix=Matrix.Identity;return;}
                    Vector delta=contact-travel;
                    basis=new Matrix(1+travel.X*delta.X/length,travel.X*delta.Y/length,travel.Y*delta.X/length,1+travel.Y*delta.Y/length,0,0);
                }
                shift=Star-basis.Transform(Star);basis.Translate(shift.X,shift.Y);ApproachView.Matrix=basis;
            }
            internal Point Seen(Point at,bool approach){
                if(Camera==null || Scale<=0)return at;
                UpdateProjection();return approach?ApproachView.Transform(at):World(Camera.Project(Local(at)));
            }
            internal Point InPlane(Point at,bool approach){
                if(Camera==null || Scale<=0)return at;
                if(!approach)return World(Camera.Unproject(Local(at)));
                UpdateProjection();var m=ApproachView.Matrix;if(!m.HasInverse)return at;m.Invert();return m.Transform(at);
            }
            Point first, second;
            double angularLead;
            internal double NativeLead{get{return angularLead;}}
            internal Point FirstControl {get{return first;}}
            internal Point SecondControl {get{return second;}}
            internal AccretionPath(Point center,Point gate,double scale,double size,double angle,bool orbital=false)
            {
                Orbital=orbital;
                if(orbital){TipRadius=size;MoveOrbitalSource(center,gate,scale,angle);return;}
                Center=center;Gate=gate;Scale=scale;EntryAngle=angle;TipRadius=size;
                Vector toGate=gate-center;
                if(toGate.LengthSquared>1e-8)toGate.Normalize();
                Star=center+toGate*(2.2*size);
                Entry=DiskPoint(Outer,angle);
                var v=Star-Gate;
                double starX=(v.X*CosTilt-v.Y*SinTilt)/Scale;
                double x=Outer*Math.Cos(angle),dx=-Outer*Math.Sin(angle);
                double gap=Math.Max(0,-Outer-starX);
                // Aim at the backward tangent of this star's own contact. The control
                // stays outside the disk and ahead of the source in disk-space X.
                angularLead=(x+Outer+Math.Min(23,.32*gap))/dx;
                second=Entry-ProjectVector(dx,OuterFlattening*x)*angularLead;
                double dxToSecond=(second-Star).X*CosTilt-(second-Star).Y*SinTilt;
                double dxToGate=Math.Max(.01,toGate.X*CosTilt-toGate.Y*SinTilt);
                first=Star+toGate*(.40*Math.Max(0,dxToSecond)/dxToGate);
            }
            internal void MoveOrbitalSource(Point center,Point gate,double scale,double angle){
                Gate=gate;Scale=scale;
                Center=center;EntryAngle=angle;Entry=DiskPoint(Outer,angle);
                var inward=UnitTangent(Gate-center);Star=center+inward*(2.2*TipRadius);
                var tangent=ProjectVector(-Outer*Math.Sin(angle),Outer*OuterFlattening*Math.Cos(angle));
                double distance=(Star-Entry).Length;
                angularLead=Math.Min(2.1,Math.Max(.38,.27*distance/Math.Max(1,tangent.Length)));
                second=Entry-tangent*angularLead;first=Star+inward*(.40*distance);
                // The source is moving even when the camera attitude stays the same.
                cameraRevision=-1;UpdateProjection();
            }
            internal Point ApproachPoint(double t)
            {
                double q=1-t;
                return new Point(q*q*q*Star.X+3*q*q*t*first.X+3*q*t*t*second.X+t*t*t*Entry.X,
                    q*q*q*Star.Y+3*q*q*t*first.Y+3*q*t*t*second.Y+t*t*t*Entry.Y);
            }
            Vector ProjectVector(double x,double y){return new Vector((CosTilt*x+SinTilt*y)*Scale,(-SinTilt*x+CosTilt*y)*Scale);}
            internal Point DiskPoint(double radius,double angle)
            {
                double blend=UiMotion.Unit((Outer-radius)/(Outer-InnerPlate));
                double mid=OuterMidline+(InnerMidline-OuterMidline)*blend;
                double flatten=OuterFlattening+(InnerFlattening-OuterFlattening)*blend;
                return Gate+ProjectVector(radius*Math.Cos(angle),mid+flatten*radius*Math.Sin(angle));
            }
            // Tangential contact starts with zero radial velocity. The primitive
            // of r^(-3/2) for r=Outer-(Outer-Inner)u² gives this closed-form angle.
            internal double Angle(double u,double turns)
            {
                double r=Outer+(Inner-Outer)*u*u;
                return EntryAngle+2*Math.PI*turns*u*Math.Sqrt(Inner/r);
            }
            internal Point OrbitPoint(double u,double turns){return DiskPoint(Outer+(Inner-Outer)*u*u,Angle(u,turns));}
            internal Point Position(double age,double approach,double orbit,double plunge,double turns)
            {
                if(age<=approach){
                    double travel=UiMotion.Unit(age/approach);
                    double endSpeed=2*Math.PI*turns*Math.Sqrt(Inner/Outer)*approach/(3*orbit*angularLead);
                    // Zero release speed and the orbital entry speed at t=1, for both emitters.
                    return ApproachPoint(endSpeed>3?Math.Pow(travel,endSpeed):travel*travel*(3-endSpeed+(endSpeed-2)*travel));
                }
                return OnDisk(age-approach,orbit,plunge,turns);
            }
            Point OnDisk(double elapsed,double orbit,double plunge,double turns)
            {
                DiskPolar(elapsed,orbit,plunge,turns,out double radius,out double sweep);
                return DiskPoint(radius,EntryAngle+sweep);
            }
            internal static void DiskPolar(double elapsed,double orbit,double plunge,double turns,out double radius,out double sweep)
            {
                double t=elapsed/orbit;
                if(t<1){
                    radius=Outer+(Inner-Outer)*t*t;
                    sweep=2*Math.PI*turns*t*Math.Sqrt(Inner/radius);
                }else{
                    double end=UiMotion.Unit((elapsed-orbit)/plunge);
                    radius=Inner-6*end;sweep=2*Math.PI*turns+1.1*end;
                }
            }
            internal Point StreamDiskPoint(double fraction){return OnDisk(fraction*StreamDiskTime,StreamOrbit,StreamPlunge,StreamTurns);}
            internal Point StreamPosition(double age,double approach){return Position(age,approach,StreamOrbit,StreamPlunge,StreamTurns);}
            internal Point StreamPose(double age,double approach,out Vector tangent)
            {
                if(age<=approach){
                    double t=UiMotion.Unit(age/approach);
                    double speed=2*Math.PI*StreamTurns*Math.Sqrt(Inner/Outer)*approach/(3*StreamOrbit*angularLead);
                    t=speed>3?Math.Pow(t,speed):t*t*(3-speed+(speed-2)*t);
                    double q=1-t;
                    tangent=UnitTangent(q*q*(first-Star)+2*q*t*(second-first)+t*t*(Entry-second));
                    return ApproachPoint(t);
                }
                double elapsed=age-approach,r,theta,dr,dtheta;
                if(elapsed<StreamOrbit){
                    double u=elapsed/StreamOrbit;
                    r=Outer+(Inner-Outer)*u*u;dr=2*(Inner-Outer)*u;
                    double speed=2*Math.PI*StreamTurns*Math.Sqrt(Inner/r);
                    theta=EntryAngle+speed*u;dtheta=speed*(1-u*dr/(2*r));
                }else{
                    double u=UiMotion.Unit((elapsed-StreamOrbit)/StreamPlunge);
                    r=Inner-6*u;dr=-6;theta=EntryAngle+2*Math.PI*StreamTurns+1.1*u;dtheta=1.1;
                }
                double c=Math.Cos(theta),s=Math.Sin(theta),blend=UiMotion.Unit((Outer-r)/(Outer-InnerPlate));
                double db=r>InnerPlate && r<=Outer?-dr/(Outer-InnerPlate):0;
                double mid=OuterMidline+(InnerMidline-OuterMidline)*blend,flat=OuterFlattening+(InnerFlattening-OuterFlattening)*blend;
                tangent=UnitTangent(ProjectVector(dr*c-r*s*dtheta,(InnerMidline-OuterMidline)*db+
                    (dr*s+r*c*dtheta)*flat+r*s*(InnerFlattening-OuterFlattening)*db));
                return Gate+ProjectVector(r*c,mid+flat*r*s);
            }
            internal bool Behind(Point at,bool approaching){
                if(approaching){if(!Orbital)return true;at=Entry;}
                var v=(at-Gate)/Scale;
                return DiskDepth(v,Camera)<0;
            }
            internal static double DiskDepth(Vector v,AccretionCamera camera){
                double x=v.X*CosTilt-v.Y*SinTilt;
                // At either node the disk radius is |x|, so its depth boundary follows the plate midline.
                double mid=OuterMidline+(InnerMidline-OuterMidline)*UiMotion.Unit((Outer-Math.Abs(x))/(Outer-InnerPlate));
                if(camera!=null)return camera.Depth(v-new Vector(SinTilt,CosTilt)*mid);
                return v.X*SinTilt+v.Y*CosTilt-mid;
            }
            // front-disk.png has its bright limb at negative disk-space x.
            internal double Doppler(Point at){var v=at-Gate;return Math.Max(-1,Math.Min(1,-(v.X*CosTilt-v.Y*SinTilt)/(35*Scale)));}
            internal const int RiverSteps=28;
            internal void RiverEdge(int step,out Point top,out Point bottom)
            {
                double t=step/(double)RiverSteps;var p=ApproachPoint(t);
                var v=UnitTangent(ApproachPoint(Math.Min(1,t+.001))-ApproachPoint(Math.Max(0,t-.001)));
                var w=new Vector(-v.Y,v.X)*(1.1-.7*t);
                top=p+w;bottom=p-w;
            }
            Geometry fullRiver;
            internal Geometry River()
            {
                if(fullRiver!=null)return fullRiver;
                const int n=RiverSteps;var edges=new Point[n+1];var geometry=new StreamGeometry();
                using(var writer=geometry.Open()){
                    for(int i=0;i<=n;i++){
                        RiverEdge(i,out var top,out edges[i]);
                        if(i==0)writer.BeginFigure(top,true,true);else writer.LineTo(top,true,false);
                    }
                    for(int i=n;i>=0;i--)writer.LineTo(edges[i],true,false);
                }
                geometry.Freeze();return fullRiver=geometry;
            }
        }

        double EntryAngleFor(string path,Point center,Point gate)
        {
            double Bearing(Point p){return (p.Y-gate.Y)/Math.Max(1,gate.X-p.X);}
            int slot=slots[path],rank=0;double bearing=Bearing(center);
            foreach(var item in slots.Values){
                if(item==slot)continue;
                double other=Bearing(Project(Anchors[item].X,Anchors[item].Y));
                if(other<bearing || (other==bearing && item<slot))rank++;
            }
            double slope=UiMotion.Unit(bearing/.65);
            double lower=slots.Count<2?slope:.70*rank/(slots.Count-1.0)+.30*slope;
            return 3.75-.32*lower;
        }
        readonly Dictionary<string,AccretionPath> channels=new Dictionary<string,AccretionPath>(StringComparer.OrdinalIgnoreCase);
        AccretionPath CapturePath(string path,Point center)
        {
            var gate=GateCenter;double radius=AppStarRadius(path),angle=EntryAngleFor(path,center,gate);
            if(!channels.TryGetValue(path,out var result)||result.Center!=center||result.Gate!=gate||result.Scale!=CoreScale||result.EntryAngle!=angle||result.TipRadius!=radius)
            {
                channels[path]=result=new AccretionPath(center,gate,CoreScale,radius,angle){Camera=diskAttitude};
                result.UpdateProjection();
            }
            return result;
        }
        // A growing ribbon keeps the original sample edges. Only its two leading
        // vertices move between samples; the absolute source-to-disk brush stays fixed.
        sealed class RiverTrace
        {
            internal readonly GeometryDrawing Art=new GeometryDrawing();
            internal readonly DiskRibbon Disk=new DiskRibbon();
            internal const double Contact=.55;
            AccretionPath channel;
            Color color;
            PolyLineSegment upper,lower;
            LineSegment topTip,bottomTip;
            int step;
            Point top,bottom,nextTop,nextBottom;
            internal void Place(AccretionPath next,Color ink,double progress)
            {
                if(channel!=next || color!=ink){
                    Disk.Place(next);
                    channel=next;color=ink;upper=null;
                    var brush=new LinearGradientBrush(A(ink,.30),A(outerHeat,.55),0){MappingMode=BrushMappingMode.Absolute,
                        StartPoint=next.Star,EndPoint=next.Entry};brush.Freeze();Art.Brush=brush;
                }
                Reveal(progress);
            }
            internal void Reveal(double progress)
            {
                if(channel==null)return;
                Disk.Reveal(UiMotion.Unit((progress-Contact)/(1-Contact)));
                progress=UiMotion.Unit(progress/Contact);
                if(progress<=0 || progress>=1){
                    upper=null;Art.Geometry=progress<=0?Geometry.Empty:channel.River();return;
                }
                double position=progress*AccretionPath.RiverSteps;
                int target=(int)position;
                if(upper==null || target<step){
                    step=0;channel.RiverEdge(0,out top,out bottom);channel.RiverEdge(1,out nextTop,out nextBottom);
                    upper=new PolyLineSegment();lower=new PolyLineSegment();
                    topTip=new LineSegment();bottomTip=new LineSegment();lower.Points.Add(bottom);
                    var figure=new PathFigure{StartPoint=top,IsClosed=true,IsFilled=true};
                    figure.Segments.Add(upper);figure.Segments.Add(topTip);figure.Segments.Add(bottomTip);figure.Segments.Add(lower);
                    var geometry=new PathGeometry();geometry.Figures.Add(figure);Art.Geometry=geometry;
                }
                while(step<target){
                    ++step;top=nextTop;bottom=nextBottom;
                    upper.Points.Add(top);lower.Points.Insert(0,bottom);
                    channel.RiverEdge(step+1,out nextTop,out nextBottom);
                }
                double fraction=position-step;
                topTip.Point=top+(nextTop-top)*fraction;bottomTip.Point=bottom+(nextBottom-bottom)*fraction;
            }
        }

        static readonly DependencyProperty RiverProgressProperty=DependencyProperty.Register("RiverProgress",typeof(double),typeof(StarfieldLayer),
            new PropertyMetadata(0d,(d,e)=>((StarfieldLayer)d).PaintRiverProgress((double)e.NewValue)));
        double riverProgress;
        bool diskGathering,riverRevealStarted;
        void PaintRiverProgress(double value)
        {
            riverProgress=value;
            foreach(var marker in markerLayout)if(marker.River!=null)marker.River.Reveal(value);
            if(value==1 && Live && CanMoveMatter){UpdateTrafficLights();LaunchReadyInfalls();}
            else if(observing && !observatoryFrames)DrawObservingSky(flightClock.Elapsed.TotalSeconds);
        }
        void SettleRivers(bool open)
        {
            riverRevealStarted=diskGathering=open;
            SetValue(RiverProgressProperty,open?1d:0d);
            BeginAnimation(RiverProgressProperty,null);
        }
        void TryRevealRivers()
        {
            if(!Live || riverRevealStarted || !diskGathering || holeScale.ScaleX<1 || !CanMoveMatter)return;
            riverRevealStarted=true;
            var grow=new DoubleAnimationUsingKeyFrames { Duration=TimeSpan.FromMilliseconds(2250) };
            grow.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            grow.KeyFrames.Add(new SplineDoubleKeyFrame(RiverTrace.Contact,TimeSpan.FromMilliseconds(1250),new KeySpline(.35,0,.75,.825)));
            grow.KeyFrames.Add(new SplineDoubleKeyFrame(1,TimeSpan.FromMilliseconds(2250),new KeySpline(.22,.15,.4,1)));
            BeginAnimation(RiverProgressProperty,UiMotion.AtRefresh(grow,refreshRate),HandoffBehavior.SnapshotAndReplace);
        }

        static readonly Color outerHeat=Color.FromRgb(255,180,105),middleHeat=Color.FromRgb(255,233,196),
            innerHeat=Color.FromRgb(220,232,255),finalHeat=Color.FromRgb(255,90,50);
        static Color HeatMix(Color a,Color b,double t){return Color.FromRgb((byte)Math.Round(a.R+(b.R-a.R)*t),
            (byte)Math.Round(a.G+(b.G-a.G)*t),(byte)Math.Round(a.B+(b.B-a.B)*t));}
        static Color ThermalColor(Color source,double travel,double disk)
        {
            if(travel<1)return HeatMix(source,outerHeat,travel);
            if(disk<=.45)return HeatMix(outerHeat,middleHeat,disk/.45);
            if(disk<=.9)return HeatMix(middleHeat,innerHeat,(disk-.45)/.45);
            return HeatMix(innerHeat,finalHeat,Math.Min(1,(disk-.9)/.1));
        }
        static Color DopplerColor(Color c,double side,double heat){return HeatMix(c,side>=0?Color.FromRgb(194,226,255):Color.FromRgb(255,137,102),.16*Math.Abs(side)*heat);}
        static double DopplerGain(double side){return side>=0?1+.25*side:1+.30*side;}
        static Vector UnitTangent(Vector v){if(v.LengthSquared<1e-10)return new Vector(1,0);v.Normalize();return v;}
        static MatrixTransform Streak(Point at,Vector dir,double length,double width){var m=new MatrixTransform(dir.X*length,dir.Y*length,-dir.Y*width,dir.X*width,at.X,at.Y);m.Freeze();return m;}
    }
}
