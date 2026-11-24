using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace AstralSplit
{
    internal sealed class OpticalSurface : ShaderEffect
    {
        static readonly DependencyProperty input=RegisterPixelShaderSamplerProperty("Input",typeof(OpticalSurface),0,SamplingMode.Bilinear);
        internal static readonly DependencyProperty Shape=Param("Shape",new Point3D(),0), Focus=Param("Focus",new Point(),1),
            Wave=Param("Wave",new Point3D(),3);
        static readonly PixelShader bevel=GlassEffect.LoadProgram("BevelField");
        static DependencyProperty Param(string name,object value,int register){return DependencyProperty.Register(name,value.GetType(),
            typeof(OpticalSurface),new UIPropertyMetadata(value,PixelShaderConstantCallback(register)));}
        public OpticalSurface(){
            PixelShader=bevel;
            foreach(var p in new[]{input,Shape,Focus,Wave})UpdateShaderValue(p);
        }
    }

    public sealed partial class GateGlassPanel
    {
        readonly Rectangle waveFace=new Rectangle{IsHitTestVisible=false,Visibility=Visibility.Hidden,RadiusX=14,RadiusY=14};
        readonly VisualBrush waveSource=new VisualBrush{AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        OpticalSurface waveCoat;
        StarfieldLayer sharedWave;
        static readonly bool share=Environment.GetEnvironmentVariable("ASTRALSPLIT_WAVE_CACHE")=="1";
        void ReleaseWave(){if(sharedWave!=null){sharedWave.ShareWaveSource(false);sharedWave=null;}Unloaded-=OnWaveUnloaded;}
        void OnWaveUnloaded(object sender,RoutedEventArgs args){ClearWave();}
        void ClearWave(){
            ReleaseWave();
            if(waveSource.Visual!=null){waveSource.Visual=null;waveFace.Visibility=Visibility.Hidden;}
        }
        void PaintWave(StarfieldLayer sky,Point focus,double radius,double strength){
            double w=ActualWidth,h=ActualHeight;
            double dx=Math.Max(Math.Max(-focus.X,focus.X-w),0),dy=Math.Max(Math.Max(-focus.Y,focus.Y-h),0);
            double farX=Math.Max(Math.Abs(focus.X),Math.Abs(w-focus.X)),farY=Math.Max(Math.Abs(focus.Y),Math.Abs(h-focus.Y));
            if(strength<.005 || radius+90<Math.Sqrt(dx*dx+dy*dy) || radius-90>Math.Sqrt(farX*farX+farY*farY)){ClearWave();return;}
            if(waveCoat==null){
                waveCoat=new OpticalSurface();waveFace.Effect=waveCoat;waveFace.Fill=waveSource;overlay.Children.Insert(0,waveFace);
            }
            if(share && sharedWave!=sky){ReleaseWave();sharedWave=sky;sky.ShareWaveSource(true);Unloaded+=OnWaveUnloaded;}
            if(waveSource.Visual!=sky.TransmittedSky)waveSource.Visual=sky.TransmittedSky;
            waveFace.Visibility=Visibility.Visible;
            var view=new Rect(TranslatePoint(new Point(),sky),RenderSize);if(waveSource.Viewbox!=view)waveSource.Viewbox=view;
            if(waveFace.Width!=w || waveFace.Height!=h){
                waveFace.Width=w;waveFace.Height=h;
                var clip=new RectangleGeometry(new Rect(0,0,w,h),14,14);clip.Freeze();waveFace.Clip=clip;
            }
            waveCoat.SetValue(OpticalSurface.Shape,new Point3D(w,h,2));waveCoat.SetValue(OpticalSurface.Focus,focus);
            waveCoat.SetValue(OpticalSurface.Wave,new Point3D(radius,strength,34));
        }
        internal void Receive(StarfieldLayer sky,Point focus,double radius,double strength){
            if(strength<.005 || !IsLoaded || !IsVisible || !LiquidLens.GetGlassEnabled(this) ||
                !GlassEffect.Hardware3(this) || ActualWidth<32){ClearWave();return;}
            PaintWave(sky,focus,radius,strength);
        }
    }
}
