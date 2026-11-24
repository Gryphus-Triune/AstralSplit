using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        sealed class AccretingDust
        {
            const int Count=896, Sectors=40, Optical=Sectors;
            const double Tau=2*Math.PI, Coast=92, Dissolve=320;
            const double TiltX=.9816271834, TiltY=-.1908089954; // The disk is inclined by -11 degrees.
            readonly Grain[] grains=new Grain[Count];
            readonly DustBatch back,front;
            readonly DrawingGroup farPlane,nearPlane;
            readonly double[] normalization=new double[2*Sectors], arrived=new double[2*Sectors], levels=new double[2*Sectors];
            readonly DepositMask depositMask=new DepositMask();
            internal StarfieldLayer Surface;
            FlowBatch sprites;
            bool mounted;
            internal Brush Mask { get { return depositMask.Brush; } }
            internal double Duration { get; private set; }
            bool hasDrawing;

            struct Grain
            {
                internal double Angle, Sweep, Edge, Start, Flight, Radius, Flatten, Midline, Size, Weight, Fraction;
                internal int Sector, Color;
                internal bool IsOptical;
            }

            internal AccretingDust(DrawingGroup far,DrawingGroup near)
            {
                farPlane=far;nearPlane=near;
                back=new DustBatch(far);front=new DustBatch(near);
                var weight=new double[normalization.Length];
                var random=new Random(0x51C08);
                for(int i=0;i<grains.Length;i++){
                    // Broad filaments share a direction of rotation, never a rigid trajectory.
                    double spread=random.NextDouble()+random.NextDouble()-1;
                    var g=new Grain();
                    g.Angle=Tau*((i%6+.34+.64*spread)/6.0);
                    g.Edge=(1.035+.13*random.NextDouble())/Math.Max(Math.Abs(Math.Cos(g.Angle)),Math.Abs(Math.Sin(g.Angle)));
                    g.Sweep=4.85+2.85*random.NextDouble()+.46*spread;
                    g.IsOptical=random.Next(5)==0;
                    double material=random.NextDouble();
                    g.Radius=g.IsOptical?24+7*material:27+49*material;
                    g.Flatten=g.IsOptical?.88+.10*random.NextDouble():.12+.035*random.NextDouble();
                    g.Midline=g.IsOptical?-.6:4.25;
                    g.Start=25+440*random.NextDouble();
                    double end=g.Angle+g.Sweep;
                    g.Flight=1300+385*random.NextDouble()+685*(.5+.5*Math.Cos(end-.4))+(g.IsOptical?65:0);
                    double glint=Math.Pow(random.NextDouble(),2.2);
                    g.Size=.30+.74*glint;
                    g.Color=glint>.79?(i%5==0?4:3):glint>.34?2:random.Next(4)==0?1:0;
                    g.Weight=.55+glint;
                    double dockAngle=end+1.52*g.Sweep*Coast/g.Flight;
                    double bin=SectorAt(dockAngle);
                    g.Sector=(g.IsOptical?Optical:0)+(int)bin;
                    g.Fraction=bin-(int)bin;
                    grains[i]=g;
                    AddWeight(weight,g,g.Weight);
                    Duration=Math.Max(Duration,g.Start+g.Flight+Dissolve);
                }
                for(int i=0;i<weight.Length;i++){
                    int a=Previous(i),b=Next(i);
                    double sum=weight[i]+.52*(weight[a]+weight[b])+.15*(weight[Previous(a)]+weight[Next(b)]);
                    normalization[i]=sum>0?1/sum:0;
                }
            }

            static double SectorAt(double theta)
            {
                double turns=theta/Tau;
                return (turns-Math.Floor(turns))*Sectors;
            }

            static int Next(int bin){return bin%Sectors==Sectors-1?bin-Sectors+1:bin+1;}
            static int Previous(int bin){return bin%Sectors==0?bin+Sectors-1:bin-1;}
            static void AddWeight(double[] field,in Grain g,double mass)
            {
                field[g.Sector]+=mass*(1-g.Fraction);
                field[Next(g.Sector)]+=mass*g.Fraction;
            }

            struct DustView
            {
                internal double HalfWidth,HalfHeight,CenterX,CenterY;
                internal DustView(Size size,Point gate,double scale)
                {
                    HalfWidth=size.Width/(2*scale);HalfHeight=size.Height/(2*scale);
                    CenterX=HalfWidth-gate.X/scale;CenterY=HalfHeight-gate.Y/scale;
                }
            }

            Point Position(in Grain g,in DustView view,double age,out double depth)
            {
                double u=UiMotion.Unit(age/g.Flight), rest=1-u;
                double angle=g.Angle+g.Sweep*u*(.48+.52*u);
                if(age>g.Flight)angle+=1.52*g.Sweep*Coast/g.Flight*(1-Math.Exp(-(age-g.Flight)/Coast));
                double c,s;OrbitSinCos(angle,out s,out c);
                depth=s;
                double x=g.Radius*c, y=g.Midline+g.Radius*g.Flatten*s;
                double diskX=TiltX*x-TiltY*y, diskY=TiltY*x+TiltX*y;
                if(Surface!=null){
                    var camera=g.IsOptical?Surface.photonAttitude:Surface.diskAttitude;
                    var projected=camera.Project(new Point(diskX,diskY));
                    depth=camera.Depth(new Vector(diskX+TiltY*g.Midline,diskY-TiltX*g.Midline));
                    diskX=projected.X;diskY=projected.Y;
                }
                double wide=rest*Math.Sqrt(rest)*(1+.32*u);
                return new Point(diskX+wide*(view.CenterX+g.Edge*view.HalfWidth*c-diskX),
                    diskY+wide*(view.CenterY+g.Edge*view.HalfHeight*s-diskY));
            }

            static readonly double[] sine=BuildSine();
            static double[] BuildSine(){
                var table=new double[8193];
                for(int i=0;i<8192;i++)table[i]=Math.Sin(i*(Tau/8192));
                table[8192]=table[0];return table;
            }
            static void OrbitSinCos(double angle,out double s,out double c){
                double at=angle*(8192/Tau);int cell=(int)Math.Floor(at);
                double f=at-cell;int a=cell&8191,b=(cell+2048)&8191;
                s=sine[a]+f*(sine[a+1]-sine[a]);c=sine[b]+f*(sine[b+1]-sine[b]);
            }

            internal double Draw(double ms,Size size,Point gate,double scale)
            {
                var view=new DustView(size,gate,scale);
                Array.Clear(arrived,0,arrived.Length);
                double deposited=0;
                bool native=false;
                if(Surface!=null && scale>0){
                    if(sprites==null)sprites=new FlowBatch(true);
                    native=sprites.BeginAt(Surface,new Rect(-gate.X/scale,-gate.Y/scale,size.Width/scale,size.Height/scale),scale);
                }
                if(native!=mounted){
                    if(native){
                        if(hasDrawing){back.Open();back.Close();front.Open();front.Close();}
                        farPlane.Children.Add(sprites.Back);nearPlane.Children.Add(sprites.Front);
                    }else{farPlane.Children.Remove(sprites.Back);nearPlane.Children.Remove(sprites.Front);}
                    mounted=native;
                }
                if(!native){back.Open();front.Open();}
                try{
                    for(int i=0;i<grains.Length;i++){
                        ref var g=ref grains[i];
                        double age=ms-g.Start, dockAge=age-g.Flight;
                        if(dockAge> -65){
                            double deposit=SmoothBand(-65,Dissolve,dockAge);
                            AddWeight(arrived,g,g.Weight*deposit);deposited+=deposit;
                        }
                        if(age<=0 || dockAge>=Dissolve)continue;
                        double fade=SmoothBand(0,110,age)*(1-SmoothBand(28,Dissolve,dockAge));
                        var p=Position(g,view,age,out double depth);
                        var previous=Position(g,view,Math.Max(0,age-14),out _);
                        var motion=p-previous;
                        double distance=motion.Length;
                        if(distance<.0001)motion=new Vector(1,0);else motion/=distance;
                        double u=UiMotion.Unit(age/g.Flight);
                        double width=g.Size*Math.Sqrt(fade)*(.78+.32*u)/CoreMagnification;
                        double tail=Math.Min(1.25*distance,(.8+g.Size*(2+7.4*SmoothBand(.24,.88,u)))/CoreMagnification);
                        bool behind=g.IsOptical || depth<=0;
                        if(native)sprites.Grain(p,motion,width,tail,g.Color,behind);
                        else (behind?back:front).Grain(p,motion,width,tail,g.Color);
                    }
                }finally{if(native)sprites.End();else{back.Close();front.Close();}}
                hasDrawing=true;
                for(int i=0;i<levels.Length;i++){
                    int a=Previous(i),b=Next(i);
                    double mass=arrived[i]+.52*(arrived[a]+arrived[b])+.15*(arrived[Previous(a)]+arrived[Next(b)]);
                    double t=normalization[i]>0?UiMotion.Unit(mass*normalization[i]):deposited/Count;
                    levels[i]=t*(1.52-.52*t);
                }
                depositMask.Update(levels,deposited<=0?0:deposited>=Count?1:-1);
                return deposited/Count;
            }

            internal void Clear()
            {
                if(mounted){farPlane.Children.Remove(sprites.Back);nearPlane.Children.Remove(sprites.Front);mounted=false;}
                sprites?.Dispose();sprites=null;
                if(!hasDrawing)return;
                back.Open();back.Close();front.Open();front.Close();hasDrawing=false;
            }

            // One retained geometry per pigment and depth, with no particle-level WPF objects or clocks.
            sealed class DustBatch
            {
                readonly StreamGeometry[] shapes=new StreamGeometry[8];
                readonly StreamGeometryContext[] pens=new StreamGeometryContext[8];
                internal DustBatch(DrawingGroup group)
                {
                    var colors=new[]{Color.FromArgb(164,224,165,119),Color.FromArgb(176,210,192,196),
                        Color.FromArgb(219,255,205,144),Color.FromRgb(255,237,204),Color.FromRgb(220,235,255)};
                    using(var dc=group.Open()){
                        Add(dc,5,Color.FromArgb(31,255,187,122));
                        Add(dc,6,Color.FromArgb(83,255,198,139));
                        Add(dc,7,Color.FromArgb(77,200,216,244));
                        for(int i=0;i<colors.Length;i++)Add(dc,i,colors[i]);
                    }
                }
                void Add(DrawingContext dc,int i,Color color)
                {
                    var shape=shapes[i]=new StreamGeometry{FillRule=FillRule.Nonzero};
                    var brush=new SolidColorBrush(color);brush.Freeze();dc.DrawGeometry(brush,null,shape);
                }
                internal void Open(){for(int i=0;i<shapes.Length;i++)pens[i]=shapes[i].Open();}
                internal void Close(){for(int i=0;i<pens.Length;i++)pens[i].Close();}
                void Bead(int index,Point p,Vector along,Vector side)
                {
                    var pen=pens[index];
                    pen.BeginFigure(p+along,true,true);
                    pen.BezierTo(p+along+side,p-along+side,p-along,true,false);
                    pen.BezierTo(p-along-side,p+along-side,p+along,true,false);
                }
                internal void Grain(Point p,Vector along,double width,double tail,int color)
                {
                    var side=new Vector(-along.Y,along.X);
                    if(color>=2){
                        var trail=pens[color==4?7:6];
                        var taper=side*(width*.68);
                        var shoulder=p-along*(tail*.26);
                        trail.BeginFigure(p+taper,true,true);
                        trail.QuadraticBezierTo(shoulder+taper,p-along*(width+tail),true,false);
                        trail.QuadraticBezierTo(shoulder-taper,p-taper,true,false);
                        Bead(5,p,along*(width*2.65),side*(width*3.55));
                    }
                    Bead(color,p,along*width,side*(width*1.27));
                }
            }

            // The alpha at each disk pixel is supplied by grains landing in that part of the inclined
            // equator or lensed rim. Project this lookup once; only the sector exposures change.
            sealed class DepositMask
            {
                const int Width=192,Height=144;
                static readonly Rect Area=new Rect(-99,-79,198,158);
                readonly WriteableBitmap image=new WriteableBitmap(Width,Height,96,96,PixelFormats.Pbgra32,null);
                readonly int[] pixels=new int[Width*Height];
                struct Sample { internal byte Plane,Rim; internal float PlanePart,RimPart,Equator; }
                readonly Sample[] samples=new Sample[Width*Height];
                int solid=0; // A new bitmap is transparent.
                internal readonly Brush Brush;
                readonly DustExposure exposure;
                internal DepositMask()
                {
                    Brush=new ImageBrush(image){ViewportUnits=BrushMappingMode.Absolute,Viewport=Area,Stretch=Stretch.Fill};
                    var planeMap=new int[samples.Length];var rimMap=new int[samples.Length];
                    for(int y=0;y<Height;y++)for(int x=0;x<Width;x++){
                        double px=Area.X+(x+.5)*Area.Width/Width,py=Area.Y+(y+.5)*Area.Height/Height;
                        double u=TiltX*px+TiltY*py,v=-TiltY*px+TiltX*py;
                        double disk=SectorAt(Math.Atan2((v-4.25)/.14,u));
                        double rim=SectorAt(Math.Atan2((v+.6)/.94,u));
                        samples[y*Width+x]=new Sample{Plane=(byte)disk,Rim=(byte)(Optical+(int)rim),
                            PlanePart=(float)(disk-(int)disk),RimPart=(float)(rim-(int)rim),
                            Equator=(float)(1-SmoothBand(7.0,14.0,Math.Abs(v-4.25)))};
                        // Two angular charts put their seams on opposite sides of the
                        // horizon. The shader samples the continuous chart on either side.
                        planeMap[y*Width+x]=PackAngle(disk);
                        rimMap[y*Width+x]=PackAngle(rim);
                    }
                    exposure=DustExposure.Create(planeMap,rimMap,Width,Height,Area);
                    if(exposure!=null)Brush=exposure.Mask;
                }
                static int PackAngle(double angle){
                    int whole=(int)angle;
                    return unchecked((int)0xff000000)|(whole<<16)|((int)Math.Round(255*(angle-whole))<<8)|((whole+Sectors/2)%Sectors);
                }
                internal void Update(double[] levels,int uniform)
                {
                    if(uniform>=0 && solid==uniform)return;
                    solid=uniform;
                    if(exposure!=null){exposure.Update(levels);return;}
                    for(int i=0;i<pixels.Length;i++){
                        var s=samples[i];
                        double plane=levels[s.Plane]+s.PlanePart*(levels[Next(s.Plane)]-levels[s.Plane]);
                        double rim=levels[s.Rim]+s.RimPart*(levels[Next(s.Rim)]-levels[s.Rim]);
                        int alpha=(int)((rim+s.Equator*(plane-rim))*255+.5);
                        pixels[i]=unchecked(alpha*0x01010101);
                    }
                    image.WritePixels(new Int32Rect(0,0,Width,Height),pixels,Width*4,0);
                }
            }
        }
    }
}
