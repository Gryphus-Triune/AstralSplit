using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AstralSplit
{
    sealed class GateOptics : ShaderEffect
    {
        static readonly PixelShader stars=GlassEffect.LoadProgram("StellarLens"), full=GlassEffect.LoadProgram("StellarFull");
        static readonly PixelShader oldStars=GlassEffect.LoadProgram("StellarLens2"), oldFull=GlassEffect.LoadProgram("StellarFull2");
        static readonly PixelShader standby=GlassEffect.LoadProgram("StellarStandby");
        bool spectral;
        internal GateOptics FullScene;
        static readonly PixelShader windowProgram=GlassEffect.LoadProgram("SceneLens");
        static readonly DependencyProperty region=DependencyProperty.Register("Region",typeof(System.Windows.Media.Media3D.Point4D),typeof(GateOptics),
            new UIPropertyMetadata(new System.Windows.Media.Media3D.Point4D(0,0,1,1),PixelShaderConstantCallback(4)));
        bool windowMode;
        internal void PrepareWindow(Rect uv){
            if(FullScene==null){
                if(windowProgram==null)return;
                FullScene=new GateOptics(){windowMode=true,PixelShader=windowProgram};FullScene.UpdateShaderValue(region);
            }
            var value=new System.Windows.Media.Media3D.Point4D(uv.X,uv.Y,uv.Width,uv.Height);
            if((System.Windows.Media.Media3D.Point4D)FullScene.GetValue(region)!=value)FullScene.SetValue(region,value);
            FullScene.SetStandby(StandbyParameters,StandbyOnly);
            FullScene.SetLensMix(LensStrength);FullScene.SetLensMass(NativeMass);
        }
        internal void Device(Visual v){spectral=GlassEffect.Hardware3(v);SelectShader();}
        static readonly DependencyProperty input=RegisterPixelShaderSamplerProperty("Input",typeof(GateOptics),0,SamplingMode.Bilinear);
        static readonly DependencyProperty lensMix=DependencyProperty.Register("LensMix",typeof(double),typeof(GateOptics),
            new UIPropertyMetadata(1.0,PixelShaderConstantCallback(0)));
        static readonly DependencyProperty lensMass=DependencyProperty.Register("LensMass",typeof(double),typeof(GateOptics),
            new UIPropertyMetadata(1.0,PixelShaderConstantCallback(1)));
        static readonly DependencyProperty standbyParameters=DependencyProperty.Register("StandbyParameters",typeof(Point),typeof(GateOptics),
            new UIPropertyMetadata(new Point(0,2),PixelShaderConstantCallback(2)));
        internal static GateOptics Create(){return stars==null?null:new GateOptics();}
        GateOptics(){
            PixelShader=oldFull??full??stars;
            UpdateShaderValue(input);UpdateShaderValue(lensMix);UpdateShaderValue(lensMass);UpdateShaderValue(standbyParameters);
        }
        void SelectShader(){
            if(windowMode)return;
            var blend=spectral?stars:oldStars;
            bool stellarImage=StandbyOnly || LensStrength<=0 && StandbyParameters.X>0;
            var shader=stellarImage ? standby??blend : LensStrength>=1 ? (spectral?full:oldFull) : blend;
            if(shader==null)shader=stars;
            if(PixelShader!=shader)PixelShader=shader;
        }
        internal void SetLensMix(double amount){
            FullScene?.SetLensMix(amount);
            if((double)GetValue(lensMix)!=amount)SetValue(lensMix,amount);
            SelectShader();
        }
        internal void SetStandby(Point parameters,bool only){
            FullScene?.SetStandby(parameters,only);
            if(StandbyParameters==parameters && StandbyOnly==only)return;
            if(StandbyParameters!=parameters)SetValue(standbyParameters,parameters);
            StandbyOnly=only;
            SelectShader();
        }
        internal bool StandbyOnly {get;private set;}
        internal Point StandbyParameters {get{return (Point)GetValue(standbyParameters);}}
        internal double NativeMass{get{return (double)GetValue(lensMass);}}
        internal double LensStrength {get{return (double)GetValue(lensMix);}}
        internal void SetLensMass(double mass){FullScene?.SetLensMass(mass);if((double)GetValue(lensMass)!=mass)SetValue(lensMass,mass);}
    }

    public sealed partial class GateGlassPanel : Grid
    {
        readonly Canvas overlay=new Canvas{IsHitTestVisible=false};
        public GateGlassPanel()
        {
            Loaded+=(s,e)=>{if(!Children.Contains(overlay))Children.Add(overlay);};
            Unloaded+=(s,e)=>ClearWave();
        }
    }
}
