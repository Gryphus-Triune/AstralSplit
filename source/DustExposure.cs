using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    // The projection never changes. Only two wrapped rows of orbital exposures
    // need to cross the dispatcher/compositor boundary as the disk accumulates.
    internal sealed class DustExposure : ShaderEffect
    {
        const int Sectors=40,Stride=Sectors+2;
        static readonly PixelShader program=GlassEffect.LoadProgram("DustExposure");
        static readonly DependencyProperty input=RegisterPixelShaderSamplerProperty("Input",typeof(DustExposure),0,SamplingMode.NearestNeighbor);
        static readonly DependencyProperty sectors=RegisterPixelShaderSamplerProperty("Sectors",typeof(DustExposure),1,SamplingMode.Bilinear);
        static readonly DependencyProperty rim=RegisterPixelShaderSamplerProperty("Rim",typeof(DustExposure),2,SamplingMode.NearestNeighbor);
        readonly int[] values=new int[2*Stride];
        readonly WriteableBitmap levels=new WriteableBitmap(Stride,2,96,96,PixelFormats.Pbgra32,null);
        internal readonly Brush Mask;

        internal static DustExposure Create(int[] plane,int[] curve,int w,int h,Rect area)
        {
            if(program==null || (RenderCapability.Tier>>16)<2 ||
                RenderOptions.ProcessRenderMode==System.Windows.Interop.RenderMode.SoftwareOnly)return null;
            return new DustExposure(plane,curve,w,h,area);
        }
        static ImageBrush Map(int[] pixels,int w,int h){
            var image=BitmapSource.Create(w,h,96,96,PixelFormats.Pbgra32,null,pixels,w*4);
            image.Freeze();var brush=new ImageBrush(image);brush.Freeze();return brush;
        }
        DustExposure(int[] plane,int[] curve,int w,int h,Rect area){
            PixelShader=program;SetValue(input,Map(plane,w,h));UpdateShaderValue(input);
            SetValue(sectors,new ImageBrush(levels));SetValue(rim,Map(curve,w,h));
            UpdateShaderValue(sectors);UpdateShaderValue(rim);
            var view=new DrawingVisual{Effect=this};
            using(var dc=view.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(0,0,w,h));
            var target=new ContainerVisual{CacheMode=new BitmapCache(1)};target.Children.Add(view);
            var cached=new BitmapCacheBrush(target){AutoLayoutContent=false,BitmapCache=null};
            var bounds=new Rect(0,0,w,h);
            Mask=new DrawingBrush(new GeometryDrawing(cached,null,new RectangleGeometry(bounds))){
                ViewboxUnits=BrushMappingMode.Absolute,Viewbox=bounds,ViewportUnits=BrushMappingMode.Absolute,
                Viewport=area,Stretch=Stretch.Fill};
        }
        // ShaderEffect is a Freezable. Its projection visual and animated strip belong to one dispatcher.
        protected override Freezable CreateInstanceCore(){return new DustExposure();}
        DustExposure(){}
        internal void Update(double[] field){
            bool changed=false;
            for(int row=0;row<2;row++){
                int start=row*Stride;
                for(int col=0;col<Sectors;col++){
                    int alpha=(int)(255*UiMotion.Unit(field[row*Sectors+col])+.5);
                    int ink=unchecked(alpha*0x01010101);
                    if(values[start+col+1]!=ink){changed=true;values[start+col+1]=ink;}
                }
                values[start]=values[start+Sectors];values[start+Stride-1]=values[start+1];
            }
            if(changed)levels.WritePixels(new Int32Rect(0,0,Stride,2),values,Stride*4,0);
        }
    }
}
