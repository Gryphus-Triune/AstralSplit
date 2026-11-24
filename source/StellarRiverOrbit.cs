using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly DrawingGroup riverBack=new DrawingGroup(), riverFront=new DrawingGroup();

        // Moving sources share one sampled spiral and a small set of drawing batches.
        // Rotate the disk coordinates before perspective projection and split at the actual depth crossing.
        sealed class ObservatoryRibbons
        {
            const int Steps=100,Bands=10;
            struct Sample {
                internal double X,Z,Mid,Flat;
                internal Sample(double t){
                    AccretionPath.DiskPolar(t*AccretionPath.StreamDiskTime,AccretionPath.StreamOrbit,
                        AccretionPath.StreamPlunge,AccretionPath.StreamTurns,out double r,out double angle);
                    X=r*Math.Cos(angle);Z=r*Math.Sin(angle);
                    double blend=UiMotion.Unit((AccretionPath.Outer-r)/(AccretionPath.Outer-AccretionPath.InnerPlate));
                    Mid=AccretionPath.OuterMidline+(AccretionPath.InnerMidline-AccretionPath.OuterMidline)*blend;
                    Flat=AccretionPath.OuterFlattening+(AccretionPath.InnerFlattening-AccretionPath.OuterFlattening)*blend;
                }
                internal Point At(double c,double s){
                    double x=X*c-Z*s,y=Mid+Flat*(X*s+Z*c);
                    return new Point(AccretionPath.CosTilt*x+AccretionPath.SinTilt*y,
                        -AccretionPath.SinTilt*x+AccretionPath.CosTilt*y);
                }
            }
            static readonly Sample[] orbit=MakeOrbit();
            static readonly bool gpuGeometry=Environment.GetEnvironmentVariable("ASTRALSPLIT_ORBIT_MESH")=="1";
            static readonly float[] orbitTable=EncodedOrbit();
            readonly float[] rotations=new float[128*4],projection=new float[16];
            internal bool UsingMesh {get; private set;}
            static float[] EncodedOrbit(){
                var data=new float[(Steps+1)*4];
                for(int k=0;k<orbit.Length;k++){
                    var v=orbit[k];int j=k*4;
                    data[j]=(float)v.X;data[j+1]=(float)v.Z;data[j+2]=(float)v.Mid;data[j+3]=(float)v.Flat;
                }
                return data;
            }
            bool InsideEdge(int row,double limit,double sign){
                double x=sign*(projection[row]-limit*projection[4]),y=sign*(projection[row+1]-limit*projection[5]);
                double ax=x*AccretionPath.CosTilt-y*AccretionPath.SinTilt,ay=x*AccretionPath.SinTilt+y*AccretionPath.CosTilt;
                // Extremum over every entry angle, including those between adjacent stars.
                foreach(var s in orbit){
                    double baseValue=ay*s.Mid-sign*limit;
                    if(baseValue<=0 || baseValue*baseValue<(s.X*s.X+s.Z*s.Z)*(ax*ax+ay*ay*s.Flat*s.Flat))return false;
                }
                return true;
            }
            static Sample[] MakeOrbit(){
                var result=new Sample[Steps+1];
                for(int i=0;i<=Steps;i++)result[i]=new Sample(i/(double)Steps);
                return result;
            }
            // Last three bands span the redshift and extinction inside the horizon.
            static readonly int[] cuts={0,15,30,45,60,73,82,90,94,97,100};
            internal readonly DrawingGroup Rear=new DrawingGroup(),Front=new DrawingGroup();
            readonly StreamGeometry[] geometry=new StreamGeometry[Bands*2];
            readonly StreamGeometryContext[] writers=new StreamGeometryContext[Bands*2];
            readonly Pen[] pens=new Pen[Bands*2];
            readonly LinearGradientBrush[] heat=new LinearGradientBrush[Bands];
            bool empty=true;
            internal ObservatoryRibbons(){
                for(int i=0;i<geometry.Length;i++){
                    int band=i%Bands;
                    double t=(cuts[band]+cuts[band+1])/(2.0*Steps);
                    double tail=UiMotion.Unit((t-.90)/.10);
                    if(i<Bands){
                        double shift=UiMotion.Unit(5*t);
                        var temperature=ThermalColor(outerHeat,1,t);
                        var gradient=new LinearGradientBrush{MappingMode=BrushMappingMode.Absolute};
                        for(int side=1;side>=-1;side--){
                            var tone=DopplerColor(temperature,side,shift);
                            double alpha=.69*(1+.30*(DopplerGain(side)-1)*shift)*(1-tail);
                            gradient.GradientStops.Add(new GradientStop(A(tone,alpha),(1-side)*.5));
                        }
                        gradient.GradientStops.Freeze();heat[band]=gradient;
                    }
                    pens[i]=new Pen(heat[band],1);
                    pens[i].StartLineCap=pens[i].EndLineCap=PenLineCap.Round;pens[i].LineJoin=PenLineJoin.Round;
                    geometry[i]=new StreamGeometry();
                    vectorInk[i]=new GeometryDrawing(null,pens[i],geometry[i]);
                    (i<Bands?Rear:Front).Children.Add(vectorInk[i]);
                    if(i<Bands)for(int j=0;j<3;j++){
                        var c=heat[i].GradientStops[j].Color;palette[3*i+j]=((uint)c.A<<24)|((uint)c.R<<16)|((uint)c.G<<8)|c.B;
                    }
                }
            }
            interface IStrokeSink {void Begin(int slot,Point at);void Line(int slot,Point at);}
            struct VectorSink:IStrokeSink {
                internal StreamGeometryContext[] W;
                public void Begin(int slot,Point at){W[slot].BeginFigure(at,false,false);}
                public void Line(int slot,Point at){W[slot].LineTo(at,true,true);}
            }
            struct NativeSink:IStrokeSink {
                internal FlowBatch.RibbonVertex[] Points;
                internal int Count;
                void Add(int slot,Point at){
                    if(Count==Points.Length)Array.Resize(ref Points,Points.Length*2);
                    Points[Count++]=new FlowBatch.RibbonVertex{X=(float)at.X,Y=(float)at.Y,Slot=slot};
                }
                public void Begin(int slot,Point at){Add(~slot,at);}
                public void Line(int slot,Point at){Add(slot,at);}
            }
            readonly GeometryDrawing[] vectorInk=new GeometryDrawing[Bands*2];
            readonly uint[] palette=new uint[Bands*3];
            readonly float[] widths=new float[Bands];
            NativeSink vertices=new NativeSink{Points=new FlowBatch.RibbonVertex[4096]};
            const int MinimumRasterStars=10;
            FlowBatch gpu;
            bool displayingNative;
            internal bool IsRasterized {get{return displayingNative;}}
            internal void Dispose(){Display(false);gpu?.Dispose();gpu=null;}
            void Display(bool native){
                if(displayingNative==native)return;
                Rear.Children.Clear();Front.Children.Clear();
                if(native){Rear.Children.Add(gpu.Back);Front.Children.Add(gpu.Front);}
                else for(int i=0;i<vectorInk.Length;i++)(i<Bands?Rear:Front).Children.Add(vectorInk[i]);
                displayingNative=native;
            }
            internal void Clear(){
                Display(false);if(empty)return;
                foreach(var g in geometry)using(g.Open()){}
                empty=true;
            }
            internal void Draw(ICollection<OrbitalStar> stars,double progress,double scale,AccretionCamera camera,
                Visual view,Point gateOnView,FlowBatch sharedDevice){
                UsingMesh=false;
                double end=UiMotion.Unit((progress-RiverTrace.Contact)/(1-RiverTrace.Contact));
                if(end<=0 || scale<=0){Clear();return;}
                var axis=new Vector(AccretionPath.CosTilt,-AccretionPath.SinTilt)*35;
                var blue=camera.Project(new Point()-axis);var red=camera.Project(new Point()+axis);
                if(stars.Count>=MinimumRasterStars && view!=null &&
                    TryNative(stars,end,scale,camera,blue,red,view,gateOnView,sharedDevice)){Display(true);return;}
                Display(false);
                for(int i=0;i<geometry.Length;i++){
                    if(i<Bands && (heat[i].StartPoint!=blue || heat[i].EndPoint!=red)){
                        heat[i].StartPoint=blue;heat[i].EndPoint=red;
                    }
                    double width=Width(i%Bands,scale);
                    if(pens[i].Thickness!=width)pens[i].Thickness=width;
                    writers[i]=geometry[i].Open();
                }
                try{
                    var sink=new VectorSink{W=writers};Generate(stars,end,camera,ref sink);
                }finally{
                    for(int i=0;i<writers.Length;i++){writers[i].Close();writers[i]=null;}
                    empty=false;
                }
            }
            static double Width(int band,double scale){return (band<7?1.35:1.2-.23*(band-7))/scale;}
            bool TryNative(IEnumerable<OrbitalStar> stars,double end,double scale,AccretionCamera camera,Point blue,Point red,
                Visual view,Point gateOnView,FlowBatch sharedDevice){
                var source=PresentationSource.FromVisual(view);
                if(source?.CompositionTarget==null || !(source.RootVisual is Visual root))return false;
                var toRoot=root==view?null:view.TransformToAncestor(root);
                var toDevice=source.CompositionTarget.TransformToDevice;
                Point Pixel(Point at){return toDevice.Transform(toRoot==null?at:toRoot.Transform(at));}
                // Keep the fractional phase of the WPF vector, including the panel's own transform.
                // Win32 screen coordinates would round it to an integer before the stroke is rasterized.
                Point phase=Pixel(gateOnView);
                Vector xAxis=Pixel(gateOnView+new Vector(1,0))-phase,yAxis=Pixel(gateOnView+new Vector(0,1))-phase;
                if(Math.Abs(xAxis.Y)>.0001 || Math.Abs(yAxis.X)>.0001)return false;
                double px=scale*xAxis.X,py=scale*yAxis.Y;
                if(px<=0 || py<=0)return false;
                int w=(int)Math.Ceiling(144*px)+2,h=(int)Math.Ceiling(60*py)+2;
                if(w>8192 || h>4096)return false;
                var bounds=new Rect((Math.Floor(phase.X-72*px)-phase.X)/px,(Math.Floor(phase.Y-26*py)-phase.Y)/py,w/px,h/py);
                if(gpu==null)gpu=new FlowBatch();
                if(!gpu.BeginRibbonFrame(view,bounds,w,h,sharedDevice))return false;
                try{
                    if(gpuGeometry && end==1){
                        camera.WriteProjection(projection);
                        if(InsideEdge(0,bounds.Left+2/px,1) && InsideEdge(0,bounds.Right-2/px,-1) &&
                           InsideEdge(2,bounds.Top+2/py,1) && InsideEdge(2,bounds.Bottom-2/py,-1)){
                            int n=0;
                            foreach(var star in stars){
                                if(star.Channel==null)continue;
                                if(n==rotations.Length/4){n=-1;break;}
                                rotations[n*4]=(float)Math.Cos(star.Channel.EntryAngle);rotations[n*4+1]=(float)Math.Sin(star.Channel.EntryAngle);n++;
                            }
                            if(n>0){
                                for(int i=0;i<Bands;i++)widths[i]=(float)Width(i,scale);
                                UsingMesh=gpu.FinishOrbitMesh(orbitTable,rotations,n,projection,palette,widths,blue,red);
                                if(UsingMesh)return true;
                                if(!gpu.AcceptsSprites)return false;
                            }
                        }
                    }
                    vertices.Count=0;Generate(stars,end,camera,ref vertices);
                    if(vertices.Count==0)return false;
                    // A future camera pose outside this compact atlas can always use the exact vector path.
                    double left=bounds.Left+2/px,right=bounds.Right-2/px,top=bounds.Top+2/py,bottom=bounds.Bottom-2/py;
                    for(int i=0;i<vertices.Count;i++){
                        var p=vertices.Points[i];
                        if(p.X<left || p.X>right || p.Y<top || p.Y>bottom)return false;
                    }
                    for(int i=0;i<Bands;i++)widths[i]=(float)Width(i,scale);
                    return gpu.FinishRibbons(vertices.Points,vertices.Count,palette,widths,blue,red);
                }finally{gpu.CancelRibbonFrame();}
            }
            static void Generate<T>(IEnumerable<OrbitalStar> stars,double end,AccretionCamera camera,ref T sink) where T:struct,IStrokeSink {
                foreach(var star in stars){
                    if(star.Channel==null)continue;
                    double c=Math.Cos(star.Channel.EntryAngle),s=Math.Sin(star.Channel.EntryAngle);
                    var last=orbit[0].At(c,s);double depth=AccretionPath.DiskDepth(last-new Point(),camera);
                    var prev=camera.Project(last);int active=-1,band=0;
                    for(int k=1;k<=Steps;k++){
                        double t=k/(double)Steps;
                        var sample=t<=end?orbit[k]:new Sample(end);
                        var local=sample.At(c,s);var point=camera.Project(local);
                        double next=AccretionPath.DiskDepth(local-new Point(),camera);
                        if(k>cuts[band+1])band++;
                        int slot=band+(depth<0?0:Bands);
                        if(slot!=active)sink.Begin(slot,prev);
                        if((next<0)!=(depth<0)){
                            var cross=camera.Project(last+(local-last)*(depth/(depth-next)));
                            sink.Line(slot,cross);
                            slot=band+(next<0?0:Bands);sink.Begin(slot,cross);
                        }
                        sink.Line(slot,point);
                        active=slot;last=local;prev=point;depth=next;
                        if(t>=end)break;
                    }
                }
            }
        }

        // A retained ribbon in the same projected disk plane and time coordinates as a mote.
        sealed class DiskRibbon
        {
            struct Edge
            {
                internal double Time;
                internal Point Center, Top, Bottom;
                internal Color Color;
            }

            sealed class Band
            {
                readonly Edge[] edges;
                readonly Geometry complete;
                internal readonly GeometryDrawing Art;
                double shown=-1;

                internal Band(List<Edge> samples)
                {
                    edges=samples.ToArray();
                    var first=edges[0];var last=edges[edges.Length-1];
                    var brush=new LinearGradientBrush(first.Color,last.Color,0) {
                        MappingMode=BrushMappingMode.Absolute,StartPoint=first.Center,EndPoint=last.Center };
                    brush.Freeze();
                    complete=Outline(last.Time);
                    Art=new GeometryDrawing(brush,null,Geometry.Empty);
                }

                Geometry Outline(double stop)
                {
                    int tip=1;
                    while(tip<edges.Length-1 && edges[tip].Time<stop)tip++;
                    var a=edges[tip-1];var b=edges[tip];
                    double f=UiMotion.Unit((stop-a.Time)/(b.Time-a.Time));
                    var geometry=new StreamGeometry();
                    using(var ctx=geometry.Open()){
                        ctx.BeginFigure(edges[0].Top,true,true);
                        for(int i=1;i<tip;i++)ctx.LineTo(edges[i].Top,true,false);
                        ctx.LineTo(a.Top+(b.Top-a.Top)*f,true,false);
                        ctx.LineTo(a.Bottom+(b.Bottom-a.Bottom)*f,true,false);
                        for(int i=tip-1;i>=0;i--)ctx.LineTo(edges[i].Bottom,true,false);
                    }
                    geometry.Freeze();return geometry;
                }

                internal void Reveal(double time)
                {
                    double from=edges[0].Time,to=edges[edges.Length-1].Time;
                    double stop=Math.Max(from,Math.Min(to,time));
                    if(shown==stop)return;
                    shown=stop;
                    Art.Geometry=stop<=from?Geometry.Empty:stop>=to?complete:Outline(stop);
                }
            }

            internal readonly DrawingGroup Rear=new DrawingGroup(),Front=new DrawingGroup();
            readonly TranslateTransform offset=new TranslateTransform();
            readonly List<Band> bands=new List<Band>();
            AccretionPath path;

            internal DiskRibbon(){Rear.Transform=Front.Transform=offset;}

            internal void Place(AccretionPath next)
            {
                offset.X=next.Gate.X;offset.Y=next.Gate.Y;
                if(path!=null && path.Scale==next.Scale && path.EntryAngle==next.EntryAngle)return;
                path=next;
                bands.Clear();Rear.Children.Clear();Front.Children.Clear();
            }

            Edge At(double t)
            {
                var world=path.StreamDiskPoint(t);
                var p=new Point(world.X-path.Gate.X,world.Y-path.Gate.Y);
                var tangent=UnitTangent(path.StreamDiskPoint(Math.Min(1,t+.0001))-path.StreamDiskPoint(Math.Max(0,t-.0001)));
                double plunge=UiMotion.Unit((t*AccretionPath.StreamDiskTime-AccretionPath.StreamOrbit)/AccretionPath.StreamPlunge);
                double width=(.4+.13*Math.Sin(Math.PI*t))*(1-plunge);
                var n=new Vector(-tangent.Y,tangent.X)*width;
                double side=path.Doppler(world),shift=UiMotion.Unit(5*t);
                var heat=DopplerColor(ThermalColor(outerHeat,1,t),side,shift);
                double opacity=.55*(1+.30*(DopplerGain(side)-1)*shift)*(1-plunge);
                return new Edge{Time=t,Center=p,Top=p+n,Bottom=p-n,Color=A(heat,opacity)};
            }

            bool Behind(double t){return path.Behind(path.StreamDiskPoint(t),false);}

            void Build()
            {
                const int steps=120;
                var samples=new List<Edge>(9){At(0)};
                bool rear=Behind(0);
                void Finish(){
                    if(samples.Count<2)return;
                    var band=new Band(samples);bands.Add(band);
                    (rear?Rear:Front).Children.Add(band.Art);
                    var last=samples[samples.Count-1];samples.Clear();samples.Add(last);
                }
                for(int i=1;i<=steps;i++){
                    double t=i/(double)steps;
                    bool nextRear=Behind(t);
                    if(nextRear!=rear){
                        // Split at the depth crossing itself. Both camera layers share
                        // its two edge vertices instead of overpainting each other.
                        double low=(i-1)/(double)steps,high=t;
                        for(int j=0;j<20;j++){
                            double mid=(low+high)*.5;
                            if(Behind(mid)==rear)low=mid;else high=mid;
                        }
                        double crossing=(low+high)*.5;
                        if(crossing-samples[samples.Count-1].Time>1e-8)samples.Add(At(crossing));
                        Finish();rear=nextRear;
                    }
                    if(t-samples[samples.Count-1].Time>1e-8)samples.Add(At(t));
                    if(i%6==0)Finish();
                }
            }

            internal void Reveal(double t)
            {
                if(bands.Count==0 && t>0)Build();
                foreach(var band in bands)band.Reveal(t);
            }
        }
    }
}
