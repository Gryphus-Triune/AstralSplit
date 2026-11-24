using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    internal sealed class NebulaSky
    {
        const int W=384,H=144;
        internal readonly DrawingGroup Art=new DrawingGroup();
        readonly MatrixTransform placement=new MatrixTransform();
        readonly DrawingVisual tile=new DrawingVisual();
        readonly ContainerVisual material=new ContainerVisual();
        static readonly Lazy<BitmapSource> atlas=new Lazy<BitmapSource>(()=>StellarSkyArt.ReadImage("corona-volume.png"));
        readonly Clock[] winds=new Clock[2];
        VolumeShader effect;
        bool initialized,hardware,running;
        double target=.12;
        int? rate;
        internal NebulaSky(){
            Art.Transform=placement;material.Children.Add(tile);
            var feather=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1)};
            feather.GradientStops.Add(new GradientStop(Colors.Transparent,0));
            feather.GradientStops.Add(new GradientStop(Colors.White,.14));
            feather.GradientStops.Add(new GradientStop(Colors.White,.68));
            feather.GradientStops.Add(new GradientStop(Colors.Transparent,1));
            feather.Freeze();Art.OpacityMask=feather;
        }

        internal void Layout(double w,double header,bool contrast){
            Art.Opacity=contrast?0:1;
            if(contrast || w<1 || header<1)return;
            bool accelerated=(RenderCapability.Tier>>16)>=2 && RenderOptions.ProcessRenderMode!=System.Windows.Interop.RenderMode.SoftwareOnly &&
                VolumeShader.Program!=null;
            if(!initialized || hardware!=accelerated){
                ClearClocks();hardware=accelerated;initialized=true;
                var bitmap=atlas.Value;
                effect=hardware?new VolumeShader(bitmap):null;tile.Effect=effect;
                using(var dc=tile.RenderOpen()){
                    if(effect!=null)dc.DrawRectangle(Brushes.White,null,new Rect(0,0,W,H));
                    else {
                        // The same depth integrals at the central camera, for software-only rendering.
                        for(int i=7;i>=0;i--){
                            var slice=new CroppedBitmap(bitmap,new Int32Rect((i%4)*W,(i/4)*H,W,H));slice.Freeze();
                            dc.DrawImage(slice,new Rect(0,0,W,H));
                        }
                    }
                }
                // The shader must be inside the cached subtree. Caching its own
                // white input surface bypasses the volume when WPF reuses it.
                material.CacheMode=new BitmapCache(Environment.GetEnvironmentVariable("ASTRALSPLIT_HALF_NEBULA")=="1"?.5:1);
                var sample=new BitmapCacheBrush(material){AutoLayoutContent=false,BitmapCache=null};
                using(var dc=Art.Open())dc.DrawRectangle(sample,null,new Rect(0,0,W,H));
                if(effect!=null){effect.SetValue(VolumeShader.Energy,target);SetClocks();}
                tile.Opacity=hardware?1:.40+.28*target;
            }
            double height=Math.Min(header+24,Math.Max(136,.42*w));
            placement.Matrix=new Matrix(w/W,0,0,height/H,0,Math.Max(0,header-height)*.55);
        }

        internal void Aim(Point eye){if(effect!=null && (Point)effect.GetValue(VolumeShader.Eye)!=eye)effect.SetValue(VolumeShader.Eye,eye);}
        internal void Light(string state,bool animate){
            double value=state=="StatusOn"?1:state=="StatusBusy"?.66:state=="StatusWarning"?.78:.12;
            if(effect!=null && (target!=value || !animate)){
                double from=(double)effect.GetValue(VolumeShader.Energy);
                effect.SetValue(VolumeShader.Energy,value);
                effect.BeginAnimation(VolumeShader.Energy,animate && from!=value?UiMotion.Ease(from,value,760,rate):null);
            }
            if(initialized && !hardware)tile.Opacity=.40+.28*value;
            target=value;
        }
        internal void Activity(bool active,int? refresh){
            if(running==active && refresh==rate)return;
            bool rebuild=refresh!=rate;running=active;rate=refresh;
            if(effect==null)return;
            if(rebuild || winds[0]==null){SetClocks();return;}
            foreach(var clock in winds){if(active)clock.Controller.Resume();else clock.Controller.Pause();}
        }
        void SetClocks(){
            var elapsed=new[]{winds[0]?.CurrentTime,winds[1]?.CurrentTime};
            ClearClocks();
            if(effect==null || !running)return;
            for(int i=0;i<2;i++){
                var prop=i==0?VolumeShader.Drift:VolumeShader.Swell;
                double seconds=i==0?19:26;
                var cycle=UiMotion.AtRefresh(new ParallelTimeline{Duration=Duration.Forever},rate);
                cycle.Children.Add(new DoubleAnimation(-1,1,TimeSpan.FromSeconds(seconds)){
                    AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}});
                var clock=(ClockGroup)cycle.CreateClock(true);winds[i]=clock;
                effect.ApplyAnimationClock(prop,(AnimationClock)clock.Children[0]);
                clock.Controller.SeekAlignedToLastTick(elapsed[i]??TimeSpan.FromSeconds(seconds*(i==0?.32:.68)),TimeSeekOrigin.BeginTime);
            }
        }
        void ClearClocks(){
            for(int i=0;i<winds.Length;i++){
                if(winds[i]==null)continue;
                winds[i].Controller.Remove();winds[i]=null;
                effect?.BeginAnimation(i==0?VolumeShader.Drift:VolumeShader.Swell,null);
            }
        }
        internal static BitmapSource Density { get { return atlas.Value; } }
        internal bool Sample(out Rect area,out Point eye,out double energy,out double drift,out double swell,out bool moving){
            area=placement.TransformBounds(new Rect(0,0,W,H));eye=new Point();energy=target;drift=swell=0;moving=false;
            if(!hardware || effect==null || Art.Opacity<=0 || area.IsEmpty)return false;
            eye=(Point)effect.GetValue(VolumeShader.Eye);energy=(double)effect.GetValue(VolumeShader.Energy);
            moving=running && winds[0]!=null && winds[1]!=null;
            if(moving){drift=(winds[0].CurrentTime??TimeSpan.Zero).TotalSeconds;swell=(winds[1].CurrentTime??TimeSpan.Zero).TotalSeconds;}
            else {drift=19*Math.Acos(-Math.Max(-1,Math.Min(1,(double)effect.GetValue(VolumeShader.Drift))))/Math.PI;
                swell=26*Math.Acos(-Math.Max(-1,Math.Min(1,(double)effect.GetValue(VolumeShader.Swell))))/Math.PI;}
            return true;
        }
        internal void Release(){ClearClocks();tile.Effect=null;Art.Children.Clear();}

        sealed class VolumeShader : ShaderEffect
        {
            internal static readonly PixelShader Program=GlassEffect.LoadProgram("Volume");
            static readonly DependencyProperty atlasProperty=RegisterPixelShaderSamplerProperty("Atlas",typeof(VolumeShader),1,SamplingMode.Bilinear);
            internal static readonly DependencyProperty Eye=Uniform("Eye",new Point(),0),Drift=Uniform("Drift",0d,1),
                Swell=Uniform("Swell",0d,2),Energy=Uniform("Energy",.12d,3);
            static DependencyProperty Uniform(string name,object value,int register){
                return DependencyProperty.Register(name,value.GetType(),typeof(VolumeShader),new UIPropertyMetadata(value,PixelShaderConstantCallback(register)));
            }
            public VolumeShader(){}
            internal VolumeShader(BitmapSource bitmap){
                PixelShader=Program;var brush=new ImageBrush(bitmap);brush.Freeze();
                SetValue(atlasProperty,brush);UpdateShaderValue(atlasProperty);
                UpdateShaderValue(Eye);UpdateShaderValue(Drift);UpdateShaderValue(Swell);UpdateShaderValue(Energy);
            }
        }
    }
}
