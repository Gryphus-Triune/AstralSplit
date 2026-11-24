using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly SpringProgress collapseGesture=new SpringProgress();
        // The apparent optical counter-image sits closer to the sky plane. The jets follow the disk normal.
        readonly AccretionCamera diskAttitude=new AccretionCamera(4.25),jetAttitude=new AccretionCamera(-1/4.25),
            photonAttitude=new AccretionCamera(.42);
        double cameraX=double.NaN,cameraY,cameraSqueeze;
        readonly DrawingGroup magneticRear=new DrawingGroup(),magneticNear=new DrawingGroup();
        readonly DrawingGroup deepSky=new DrawingGroup(),survey=new DrawingGroup(),observingSky=new DrawingGroup();
        readonly ObservatoryRibbons observingRibbons=new ObservatoryRibbons();
        readonly DrawingGroup observingNearApproaches=new DrawingGroup();
        readonly ImageDrawing ionizedPlate=new ImageDrawing();
        readonly DrawingGroup ionizedLight=new DrawingGroup { Opacity=.50,
            OpacityMask=new RadialGradientBrush(Colors.White,Colors.Transparent){RadiusX=.19,RadiusY=.27} };
        static readonly Lazy<Task<BitmapSource>> ionizedTexture=new Lazy<Task<BitmapSource>>(
            ()=>Task.Run(()=>StellarSkyArt.ReadImage("ionized-nebula.png")));
        readonly Dictionary<string,OrbitalStar> orbitalStars=new Dictionary<string,OrbitalStar>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> observingCatalog=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> surveyCatalog=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly List<ChartMarker> surveyExtras=new List<ChartMarker>();
        FlowBatch magneticBatch;
        bool observing,observatoryFrames,magneticGpu;
        bool draggingHole,releasingGestureForPhase;
        double closingGesture;
        double gestureMass,fieldPhase,fieldRate,lastObservatoryTime,terminalRippleAt=double.NegativeInfinity;
        double foldingRippleAt=double.NegativeInfinity;
        double standbyRippleAt=double.NegativeInfinity;
        const double StandbyRippleDuration=.48;
        int stage,fieldFallback=-1;
        TimeSpan observatoryFrame=TimeSpan.MinValue;
        Vector windowLag,windowVelocity;
        string rowAim,starAim,chartSearch="";
        Point rowEdge;
        internal Point? SurveyRowIcon {get;set;}
        internal event Action<string> Inspected;
        internal event Action<Vector> GlassMotion;
        internal event Action<Point,double,double> GlassWave;
        bool waveOnGlass;

        void InitializeObservatory(){
            collapseGesture.ValueChanged+=()=>{
                if(disposed)return;
                gestureMass=draggingHole?0:collapseGesture.Value;
                closingGesture=draggingHole && Live?collapseGesture.Value:0;
                ChangeAttitude();
                if(releasingGestureForPhase || evaporationRunning || formationRunning)return;
                if(Live){
                    holeScale.ScaleX=holeScale.ScaleY=1-.34*closingGesture;
                    SetCoreLens(new LensState(holeScale.ScaleX,1,1));
                }else
                    SetCoreLens(new LensState(StandbyLensMass+(.54-StandbyLensMass)*gestureMass,gestureMass,1));
            };
        }
        void ReleaseGestureForPhase()
        {
            releasingGestureForPhase=true;
            try {
                draggingHole=false;
                collapseGesture.MoveTo(0,false,18,refreshRate);
            }
            finally { releasingGestureForPhase=false; }
        }
        internal void Gesture(double? fraction){
            if(fraction.HasValue){
                if(!CanMoveMatter || formationRunning || evaporationRunning)return;
                draggingHole=Live;
            }
            double amount=fraction.HasValue?UiMotion.Unit(draggingHole?1-fraction.Value:fraction.Value):0;
            collapseGesture.MoveTo(amount,!fraction.HasValue && running,18,refreshRate);
        }
        internal void WindowImpulse(Vector displacement,double seconds){
            if(!running || !SystemParameters.ClientAreaAnimation || seconds<=0)return;
            double dt=Math.Max(.008,seconds);
            windowVelocity-=displacement*(.92*(1-Math.Exp(-14*dt))/dt);
            if(windowVelocity.Length>225)windowVelocity*=225/windowVelocity.Length;
            SyncObservatory();
        }
        void ChangeAttitude(){
            double x=Math.Max(-1,Math.Min(1,tiltLook.X+windowLag.X/70)),y=Math.Max(-1,Math.Min(1,tiltLook.Y+windowLag.Y/70));
            double squeeze=1-.34*closingGesture;
            if(x==cameraX && y==cameraY && squeeze==cameraSqueeze)return;
            cameraX=x;cameraY=y;cameraSqueeze=squeeze;
            var orientation=Matrix3D.Identity;
            orientation.Rotate(new Quaternion(new Vector3D(0,1,0),8*x));
            orientation.Rotate(new Quaternion(new Vector3D(1,0,0),-8*y));
            orientation.Rotate(new Quaternion(new Vector3D(0,0,1),8*x));
            diskAttitude.Orient(orientation,squeeze);jetAttitude.Orient(orientation,squeeze);
            photonAttitude.Orient(orientation,squeeze);
            foreach(var lane in channels.Values)lane.UpdateProjection();
        }
        void SyncObservatory(){
            if(Live || !CanMoveMatter)standbyRippleAt=double.NegativeInfinity;
            bool active=running && !disposed && !highContrast && !viewport.IsEmpty && SystemParameters.ClientAreaAnimation &&
                (Live || observing || stage>0 || evaporationRunning || flightClock.Elapsed.TotalSeconds-Math.Max(terminalRippleAt,foldingRippleAt)<.6 ||
                 flightClock.Elapsed.TotalSeconds-standbyRippleAt<StandbyRippleDuration ||
                 windowLag.LengthSquared>.001 || windowVelocity.LengthSquared>.01);
            if(active==observatoryFrames)return;
            observatoryFrames=active;
            if(active){
                lastObservatoryTime=flightClock.Elapsed.TotalSeconds;observatoryFrame=TimeSpan.MinValue;
                UiFrames.Rendering+=ObservatoryFrame;
            }else{
                UiFrames.Rendering-=ObservatoryFrame;
                if(waveOnGlass){GlassWave?.Invoke(ProjectedGate,0,0);waveOnGlass=false;}
                ReleaseMagnetics();windowLag=windowVelocity=new Vector();GlassMotion?.Invoke(windowLag);if(!disposed)MoveLayers();
            }
        }
        void ObservatoryFrame(object sender,EventArgs args){
            var stamp=((RenderingEventArgs)args).RenderingTime;if(stamp==observatoryFrame)return;observatoryFrame=stamp;
            double now=flightClock.Elapsed.TotalSeconds,dt=Math.Min(.12,Math.Max(0,now-lastObservatoryTime));lastObservatoryTime=now;
            if(windowLag.LengthSquared>.001 || windowVelocity.LengthSquared>.01){
                Vector q=windowVelocity+11*windowLag;double decay=Math.Exp(-11*dt);
                windowLag=(windowLag+dt*q)*decay;windowVelocity=(windowVelocity-11*dt*q)*decay;
                if(windowLag.LengthSquared<.001 && windowVelocity.LengthSquared<.01)windowLag=windowVelocity=new Vector();
                GlassMotion?.Invoke(windowLag);MoveLayers();
            }
            if(Live && riverProgress>.001){
                double bandwidth=UiMotion.Unit(Math.Log(1+Math.Max(0,tunnelRate)/8192.0)/Math.Log(513));
                fieldRate+=(bandwidth-fieldRate)*(1-Math.Exp(-2*dt));
                fieldPhase+=dt*(.075+.23*fieldRate);PaintMagnetics();
            }else if(magneticBatch!=null || fieldFallback>=0)ReleaseMagnetics();
            double sinceFlash=now-terminalRippleAt;
            double sinceRelight=now-standbyRippleAt;
            double radius=birthScale.ScaleX*100,energy=formationRunning?birthWave.Opacity:0;
            if(sinceFlash>=0 && sinceFlash<.60){
                double u=sinceFlash/.60;radius=Math.Sqrt(RenderSize.Width*RenderSize.Width+RenderSize.Height*RenderSize.Height)*u;
                energy=Math.Sin(Math.PI*u)*(1-.45*u);
            }else if(now-foldingRippleAt<.48 && energy<.01){
                double u=UiMotion.Unit((now-foldingRippleAt)/.48);
                radius=Math.Max(RenderSize.Width,RenderSize.Height)*(observing?u:1-u);
                energy=.68*Math.Sin(Math.PI*u);
            }else if(!Live && sinceRelight>=0 && sinceRelight<StandbyRippleDuration && energy<.01){
                double u=sinceRelight/StandbyRippleDuration;
                radius=StandbyOpticalScale*Math.Sqrt(RenderSize.Width*RenderSize.Width+RenderSize.Height*RenderSize.Height)*u;
                energy=StandbyOpticalScale*Math.Sin(Math.PI*u)*(1-.45*u);
            }
            if(energy>=.005 || waveOnGlass){GlassWave?.Invoke(ProjectedGate,radius,energy);waveOnGlass=energy>=.005;}
            if(stage>0)DrawDeepSky(now);
            if(observing && !observationCameraMoving)PaintObservingSky(now,true);
            SyncObservatory();
        }
        static Point MagneticPoint(double radius,double angle){
            double x=radius*Math.Cos(angle),y=4.1+.135*radius*Math.Sin(angle);
            return new Point(AccretionPath.CosTilt*x+AccretionPath.SinTilt*y,-AccretionPath.SinTilt*x+AccretionPath.CosTilt*y);
        }
        static readonly Color fieldBlue=Color.FromRgb(162,212,255),fieldGold=Color.FromRgb(255,215,163);
        const int FieldLineCount=256;
        void PaintMagnetics(){
            if(magneticBatch==null)magneticBatch=new FlowBatch(false,true);
            double rasterScale=observing && !observationMonitor.IsEmpty
                ? Math.Min(4.8,Math.Min(observationMonitor.Width/220,observationMonitor.Height/175)):CoreScale;
            bool gpu=magneticBatch.BeginAt(this,new Rect(-82,-43,164,86),rasterScale);
            bool present=gpu && magneticBatch.HasImage;
            if(present && !magneticGpu){
                magneticRear.Children.Clear();magneticNear.Children.Clear();
                magneticRear.Children.Add(magneticBatch.Back);magneticNear.Children.Add(magneticBatch.Front);
            }
            if(gpu && magneticBatch.AcceptsSprites){
                var x=new Vector(AccretionPath.CosTilt,-AccretionPath.SinTilt);
                var y=new Vector(AccretionPath.SinTilt,AccretionPath.CosTilt);
                var center=new Point()+4.1*y;
                double phase=fieldPhase%(2*Math.PI);
                magneticBatch.MagneticPlate(center,77*x,12.5*y,phase,fieldRate,0,riverProgress,true);
                magneticBatch.MagneticPlate(center,77*x,12.5*y,phase,fieldRate,1,riverProgress,false);
                magneticBatch.MagneticPlate(new Point(0,-1.1),36*x,32*y,phase,fieldRate,2,riverProgress,true);
                magneticBatch.End();
            }
            int bucket=(int)(fieldRate*8)*1000+(int)(fieldPhase*12)%1000;
            bool repaint=!present && (magneticGpu || bucket!=fieldFallback);
            magneticGpu=present;fieldFallback=present?-1:bucket;
            if(!repaint)return;
            var far=new StreamGeometry();var near=new StreamGeometry();
            using(var back=far.Open())using(var front=near.Open()){
                for(int i=0;i<FieldLineCount;i++){
                    double seed=(i/4)*2*Math.PI/64+(i%4-1.5)*.016+fieldPhase;
                    Point previous=new Point();bool wasRear=false;
                    for(int j=0;j<=42;j++){
                        double t=j/42.0,r=27+43*t,angle=seed+(2.55+3.45*fieldRate)*Math.Log(r/40);
                        bool rear=Math.Sin(angle)<0;Point next=MagneticPoint(r,angle);
                        var path=rear?back:front;
                        if(j==0 || wasRear!=rear)path.BeginFigure(j==0?next:previous,false,false);
                        if(j>0)path.LineTo(next,true,true);
                        previous=next;wasRear=rear;
                    }
                }
            }
            far.Freeze();near.Freeze();
            using(var dc=magneticRear.Open())dc.DrawGeometry(null,Stroke(fieldGold,.46,.42),far);
            using(var dc=magneticNear.Open())dc.DrawGeometry(null,Stroke(fieldGold,.52,.46),near);
        }
        void ReleaseMagnetics(){
            magneticRear.Children.Clear();magneticNear.Children.Clear();magneticBatch?.Dispose();magneticBatch=null;
            magneticGpu=false;fieldFallback=-1;
        }
        internal void Inspect(string path){
            if(String.Equals(starAim,path,StringComparison.OrdinalIgnoreCase))return;starAim=path;Inspected?.Invoke(path);DrawSurvey();
        }
        internal void AimFromRow(string path,Point edge){
            if(path==rowAim && (rowEdge-edge).LengthSquared<1)return;rowAim=path;rowEdge=edge;DrawSurvey();
        }
        internal void SearchChart(string term){
            term=(term??"").Trim();if(term==chartSearch)return;chartSearch=term;QueueChart();
        }
        internal void SetSurveyCatalog(IEnumerable<KeyValuePair<string,string>> apps){
            if(Replace(surveyCatalog,Catalog(apps)) && (chartSearch.Length>0 || rowAim!=null))DrawSurvey();
        }
        bool SurveyMatch(string path){
            if(chartSearch.Length==0)return true;
            if(!surveyCatalog.TryGetValue(path,out var name) && !selectedNames.TryGetValue(path,out name))directNames.TryGetValue(path,out name);
            return path.IndexOf(chartSearch,StringComparison.OrdinalIgnoreCase)>=0 || (name??"").IndexOf(chartSearch,StringComparison.CurrentCultureIgnoreCase)>=0;
        }
        readonly Dictionary<string,SurveyArc> surveyArcs=new Dictionary<string,SurveyArc>(StringComparer.OrdinalIgnoreCase);
        int surveyRevision;
        sealed class SurveyArc : Animatable{
            static readonly DependencyProperty ProgressProperty=DependencyProperty.Register("Progress",typeof(double),typeof(SurveyArc),
                new PropertyMetadata(0d,(d,e)=>((SurveyArc)d).Paint((double)e.NewValue)));
            internal readonly DrawingGroup Art=new DrawingGroup();
            readonly DrawingGroup head=new DrawingGroup(),target=new DrawingGroup();
            readonly TranslateTransform headAt=new TranslateTransform(),targetAt=new TranslateTransform();
            readonly PathFigure line=new PathFigure{IsFilled=false};
            readonly BezierSegment segment=new BezierSegment();
            Point from,to,firstBend,secondBend;
            Point fromAnchor,toAnchor,starAnchor;
            Rect skyRoom;
            bool startMoves,endMoves;
            TranslateTransform skyShift;
            bool wanted;
            int clockRevision;
            internal int Seen;
            protected override Freezable CreateInstanceCore(){return new SurveyArc(Colors.White,false);}
            internal SurveyArc(Color color,bool reticle){
                var path=new PathGeometry();line.Segments.Add(segment);path.Figures.Add(line);
                Art.Children.Add(new GeometryDrawing(null,Stroke(color,.13,3.2),path));
                Art.Children.Add(new GeometryDrawing(null,Stroke(color,.80,reticle?1.05:.85),path));
                head.Transform=headAt;target.Transform=targetAt;Art.Children.Add(head);Art.Children.Add(target);
                using(var d=head.Open()){d.DrawEllipse(Glow(A(color,.85)),null,new Point(),5,5);d.DrawEllipse(Brushes.White,null,new Point(),1.24,1.24);}
                using(var d=target.Open()){
                    var pen=Stroke(color,.78,.85);double r=reticle?11:4.5;
                    d.DrawEllipse(Glow(A(color,.54)),null,new Point(),r*.85,r*.85);d.DrawEllipse(null,pen,new Point(),r,r);
                    if(reticle){
                        d.DrawEllipse(null,Stroke(color,.31,.65),new Point(),15,15);
                        for(int i=0;i<4;i++){var v=new Vector(Math.Cos(i*Math.PI/2),Math.Sin(i*Math.PI/2));d.DrawLine(pen,new Point()+v*7,new Point()+v*16);}
                    }
                }
                Paint(0);
            }
            internal void Anchor(Point start,Point end,Point star,Rect room,bool moveStart,bool moveEnd,TranslateTransform shift){
                fromAnchor=start;toAnchor=end;starAnchor=star;skyRoom=room;startMoves=moveStart;endMoves=moveEnd;skyShift=shift;
                if(moveStart && moveEnd){
                    Art.Transform=shift;Position(start,end,star,room);
                }else{Art.Transform=Transform.Identity;FollowSky();}
            }
            internal void FollowSky(){
                if(skyShift==null || startMoves && endMoves)return;
                var a=startMoves?skyShift.Transform(fromAnchor):fromAnchor;
                var b=endMoves?skyShift.Transform(toAnchor):toAnchor;
                var room=skyRoom;room.Union(a);room.Union(b);
                Position(a,b,skyShift.Transform(starAnchor),room);
            }
            void Position(Point start,Point end,Point star,Rect room){
                from=start;to=end;var chord=end-start;double length=chord.Length;
                var normal=length>.01?new Vector(-chord.Y/length,chord.X/length):new Vector();
                var middle=start+chord*.5;
                var interior=new Point(room.X+room.Width*.49,room.Y+room.Height*.38);
                if(Vector.Multiply(normal,interior-middle)<0)normal=-normal;
                double bow=Math.Min(114,length*.34);
                Point Inside(Point at){return new Point(Math.Max(room.Left,Math.Min(room.Right,at.X)),
                    Math.Max(room.Top,Math.Min(room.Bottom,at.Y)));}
                firstBend=Inside(start+chord*.17+normal*bow*1.12);
                secondBend=Inside(start+chord*.72+normal*bow*.73);
                targetAt.X=star.X;targetAt.Y=star.Y;Paint((double)GetValue(ProgressProperty));
            }
            void Paint(double value){
                double t=UiMotion.Unit(value),q=1-t;
                // de Casteljau's left subcurve keeps the luminous tip on the same cubic
                // through extension, interruption and retraction.
                var a=from+(firstBend-from)*t;var b=firstBend+(secondBend-firstBend)*t;var c=secondBend+(to-secondBend)*t;
                var ab=a+(b-a)*t;var bc=b+(c-b)*t;var tip=ab+(bc-ab)*t;
                line.StartPoint=from;segment.Point1=a;segment.Point2=ab;segment.Point3=tip;
                headAt.X=tip.X;headAt.Y=tip.Y;
                head.Opacity=Math.Min(1,7*t)*Math.Min(1,8*q);target.Opacity=SmoothBand(.28,1,t);
                Art.Opacity=SmoothBand(0,.12,t);
            }
            internal void To(bool show,bool animate,int? rate,int delay,Action remove){
                if(wanted==show && animate)return;
                wanted=show;int rev=++clockRevision;double now=(double)GetValue(ProgressProperty),end=show?1:0;
                SetValue(ProgressProperty,end);
                if(!animate || Math.Abs(end-now)<.0005){BeginAnimation(ProgressProperty,null);Paint(end);if(!show)remove?.Invoke();return;}
                var travel=UiMotion.Ease(now,end,(show?610:380)*Math.Max(.25,Math.Abs(end-now)),rate,
                    .25,0,.28,1,show && now==0?delay:0);
                travel.Completed+=(s,e)=>{if(!wanted && rev==clockRevision)remove?.Invoke();};
                BeginAnimation(ProgressProperty,travel);
            }
        }
        void ClearSurvey(){surveyRevision++;surveyArcs.Clear();survey.Children.Clear();surveyExtras.Clear();}
        void DrawSurvey(){
            if(disposed || highContrast || observing || viewport.IsEmpty){ClearSurvey();return;}
            int rev=++surveyRevision;bool animate=CanMoveMatter;
            void Show(string key,Point from,Point to,Point star,Color color,bool reticle,int delay=0,bool moveStart=true,bool moveEnd=true){
                if(!surveyArcs.TryGetValue(key,out var arc)){
                    surveyArcs.Add(key,arc=new SurveyArc(color,reticle));survey.Children.Add(arc.Art);
                }
                arc.Seen=rev;var room=viewport;room.Union(from);room.Union(to);
                arc.Anchor(from,to,star,room,moveStart,moveEnd,mapShift);arc.To(true,animate,refreshRate,delay,null);
            }
            surveyExtras.Clear();
            if(rowAim!=null || chartSearch.Length>0 || starAim!=null){
                foreach(var app in surveyCatalog){
                    if(app.Key!=rowAim && app.Key!=starAim && (chartSearch.Length==0 || !SurveyMatch(app.Key)))continue;
                    if(markerLayout.Any(m=>String.Equals(m.Path,app.Key,StringComparison.OrdinalIgnoreCase)))continue;
                    uint seed=PathSeed(app.Key);
                    var m=new ChartMarker{Path=app.Key,Selected=observingCatalog.ContainsKey(app.Key),
                        Position=Project(.065+.64*(seed%1021)/1021.0,.16+.70*((seed>>12)%1013)/1013.0)};
                    surveyExtras.Add(m);
                }
                var available=markerLayout.Concat(surveyExtras);
                if(chartSearch.Length>0){
                    var hits=available.Where(m=>SurveyMatch(m.Path)).OrderBy(m=>m.Position.X).ThenBy(m=>m.Path,StringComparer.OrdinalIgnoreCase).ToArray();
                    for(int i=0;i<hits.Length;i++){
                        var first=hits[Math.Max(0,i-1)];var next=hits[i];
                        Show("search|"+first.Path+"|"+next.Path,first.Position,next.Position,next.Position,
                            Color.FromRgb(215,231,249),false,Math.Min(180,i*26));
                    }
                }
                var aim=available.FirstOrDefault(m=>String.Equals(m.Path,rowAim??starAim,StringComparison.OrdinalIgnoreCase));
                if(aim!=null){
                    bool fromRow=rowAim!=null;
                    double x=fromRow?rowEdge.X:viewport.Left+32;
                    var lip=new Point(Math.Max(viewport.Left,Math.Min(viewport.Right,x)),viewport.Bottom);
                    if(!fromRow && SurveyRowIcon.HasValue)lip=SurveyRowIcon.Value;
                    Show((fromRow?"row|":"sky|")+aim.Path,fromRow?lip:aim.Position,fromRow?aim.Position:lip,aim.Position,AppInk(aim.Path),true,0,!fromRow,fromRow);
                }
            }
            foreach(var pair in surveyArcs.Where(p=>p.Value.Seen!=rev).ToArray()){
                var arc=pair.Value;
                arc.To(false,animate,refreshRate,0,()=>{if(surveyArcs.TryGetValue(pair.Key,out var current) && current==arc){
                    survey.Children.Remove(arc.Art);surveyArcs.Remove(pair.Key);
                }});
            }
        }
        internal void SetDeepStage(int value){
            if(stage==value)return;stage=value;
            if(stage>=3 && ionizedPlate.ImageSource==null)LoadIonizedNebula();
            DrawDeepSky(flightClock.Elapsed.TotalSeconds);SyncObservatory();
        }
        async void LoadIonizedNebula(){
            try{
                var image=await ionizedTexture.Value.ConfigureAwait(false);
                if(Dispatcher.HasShutdownStarted)return;
                await Dispatcher.InvokeAsync(()=>{
                    if(disposed || ionizedPlate.ImageSource!=null)return;
                    ionizedPlate.ImageSource=image;
                    ionizedLight.Children.Add(ionizedPlate);
                    if(stage>=3)DrawDeepSky(flightClock.Elapsed.TotalSeconds);
                },DispatcherPriority.Render);
            }catch(Exception ex) when(ex is OperationCanceledException || ex is System.IO.IOException ||
                ex is NotSupportedException || ex is System.Runtime.InteropServices.COMException){ }
        }
        sealed class MilestoneLamp
        {
            internal readonly DrawingGroup Art=new DrawingGroup();
            internal readonly TranslateTransform Position=new TranslateTransform();
            readonly GeometryDrawing halo, inner, rayX, rayY, core, center;
            readonly Color color;
            internal MilestoneLamp(Color color,double radius){
                this.color=color;Art.Transform=Position;
                halo=Ellipse(radius*7.5);inner=Ellipse(radius*3.5);
                rayX=new GeometryDrawing(null,null,new LineGeometry(new Point(-radius*2.9,0),new Point(radius*2.9,0)));
                rayY=new GeometryDrawing(null,null,new LineGeometry(new Point(0,-radius*2.3),new Point(0,radius*2.3)));
                core=Ellipse(radius);center=Ellipse(radius*.46);
                foreach(var node in new[]{halo,inner,rayX,rayY,core,center}){node.Geometry.Freeze();Art.Children.Add(node);}
            }
            static GeometryDrawing Ellipse(double r){return new GeometryDrawing(null,null,new EllipseGeometry(new Point(),r,r));}
            internal void LightAt(Point at,double power){
                Position.X=at.X;Position.Y=at.Y;
                halo.Brush=Glow(A(color,power*.68));inner.Brush=Glow(A(color,power*.83));
                rayX.Pen=rayY.Pen=Stroke(color,.61*power,.62);core.Brush=Ink(color,power);center.Brush=Ink(Colors.White,.92*power);
            }
        }
        readonly DrawingGroup cometLayer=new DrawingGroup(),cometBody=new DrawingGroup(),pulsarLayer=new DrawingGroup(),diamondLayer=new DrawingGroup(),gasLayer=new DrawingGroup();
        readonly TranslateTransform cometAt=new TranslateTransform();
        readonly LineGeometry iceLine=new LineGeometry(),dustLine=new LineGeometry(),pulseLine=new LineGeometry();
        readonly EllipseGeometry diamondCircle=new EllipseGeometry();
        MilestoneLamp pulsarLamp,diamondLamp;
        GeometryDrawing pulseStroke;
        bool deepBuilt;
        int deepStage=-1;
        void BuildDeepSky(){
            if(deepBuilt)return;deepBuilt=true;
            cometLayer.Transform=cometShift;cometBody.Transform=cometAt;cometLayer.Children.Add(cometBody);
            cometBody.Children.Add(new GeometryDrawing(null,Stroke(Color.FromRgb(145,203,255),.68,1),iceLine));
            cometBody.Children.Add(new GeometryDrawing(null,Stroke(fieldGold,.43,1.4),dustLine));
            cometBody.Children.Add(new GeometryDrawing(Glow(Colors.White),null,new EllipseGeometry(new Point(),5,5)));
            pulsarLayer.Transform=pulsarShift;diamondLayer.Transform=mapShift;gasLayer.Transform=gasShift;
            pulsarLamp=new MilestoneLamp(fieldBlue,1.4);diamondLamp=new MilestoneLamp(Colors.Bisque,1.8);
            pulsarLayer.Children.Add(pulsarLamp.Art);pulseStroke=new GeometryDrawing(null,null,pulseLine);pulsarLayer.Children.Add(pulseStroke);
            diamondLayer.Children.Add(new GeometryDrawing(null,Stroke(fieldGold,.38,.85),diamondCircle));diamondLayer.Children.Add(diamondLamp.Art);
            var baseGas=new DrawingGroup{Opacity=.70};baseGas.Children.Add(ionizedPlate);gasLayer.Children.Add(baseGas);gasLayer.Children.Add(ionizedLight);
        }
        void DrawDeepSky(double now){
            int level=viewport.IsEmpty || highContrast?0:stage;
            if(level==0 && deepStage<=0){deepStage=0;return;}
            BuildDeepSky();
            if(deepStage!=level){
                deepStage=level;deepSky.Children.Clear();
                if(level>=3)deepSky.Children.Add(gasLayer);
                if(level>=1)deepSky.Children.Add(cometLayer);
                if(level>=2)deepSky.Children.Add(pulsarLayer);
                if(level>=5)deepSky.Children.Add(diamondLayer);
            }
            if(level==0)return;
            var gate=GateCenter;double w=viewport.Width,h=viewport.Height;
            var image=ionizedPlate.ImageSource as BitmapSource;
            if(level>=3 && image!=null){
                double aspect=(double)image.PixelWidth/image.PixelHeight;
                double width=Math.Min(observing?530:300,Math.Min(w*.41,h*.74*aspect));
                var area=new Rect(viewport.X+w*.035,viewport.Y+h*(observing?.16:.11),width,width/aspect);
                if(ionizedPlate.Rect!=area)ionizedPlate.Rect=area;
                double v=SystemParameters.ClientAreaAnimation?.5-.5*Math.Cos(now*(2*Math.PI/19)):.5;
                var center=new Point(.445+.31*v,.64-.29*v);var mask=(RadialGradientBrush)ionizedLight.OpacityMask;
                if(mask.Center!=center)mask.Center=mask.GradientOrigin=center;
            }
            double comet=(now+1.3)%23;
            if(comet<3.2){
                double u=comet/3.2;var at=new Point(viewport.X+w*(.10+.72*u),viewport.Y+h*(.26+.33*u-.19*u*u));
                var away=at-gate;cometBody.Opacity=away.Length>.01?Math.Sin(Math.PI*u):0;
                if(cometBody.Opacity>0){away.Normalize();cometAt.X=at.X;cometAt.Y=at.Y;
                    iceLine.EndPoint=(Point)(away*49);dustLine.EndPoint=(Point)(away*27-new Vector(14,7));}
            }else if(cometBody.Opacity!=0)cometBody.Opacity=0;
            if(level>=2){
                var at=new Point(viewport.X+w*.21,viewport.Y+h*.26);double pulse=.22+.72*Math.Pow(.5+.5*Math.Cos(now*2.7),15);
                pulsarLamp.LightAt(at,pulse);var ray=new Vector(10,-3)*pulse;
                pulseStroke.Pen=Stroke(fieldBlue,.31*pulse,.8);pulseLine.StartPoint=at-ray;pulseLine.EndPoint=at+ray;
            }
            if(level>=5){
                double r=(Live?35:14)*CoreScale;diamondCircle.Center=gate;diamondCircle.RadiusX=diamondCircle.RadiusY=r;
                diamondLamp.LightAt(gate+new Vector(.78*r,-.626*r),.69+.24*Math.Cos(now*1.2));
            }
        }
        double observationBlend;
        void ShowHomeChart(double opacity){
            chart.Opacity=markers.Opacity=riverBack.Opacity=riverFront.Opacity=connectionSignals.Opacity=
                landingRings.Opacity=rearAppSources.Opacity=emittingStars.Opacity=opacity;
        }
        void BlendObservation(double value){
            observationBlend=UiMotion.Unit(value);
            ShowHomeChart(observationMonitor.IsEmpty?1-observationBlend:1-previewExposure);
            observingSky.Opacity=observingNearApproaches.Opacity=observingRibbons.Front.Opacity=observingRibbons.Rear.Opacity=observationBlend;
        }
        internal void SetObservation(bool value){PresentObservation(value,value?1:0);}
        internal void PresentObservation(bool value,double blend){
            bool changed=observing!=value;
            if(!changed && observationBlend==blend)return;
            observing=value;
            if(changed){
                foldingRippleAt=flightClock.Elapsed.TotalSeconds;
                ClearLights(Live && CanMoveMatter);ClearSurvey();ResetCaustic();
                if(!value)ClearObservingStars();
            }
            BlendObservation(blend);
            if(changed){
                DrawSurface();QueueChart();UpdateTrafficLights();SyncObservatory();
                if(value && !viewport.IsEmpty && (!Live || !CanMoveMatter))DrawObservingSky(flightClock.Elapsed.TotalSeconds);
            }
        }
        internal void SetObservationCatalog(IEnumerable<KeyValuePair<string,string>> apps){
            if(Replace(observingCatalog,Catalog(apps)) && observing){
                foreach(string key in orbitalStars.Keys.Where(k=>!observingCatalog.ContainsKey(k)).ToArray()){
                    var star=orbitalStars[key];ForgetSource(star);
                    observingSky.Children.Remove(star.Art);observingNearApproaches.Children.Remove(star.Near);orbitalStars.Remove(key);
                }
                DrawObservingSky(flightClock.Elapsed.TotalSeconds);UpdateTrafficLights();
            }
        }
        sealed class OrbitalStar{
            internal readonly DrawingGroup Art=new DrawingGroup(),Near=new DrawingGroup();
            readonly DrawingGroup far=new DrawingGroup();
            readonly DrawingGroup star=new DrawingGroup(),river=new DrawingGroup();
            readonly TranslateTransform starAt=new TranslateTransform();
            readonly PathFigure trail=new PathFigure{IsFilled=false};
            readonly BezierSegment arc=new BezierSegment();
            readonly GeometryDrawing line;
            LinearGradientBrush temperature;
            readonly double azimuth,frequency,radius;
            internal AccretionPath Channel {get;private set;}
            Color color;
            internal OrbitalStar(uint seed){
                azimuth=(seed%8191)*.000767;frequency=.014+(seed%17)*.0008;radius=.54+.39*((seed>>10)%223)/223.0;
                trail.Segments.Add(arc);var geometry=new PathGeometry();geometry.Figures.Add(trail);
                line=new GeometryDrawing(null,null,geometry);river.Children.Add(line);
                star.Transform=starAt;far.Children.Add(river);Near.Children.Add(river);
                Art.Children.Add(far);Art.Children.Add(star);
            }
            internal void Update(Rect sky,Point gate,double scale,double time,Color hue,double rate,double progress,AccretionCamera camera){
                if(color!=hue){
                    color=hue;temperature=new LinearGradientBrush(A(hue,.72),A(outerHeat,.78),0){MappingMode=BrushMappingMode.Absolute};
                    temperature.GradientStops.Add(new GradientStop(A(HeatMix(hue,outerHeat,.42),.47),.58));
                    temperature.GradientStops.Freeze();line.Pen=new Pen(temperature,1.17){EndLineCap=PenLineCap.Round};
                    using(var d=star.Open())Light(d,new Point(),hue,2.05,1);
                }
                double a=azimuth+time*frequency;
                var pos=gate+new Vector(Math.Cos(a)*radius*sky.Width*.49,Math.Sin(a)*radius*sky.Height*.49);
                starAt.X=pos.X;starAt.Y=pos.Y;
                double angle=a+.48; // Keep the contact on the source's side of the inclined disk.
                if(Channel==null){
                    Channel=new AccretionPath(pos,gate,scale,2.05,angle,true){Camera=camera};
                    river.Transform=Channel.ApproachView;
                }
                Channel.MoveOrbitalSource(pos,gate,scale,angle);
                temperature.StartPoint=Channel.Star;temperature.EndPoint=Channel.Entry;
                bool behind=Channel.Behind(Channel.Entry,false);
                far.Opacity=behind?1:0;Near.Opacity=behind?0:1;
                double t=UiMotion.Unit(progress/RiverTrace.Contact);temperature.Opacity=Math.Min(1,t*4)*(.69+.31*NormalizedAppRate(rate));
                if(t<=0)return;
                var p0=Channel.Star;var p1=Channel.FirstControl;var p2=Channel.SecondControl;
                var c1=p0+(p1-p0)*t;var c2=p1+(p2-p1)*t;
                trail.StartPoint=p0;arc.Point1=c1;arc.Point2=c1+(c2-c1)*t;arc.Point3=Channel.ApproachPoint(t);
            }
        }
        void ClearObservingStars(){
            foreach(var star in orbitalStars.Values)ForgetSource(star);
            observingSky.Children.Clear();observingNearApproaches.Children.Clear();observingRibbons.Clear();orbitalStars.Clear();
        }
        // Configuration and traffic can both change several times before WPF presents a frame.
        // The render callback owns the two shared disk surfaces; other callers update the vector immediately.
        void DrawObservingSky(double now){PaintObservingSky(now,false);}
        void PaintObservingSky(double now,bool renderFrame){
            if(viewport.IsEmpty || CoreScale<=0 || highContrast)return;
            Point gate=GateCenter;
            foreach(var item in observingCatalog){
                if(!orbitalStars.TryGetValue(item.Key,out var star)){
                    orbitalStars.Add(item.Key,star=new OrbitalStar(PathSeed(item.Key)));observingSky.Children.Add(star.Art);observingNearApproaches.Children.Add(star.Near);
                }
                appRates.TryGetValue(item.Key,out double rate);
                star.Update(viewport,gate,CoreScale,now,AppInk(item.Key),rate,Live?riverProgress:0,diskAttitude);
                if(LensEventReady)TraceSource(star,MapToScreen(star.Channel.Center),AppInk(item.Key));
            }
            observingRibbons.Draw(orbitalStars.Values,Live?riverProgress:0,CoreScale,diskAttitude,
                renderFrame && !observationCameraMoving && observationBlend>=1?this:null,MapToScreen(gate),flowBatch);
        }
    }
}
