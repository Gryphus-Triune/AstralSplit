using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        sealed class AppStream
        {
            sealed class Mote
            {
                internal DrawingGroup Art;
                internal MatrixTransform Transform;
                internal GeometryDrawing Light;
                internal DrawingGroup Layer;
                internal AccretionPath Channel;
                internal double Birth,Approach,Radius,DrainTime,DrainAge;
                internal void Attach(DrawingGroup layer,Drawing previous)
                {
                    if(Layer==layer)return;
                    if(Layer!=null)Layer.Children.Remove(Art);
                    Layer=layer;
                    if(layer==null)return;
                    if(Art==null){
                        Transform=new MatrixTransform();Light=new GeometryDrawing(null,null,unitDisk);
                        Art=new DrawingGroup{Transform=Transform};Art.Children.Add(Light);
                    }
                    layer.Children.Insert(previous==null?0:layer.Children.IndexOf(previous)+1,Art);
                }
            }
            readonly List<Mote> motes=new List<Mote>(48);
            readonly Stack<Mote> spare=new Stack<Mote>();
            internal readonly DrawingGroup Rear=new DrawingGroup(),Front=new DrawingGroup();
            readonly DrawingGroup rearMotes=new DrawingGroup(),frontMotes=new DrawingGroup(),impact=new DrawingGroup();
            readonly MatrixTransform impactTransform=new MatrixTransform();
            DrawingGroup impactLayer;
            readonly Random random;
            readonly Brush[] approachColors=new Brush[24];
            readonly Color[] approachInk=new Color[24];
            readonly float[] nativeProjection=new float[8];
            static readonly Color[,] diskInk=new Color[3,24];
            static readonly Brush[,] diskColors=DiskColors();
            static readonly EllipseGeometry unitDisk=UnitDisk();
            static readonly Brush hotSpot=SpotBrush();
            readonly double baseRate,frequency,phase;
            double nextBirth,birthsPerSecond,traffic;
            Color starInk;
            internal AccretionPath Path;
            internal FlowBatch Batch;
            internal bool Draining {get;private set;}
            internal bool Empty {get{return motes.Count==0;}}
            internal bool CanSleep {get{return Empty && (Draining || birthsPerSecond<=0);}}
            internal AppStream(string path,double now)
            {
                random=new Random(unchecked((int)PathSeed(path)));baseRate=8+4*random.NextDouble();
                frequency=7+4*random.NextDouble();phase=random.NextDouble()*Math.PI*2;
                nextBirth=now+random.NextDouble()*80;
                Rear.Children.Add(rearMotes);Front.Children.Add(frontMotes);
                impact.Transform=impactTransform;impact.Opacity=0;
                impact.Children.Add(new GeometryDrawing(hotSpot,null,unitDisk));
            }
            static EllipseGeometry UnitDisk(){var disk=new EllipseGeometry(new Point(),1,1);disk.Freeze();return disk;}
            static Brush[,] DiskColors()
            {
                var table=new Brush[3,24];
                for(int i=24;i<48;i++){
                    var heat=ThermalColor(outerHeat,1,2*i/47.0-1);
                    for(int j=0;j<3;j++)table[j,i-24]=Glow(diskInk[j,i-24]=DopplerColor(heat,j-1,1));
                }
                return table;
            }
            static Brush SpotBrush()
            {
                var b=new RadialGradientBrush();b.GradientStops.Add(new GradientStop(Colors.White,0));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(220,255,220,164),.22));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(105,255,137,66),.57));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0,255,104,50),1));b.Freeze();return b;
            }
            internal void Configure(AccretionPath path,Color ink,double rate,double budget,double now)
            {
                if(Path!=path)PlacePath(path);
                traffic=NormalizedAppRate(Math.Max(0,rate));
                birthsPerSecond=rate<0?0:Math.Min(budget,baseRate+18*traffic);
                if(birthsPerSecond==0 && impact.Opacity!=0)impact.Opacity=0;
                if(Draining){nextBirth=now;Draining=false;}
                if(approachColors[0]!=null && ink==starInk)return;
                starInk=ink;
                for(int i=0;i<24;i++)approachColors[i]=Glow(approachInk[i]=ThermalColor(ink,2*i/47.0,0));
            }
            void PlacePath(AccretionPath path)
            {
                // A changed entry rank only affects fresh material. During a resize
                // remap every already airborne grain to its original lane angle.
                if(Path!=null && (path.Center!=Path.Center || path.Gate!=Path.Gate || path.Scale!=Path.Scale)){
                    Dictionary<double,AccretionPath> previousAngles=null;
                    foreach(var m in motes){
                        double angle=m.Channel.EntryAngle;
                        if(path.Orbital || angle==path.EntryAngle){m.Channel=path;continue;}
                        if(previousAngles==null)previousAngles=new Dictionary<double,AccretionPath>();
                        if(!previousAngles.TryGetValue(angle,out var old))
                            previousAngles.Add(angle,old=new AccretionPath(path.Center,path.Gate,path.Scale,path.TipRadius,angle){Camera=path.Camera});
                        m.Channel=old;
                    }
                }
                Path=path;
                var entry=path.Entry;
                PositionImpact(path);
                var view=new TransformGroup();view.Children.Add(impactTransform);view.Children.Add(path.DiskView);impact.Transform=view;
                var layer=path.Behind(entry,false)?Rear:Front;
                if(layer==impactLayer)return;
                if(impactLayer!=null)impactLayer.Children.Remove(impact);
                layer.Children.Insert(0,impact);impactLayer=layer;
            }
            void PositionImpact(AccretionPath path){
                var entry=path.Entry;double s=path.Scale,c=AccretionPath.CosTilt,v=AccretionPath.SinTilt;
                var pose=new Matrix(7*s*c,-7*s*v,3*s*v,3*s*c,entry.X,entry.Y);
                if(impactTransform.Matrix!=pose)impactTransform.Matrix=pose;
            }
            internal void NativeParticles(SceneStreamData data,double now){
                if(motes.Count==0)return;
                int palette=data.Colors.Count;
                foreach(var col in approachInk)data.Ink(col);
                for(int bank=0;bank<3;bank++)for(int j=0;j<24;j++)data.Ink(diskInk[bank,j]);
                var camera=nativeProjection;AccretionCamera last=null;bool projected=false;
                for(int i=motes.Count-1;i>=0;i--){
                    var m=motes[i];var ch=m.Channel;if(ch==null || ch.Scale<=0)continue;
                    ch.UpdateProjection();double rate=1,age=now-m.Birth;
                    if(m.DrainTime>=0){rate=Math.Max(1,(m.Approach+AccretionPath.StreamDiskTime-m.DrainAge)/400);age=m.DrainAge+(now-m.DrainTime)*rate;}
                    if(age>=m.Approach+AccretionPath.StreamDiskTime)continue;
                    var map=ch.ApproachView.Matrix;
                    if(!projected || last!=ch.Camera){last=ch.Camera;projected=true;
                        if(last!=null)last.WriteProjection(camera);else{camera[0]=camera[3]=1;camera[1]=camera[2]=camera[4]=camera[5]=0;
                            camera[6]=(float)AccretionPath.SinTilt;camera[7]=(float)AccretionPath.CosTilt;}}
                    data.Row(ch.Star.X,ch.Star.Y,ch.FirstControl.X,ch.FirstControl.Y);
                    data.Row(ch.SecondControl.X,ch.SecondControl.Y,ch.Entry.X,ch.Entry.Y);
                    data.Row(ch.Gate.X,ch.Gate.Y,ch.Scale,ch.EntryAngle);data.Row(age,m.Approach,m.Radius,rate);
                    data.Row(map.M11,map.M21,map.OffsetX,0);data.Row(map.M12,map.M22,map.OffsetY,0);
                    data.Row(camera[0],camera[1],camera[2],camera[3]);data.Row(camera[4],camera[5],camera[6],camera[7]);
                    data.Row(ch.NativeLead,ch.Behind(ch.Entry,true)?1:0,palette,0);
                }
            }
            internal void Drain(double now)
            {
                if(Draining)return;Draining=true;impact.Opacity=0;
                for(int i=0;i<motes.Count;i++){
                    var m=motes[i];if(m.DrainTime>=0)continue;
                    m.DrainTime=now;m.DrainAge=now-m.Birth;
                }
            }
            internal void Draw(double now,Queue<double> births)
            {
                if(!Draining && birthsPerSecond>0){
                    // Discard missed emissions after a stall. A rolling window imposes the absolute global limit.
                    nextBirth=Math.Max(nextBirth,now-35);
                    while(nextBirth<=now){
                        if(births.Count<200){
                            var m=spare.Count>0?spare.Pop():new Mote();
                            m.Birth=nextBirth;m.Approach=900+500*random.NextDouble();m.Radius=.8+.85*random.NextDouble();
                            m.Channel=Path;m.DrainTime=-1;motes.Add(m);
                            births.Enqueue(now);
                        }
                        nextBirth+=1000/birthsPerSecond;
                    }
                    var entry=Path.Entry;PositionImpact(Path);
                    var entryLayer=Path.Behind(entry,false)?Rear:Front;
                    if(entryLayer!=impactLayer){impactLayer?.Children.Remove(impact);entryLayer.Children.Insert(0,impact);impactLayer=entryLayer;}
                    double light=.86*traffic*(1+.15*Math.Sin(phase+now*.002*Math.PI*frequency));
                    if(impact.Opacity!=light)impact.Opacity=light;
                }
                Drawing previousRear=null,previousFront=null;
                for(int i=motes.Count-1;i>=0;i--){
                    var m=motes[i];double total=m.Approach+AccretionPath.StreamDiskTime;
                    double age=now-m.Birth;
                    if(m.DrainTime>=0)age=m.DrainAge+(now-m.DrainTime)*Math.Max(1,(total-m.DrainAge)/400);
                    if(age>=total){
                        m.Attach(null,null);m.Channel=null;motes.RemoveAt(i);
                        if(spare.Count<80)spare.Push(m);
                        continue;
                    }
                    RenderMote(m,age,ref previousRear,ref previousFront);
                }
            }
            void RenderMote(Mote m,double age,ref Drawing previousRear,ref Drawing previousFront)
            {
                var channel=m.Channel;var at=channel.StreamPose(age,m.Approach,out var dir);
                double u=UiMotion.Unit((age-m.Approach)/AccretionPath.StreamDiskTime),travel=UiMotion.Unit(age/m.Approach);
                double redshift=UiMotion.Unit((u-.9)/.1);
                double side=u==0?0:channel.Doppler(at),alpha=Math.Min(1,.77*DopplerGain(side)*UiMotion.Unit(age/100)*(1-redshift));
                if(alpha<.005){m.Attach(null,null);return;}
                bool rear=channel.Behind(at,age<m.Approach);
                if(channel.Camera!=null){
                    var tip=channel.Seen(at+dir*.1,age<m.Approach);
                    at=channel.Seen(at,age<m.Approach);dir=UnitTangent(tip-at);
                }
                int key=(int)Math.Round((.5*travel+.5*u)*47);
                int bank=side<-.28?0:side>.28?2:1;
                double rx=2.7*m.Radius*(1+2.4*u*u),ry=2.7*m.Radius*(1-.36*u);
                if(Batch!=null){
                    if(m.Layer!=null)m.Attach(null,null);
                    var color=key<24?approachInk[key]:diskInk[bank,key-24];
                    Batch.Add(at,dir,rx,ry,color,alpha,rear);return;
                }
                m.Attach(rear?rearMotes:frontMotes,rear?previousRear:previousFront);
                if(rear)previousRear=m.Art;else previousFront=m.Art;
                var brush=key<24?approachColors[key]:diskColors[bank,key-24];
                if(m.Light.Brush!=brush)m.Light.Brush=brush;
                if(m.Art.Opacity!=alpha)m.Art.Opacity=alpha;
                m.Transform.Matrix=new Matrix(dir.X*rx,dir.Y*rx,-dir.Y*ry,dir.X*ry,at.X,at.Y);
            }
        }

        readonly DrawingGroup streamBack=new DrawingGroup(),streamFront=new DrawingGroup();
        readonly DrawingGroup batchRear=new DrawingGroup(),batchFront=new DrawingGroup();
        FlowBatch flowBatch;
        readonly Dictionary<string,AppStream> appStreams=new Dictionary<string,AppStream>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> exhaustedLanes=new List<string>();
        readonly Queue<double> recentBirths=new Queue<double>(200);
        bool streamFrames;
        TimeSpan? lastStreamFrame;
        double streamTime;

        bool CanMoveMatter {get{return running && !disposed && !highContrast && SystemParameters.ClientAreaAnimation && !viewport.IsEmpty;}}
        void DrainAccretion()
        {
            if(!CanMoveMatter){ClearLights();SettleInfall();return;}
            foreach(var stream in appStreams.Values)stream.Drain(streamTime);
            double now=flightClock.Elapsed.TotalMilliseconds;
            foreach(var f in infalls.Values)f.Drain(now);
            foreach(var hit in diskHits.Values)hit.Drain(now);
            fallOnArrival.Clear();
        }
        void UpdateTrafficLights()
        {
            if(!CanMoveMatter){ClearLights();return;}
            if(!Live || riverProgress<1){foreach(var stream in appStreams.Values)stream.Drain(streamTime);return;}
            if(observing){
                DrawObservingSky(flightClock.Elapsed.TotalSeconds);
                foreach(var pair in appStreams)if(!orbitalStars.ContainsKey(pair.Key))pair.Value.Drain(streamTime);
                double allocation=200.0/Math.Max(1,orbitalStars.Count);
                foreach(var pair in orbitalStars){
                    if(pair.Value.Channel!=null)SupplyStream(pair.Key,pair.Value.Channel,allocation);
                }
            }else SupplyChartStreams();
            if(appStreams.Count>0 && !streamFrames){streamFrames=true;UiFrames.Rendering+=DrawStreams;}
        }
        void SupplyChartStreams(){
            foreach(var pair in appStreams)if(!slots.ContainsKey(pair.Key)||IsFlyingIn(pair.Key))pair.Value.Drain(streamTime);
            double budget=200.0/Math.Max(1,slots.Count);
            foreach(var pair in positions){
                string path=pair.Key;if(!slots.ContainsKey(path)||IsFlyingIn(path))continue;
                SupplyStream(path,CapturePath(path,pair.Value),budget);
            }
        }
        void SupplyStream(string path,AccretionPath channel,double budget){
            appRates.TryGetValue(path,out double bytes);
            bool emitting=!MeasuredAppStreams || bytes>0;
            if(!appStreams.TryGetValue(path,out var stream)){
                if(!emitting)return;
                appStreams.Add(path,stream=new AppStream(path,streamTime));
                streamBack.Children.Add(stream.Rear);streamFront.Children.Add(stream.Front);
            }
            stream.Configure(channel,AppInk(path),emitting?bytes:-1,budget,streamTime);
        }
        void DrawStreams(object sender,EventArgs args)
        {
            var frame=((RenderingEventArgs)args).RenderingTime;
            if(lastStreamFrame==frame)return;
            if(lastStreamFrame.HasValue)streamTime+=Math.Max(0,(frame-lastStreamFrame.Value).TotalMilliseconds);
            lastStreamFrame=frame;
            while(recentBirths.Count>0 && recentBirths.Peek()<=streamTime-1000)recentBirths.Dequeue();
            if(flowBatch==null)flowBatch=new FlowBatch();
            if(batchRear.Children.Count==0){
                batchRear.Children.Add(flowBatch.Back);batchFront.Children.Add(flowBatch.Front);
                streamBack.Children.Add(batchRear);streamFront.Children.Add(batchFront);
            }
            var canvas=new Rect(RenderSize);
            var area=observing?canvas:Rect.Intersect(coreAperture==null?viewport:coreAperture.Bounds,canvas);
            bool batched=flowBatch.Begin(this,area);
            batchRear.Opacity=batchFront.Opacity=batched?1:0;
            try {
                foreach(var pair in appStreams){
                    pair.Value.Batch=batched?flowBatch:null;
                    pair.Value.Draw(streamTime,recentBirths);
                    if(pair.Value.CanSleep)exhaustedLanes.Add(pair.Key);
                }
            } finally { if(batched && exhaustedLanes.Count<appStreams.Count)flowBatch.End(); }
            foreach(var path in exhaustedLanes){
                var stream=appStreams[path];
                streamBack.Children.Remove(stream.Rear);streamFront.Children.Remove(stream.Front);
                appStreams.Remove(path);
            }
            exhaustedLanes.Clear();
            if(appStreams.Count==0)ClearLights(keepDevice:true);
        }
        void ClearLights(bool keepDevice=false)
        {
            if(!streamFrames && appStreams.Count==0){
                if(!keepDevice && flowBatch!=null){flowBatch.Dispose();flowBatch=null;}
                return;
            }
            if(streamFrames){UiFrames.Rendering-=DrawStreams;streamFrames=false;}
            appStreams.Clear();exhaustedLanes.Clear();recentBirths.Clear();lastStreamFrame=null;streamTime=0;
            streamBack.Children.Clear();streamFront.Children.Clear();
            batchRear.Children.Clear();batchFront.Children.Clear();
            if(!keepDevice || flowBatch==null || !flowBatch.ResetImages()){flowBatch?.Dispose();flowBatch=null;}
        }
    }
}
