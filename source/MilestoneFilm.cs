using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AstralSplit
{
    internal enum FilmKind { Comet, Pulsar, Nova, Diamond, Corona }

    internal sealed class MilestoneShader : ShaderEffect
    {
        static readonly DependencyProperty Coordinates=DependencyProperty.Register("Coordinates",typeof(Point4D),typeof(MilestoneShader),
            new UIPropertyMetadata(new Point4D(),PixelShaderConstantCallback(0)));
        static readonly DependencyProperty Sample=DependencyProperty.Register("Sample",typeof(Point4D),typeof(MilestoneShader),
            new UIPropertyMetadata(new Point4D(),PixelShaderConstantCallback(1)));
        internal MilestoneShader(FilmKind kind){
            PixelShader=GlassEffect.LoadProgram("film"+(kind==FilmKind.Corona?FilmKind.Diamond:kind)+"PS");
            UpdateShaderValue(Coordinates);UpdateShaderValue(Sample);
        }
        internal bool Available => PixelShader!=null;
        internal void Update(Point4D basis,Point4D frame){
            if((Point4D)GetValue(Coordinates)!=basis)SetValue(Coordinates,basis);
            if((Point4D)GetValue(Sample)!=frame)SetValue(Sample,frame);
        }
    }

    internal class MilestoneFilm : DrawingVisual
    {
        internal readonly FilmKind Kind;
        internal Rect Area {get;private set;}=Rect.Empty;
        internal Point4D Basis {get;private set;}
        internal Point4D Frame {get;private set;}
        internal bool Playing {get;private set;}
        internal bool Procedural {get;private set;}
        MilestoneShader shader;
        BitmapSource poster;
        Rect posterArea=Rect.Empty;
        double stillReach;
        readonly RectangleGeometry aperture=new RectangleGeometry();
        readonly DrawingGroup still=new DrawingGroup(), fallbackFront=new DrawingGroup();
        readonly ScaleTransform frontScale=new ScaleTransform();
        readonly TranslateTransform cometTravel=new TranslateTransform();
        internal MilestoneFilm(FilmKind kind){Kind=kind;Opacity=0;}

        internal void Clear(){
            if(Area.IsEmpty)return;
            Area=Rect.Empty;Opacity=0;Playing=false;Procedural=false;Effect=null;
            using(RenderOpen()){}
        }

        internal void Show(Point center,double unit,Rect field,double seconds,double drawingScale,bool animate){
            if(field.IsEmpty || field.Width<=0 || field.Height<=0 || unit<=0){Clear();return;}
            bool procedural=animate && GlassEffect.Hardware3(this);
            if(procedural && shader==null)shader=new MilestoneShader(Kind);
            procedural=procedural && shader.Available;
            bool comet=Kind==FilmKind.Comet;
            double width=comet?2.4:2,height=comet?1:2;
            var art=new Rect(center.X-width*unit/2,center.Y-height*unit/2,width*unit,height*unit);
            bool reconnect=Area.IsEmpty || Procedural!=procedural;
            bool placeArt=art!=posterArea;
            Area=field;posterArea=art;Playing=Procedural=procedural;
            // Keep the emitter in its own physical units. Only the propagation field follows the window.
            var origin=Kind==FilmKind.Nova?new Point(center.X-.062*unit,center.Y-.012*unit):center;
            double dx=Math.Max(Math.Abs(field.Left-origin.X),Math.Abs(field.Right-origin.X));
            double dy=Math.Max(Math.Abs(field.Top-origin.Y),Math.Abs(field.Bottom-origin.Y));
            double reach=Math.Sqrt(dx*dx+dy*dy)/unit;
            Basis=new Point4D((field.Left-center.X)/unit,(center.Y-field.Top)/unit,field.Width/unit,-field.Height/unit);
            Frame=new Point4D(seconds,comet?field.Width/unit:reach,Math.Max(1,unit*drawingScale*VisualTreeHelper.GetDpi(this).PixelsPerDip),
                comet?field.Height/unit:Kind==FilmKind.Diamond?1:0);
            if(procedural)shader.Update(Basis,Frame);
            if(aperture.Rect!=field)aperture.Rect=field;
            if(reconnect || placeArt || (!procedural && Math.Abs(reach*unit-stillReach)>Math.Max(.5,.02*stillReach))){
                if(poster==null)poster=StellarSkyArt.ReadImage("milestone-"+Kind.ToString().ToLowerInvariant()+".png");
                stillReach=reach*unit;
                BuildStill(center,origin,stillReach,unit,art);
            }
            if(reconnect){
                Effect=procedural?shader:null;
                using(var dc=RenderOpen()){
                    dc.DrawGeometry(Brushes.Transparent,null,aperture);dc.PushClip(aperture);
                    dc.DrawDrawing(still);dc.DrawDrawing(fallbackFront);dc.Pop();
                }
                Opacity=1;
            }
            if(procedural)return;
            if(Kind==FilmKind.Nova){
                double age=Math.Max(0,seconds-1.16),fraction=Math.Min(1,age/5.8);
                frontScale.ScaleX=frontScale.ScaleY=animate?.006+age/5.8*(1.15-.15*fraction):.90;
                fallbackFront.Opacity=animate?Math.Min(1,age/.28)*Math.Max(0,1-age/7):seconds<9?.30:0;
            }else if(comet){
                double t=animate?Math.Max(0,Math.Min(1,seconds/6.6)):.54;
                double eased=t*t*(3-2*t);
                cometTravel.X=(eased-.5)*field.Width*1.1;cometTravel.Y=(.5-t)*field.Height*.84;
            }
        }

        void BuildStill(Point center,Point origin,double reach,double unit,Rect art){
            still.Transform=Kind==FilmKind.Comet?cometTravel:null;
            using(var dc=still.Open()){
                if(Kind==FilmKind.Pulsar || Kind==FilmKind.Diamond || Kind==FilmKind.Corona){
                    bool beam=Kind==FilmKind.Pulsar;
                    var source=beam?new Point(center.X-.018*unit,center.Y+.008*unit):center;
                    int count=beam?2:12;
                    for(int i=0;i<count;i++){
                        double angle=beam?-.6954+i*Math.PI:i*2*Math.PI/count+.14*Math.Sin(i*5.2);
                        var direction=new Vector(Math.Cos(angle),Math.Sin(angle));var side=new Vector(-direction.Y,direction.X);
                        double start=beam?.055:Kind==FilmKind.Diamond?.254:.015;
                        var root=source+direction*(unit*start);
                        var tip=source+direction*(reach*1.12);
                        var brush=new LinearGradientBrush(beam?Color.FromArgb(85,135,167,255):Color.FromArgb(55,255,204,153),Colors.Transparent,0){
                            MappingMode=BrushMappingMode.Absolute,StartPoint=source,EndPoint=tip};brush.Freeze();
                        var wedge=new StreamGeometry();using(var shape=wedge.Open()){
                            shape.BeginFigure(root,true,true);
                            shape.QuadraticBezierTo(source+direction*(reach*.38)+side*(unit*.055),tip,true,true);
                            shape.QuadraticBezierTo(source+direction*(reach*.38)-side*(unit*.048),root,true,true);
                        }wedge.Freeze();dc.DrawGeometry(brush,null,wedge);
                    }
                }
                if(Kind==FilmKind.Comet){
                    // The baked dust tail touches the lower margin. Let it dissipate across its final texels.
                    var feather=new LinearGradientBrush{StartPoint=new Point(.5,0),EndPoint=new Point(.5,1)};
                    feather.GradientStops.Add(new GradientStop(Colors.White,.90));
                    feather.GradientStops.Add(new GradientStop(Colors.Transparent,1));feather.Freeze();
                    dc.PushOpacityMask(feather);
                }
                dc.DrawImage(poster,art);
                if(Kind==FilmKind.Comet)dc.Pop();
            }
            using(var dc=fallbackFront.Open()){
                if(Kind==FilmKind.Nova){
                    frontScale.CenterX=origin.X;frontScale.CenterY=origin.Y;fallbackFront.Transform=frontScale;
                    var rim=new RadialGradientBrush{RadiusX=.5,RadiusY=.5};
                    rim.GradientStops.Add(new GradientStop(Colors.Transparent,.965));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(65,185,135,251),.982));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(190,139,194,255),.994));
                    rim.GradientStops.Add(new GradientStop(Colors.Transparent,1));rim.Freeze();
                    dc.DrawEllipse(rim,null,origin,reach,reach);
                }
            }
        }
    }
}
