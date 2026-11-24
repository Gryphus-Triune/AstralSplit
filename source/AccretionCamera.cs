using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace AstralSplit
{
    // The texture and the stream geometry are authored in the neutral camera's image plane.
    // Back-project each ray onto its emitting plane before rotating the view. The homogeneous
    // denominator accounts for the different distances to its near and far sides.
    internal sealed class AccretionCamera
    {
        const double focalLength=235;
        static readonly Rect frame=new Rect(-120,-94,240,188);
        readonly double slope;
        sealed class Plate {internal DrawingGroup Output;internal Drawing Native,Projected;internal bool ReceivesDeposits;}
        readonly System.Collections.Generic.List<Plate> plates=new System.Collections.Generic.List<Plate>();
        static readonly bool useIdentity=Environment.GetEnvironmentVariable("ASTRALSPLIT_IDENTITY_PLATES")=="1";
        bool fused;
        void SelectProjection(){
            if(!useIdentity)return;
            foreach(var p in plates){
                var image=forward.IsIdentity && !(fused && p.ReceivesDeposits)?p.Native:p.Projected;
                if(p.Output.Children[0]!=image)p.Output.Children[0]=image;
            }
        }
        internal bool SetExposure(Brush source){
            fused=depositOptics!=null && depositOptics.SetExposure(source,frame);SelectProjection();return fused;
        }
        readonly MatrixTransform fallback=new MatrixTransform();
        readonly AccretionProjection optics=new AccretionProjection();
        AccretionProjection depositOptics;
        Matrix3D forward=Matrix3D.Identity,reverse=Matrix3D.Identity;
        Vector3D basisX,basisY;
        internal int Revision {get;private set;}
        internal AccretionCamera(double depthSlope){slope=depthSlope;Orient(Matrix3D.Identity,1);}
        internal void Orient(Matrix3D rotation,double scale){
            double kx=.190808995376545*slope,ky=.981627183447664*slope;
            var a=rotation.Transform(new Vector3D(1,0,kx));
            var b=rotation.Transform(new Vector3D(0,1,ky));
            var m=new Matrix3D(a.X*scale,a.Y*scale,0,(kx-a.Z)/focalLength,
                b.X*scale,b.Y*scale,0,(ky-b.Z)/focalLength,0,0,1,0,0,0,0,1);
            basisX=a;basisY=b;
            if(m==forward)return;
            forward=m;reverse=m;reverse.Invert();Revision++;
            fallback.Matrix=new Matrix(m.M11,m.M12,m.M21,m.M22,0,0);
            optics.SetInverse(reverse);depositOptics?.SetInverse(reverse);SelectProjection();
        }
        internal Point Project(Point local){return Map(forward,local);}
        internal void WriteProjection(float[] values){
            values[0]=(float)forward.M11;values[1]=(float)forward.M21;values[2]=(float)forward.M12;values[3]=(float)forward.M22;
            values[4]=(float)forward.M14;values[5]=(float)forward.M24;values[6]=(float)basisX.Z;values[7]=(float)basisY.Z;
        }
        internal Point Unproject(Point image){return Map(reverse,image);}
        static Point Map(Matrix3D m,Point p){
            double w=1+m.M14*p.X+m.M24*p.Y;
            return new Point((p.X*m.M11+p.Y*m.M21)/w,(p.X*m.M12+p.Y*m.M22)/w);
        }
        internal double Depth(Vector displacement){return basisX.Z*displacement.X+basisY.Z*displacement.Y;}
        internal Drawing ProjectPlate(Drawing source){return MakePlate(source,false);}
        internal Drawing ProjectDepositPlate(Drawing source){return MakePlate(source,true);}
        Drawing MakePlate(Drawing source,bool receivesDeposits){
            var shader=optics;
            if(receivesDeposits && AccretionProjection.Combine){
                if(depositOptics==null){depositOptics=new AccretionProjection();depositOptics.SetInverse(reverse);}
                shader=depositOptics;
            }
            if(!shader.Available){var group=new DrawingGroup{Transform=fallback};group.Children.Add(source);return group;}
            var visual=new DrawingVisual{Effect=shader};
            using(var dc=visual.RenderOpen()){
                dc.DrawRectangle(Brushes.Transparent,null,frame);
                dc.PushClip(new RectangleGeometry(frame));dc.DrawDrawing(source);dc.Pop();
            }
            var boundary=new ContainerVisual();boundary.Children.Add(visual);
            var sample=new VisualBrush(boundary){AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,Viewbox=frame,Stretch=Stretch.Fill};
            var bounds=new RectangleGeometry(frame);bounds.Freeze();
            var projected=new GeometryDrawing(sample,null,bounds);
            if(!useIdentity)return projected;
            var direct=new DrawingGroup{ClipGeometry=bounds};direct.Children.Add(source);
            var output=new DrawingGroup();output.Children.Add(forward.IsIdentity&&!(fused && receivesDeposits)?(Drawing)direct:projected);
            plates.Add(new Plate{Output=output,Native=direct,Projected=projected,ReceivesDeposits=receivesDeposits});return output;
        }
    }

    internal sealed class AccretionProjection : ShaderEffect
    {
        static readonly PixelShader program=GlassEffect.LoadProgram("AccretionProjection");
        static readonly DependencyProperty input=RegisterPixelShaderSamplerProperty("Input",typeof(AccretionProjection),0,SamplingMode.Bilinear);
        static readonly PixelShader masked=GlassEffect.LoadProgram("MaskedProjection");
        static readonly DependencyProperty exposure=RegisterPixelShaderSamplerProperty("Exposure",typeof(AccretionProjection),1,SamplingMode.Bilinear);
        internal static readonly bool Combine=Environment.GetEnvironmentVariable("ASTRALSPLIT_FUSED_DEPOSIT")=="1";
        internal bool SetExposure(Brush mask,Rect area){
            if(!Combine || program==null || masked==null)return false;
            if(mask==null){PixelShader=program;SetValue(exposure,null);return false;}
            var source=new DrawingVisual();
            using(var d=source.RenderOpen()){d.DrawRectangle(Brushes.Transparent,null,area);d.DrawRectangle(mask,null,area);}
            var view=new VisualBrush(source){AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,Viewbox=area,Stretch=Stretch.Fill};
            SetValue(exposure,view);PixelShader=masked;UpdateShaderValue(exposure);return true;
        }
        static readonly DependencyProperty first=Row("First",new Point3D(1,0,0),0), second=Row("Second",new Point3D(0,1,0),1),
            divisor=Row("Divisor",new Point3D(0,0,1),2);
        static DependencyProperty Row(string name,Point3D initial,int index){return DependencyProperty.Register(name,typeof(Point3D),
            typeof(AccretionProjection),new UIPropertyMetadata(initial,PixelShaderConstantCallback(index)));}
        internal Matrix3D NativeForward{get;private set;}
        internal bool NativeMasked{get{return masked!=null && PixelShader==masked;}}
        internal bool Available {get{return program!=null;}}
        internal AccretionProjection(){PixelShader=program;UpdateShaderValue(input);UpdateShaderValue(first);UpdateShaderValue(second);UpdateShaderValue(divisor);}
        internal void SetInverse(Matrix3D m){
            var forward=m;forward.Invert();NativeForward=forward;
            SetValue(first,new Point3D(m.M11,m.M21,0));SetValue(second,new Point3D(m.M12,m.M22,0));
            SetValue(divisor,new Point3D(m.M14,m.M24,1));
        }
    }
}
