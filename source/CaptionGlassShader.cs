using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace AstralSplit
{
    internal sealed class CaptionGlassShader : ShaderEffect
    {
        static readonly PixelShader shader=GlassEffect.LoadProgram("CaptionGlass");
        internal static bool Available { get { return shader!=null; } }
        static readonly DependencyProperty Atlas=RegisterPixelShaderSamplerProperty("Atlas",typeof(CaptionGlassShader),1,SamplingMode.Bilinear);
        static readonly DependencyProperty Shape=DependencyProperty.Register("Shape",typeof(Point),typeof(CaptionGlassShader),
            new UIPropertyMetadata(new Point(12,12),PixelShaderConstantCallback(0)));
        static readonly DependencyProperty Region=DependencyProperty.Register("Region",typeof(Point4D),typeof(CaptionGlassShader),
            new UIPropertyMetadata(new Point4D(0,0,1,1),PixelShaderConstantCallback(1)));
        internal CaptionGlassShader(){PixelShader=shader;UpdateShaderValue(Atlas);UpdateShaderValue(Shape);UpdateShaderValue(Region);}
        internal void Update(Brush atlas,Rect area,Size world,Size face){
            var uv=new Point4D(area.X/world.Width,area.Y/world.Height,area.Width/world.Width,area.Height/world.Height);
            var shape=new Point(face.Width,face.Height);
            if(atlas!=(Brush)GetValue(Atlas))SetValue(Atlas,atlas);
            if(shape!=(Point)GetValue(Shape))SetValue(Shape,shape);
            if(uv!=(Point4D)GetValue(Region))SetValue(Region,uv);
        }
    }
}
