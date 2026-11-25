using System;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        const double StandbyOpticalScale=.25;
        const double StandbyLensRadius=LensRadius*StandbyOpticalScale;
        const double StandbyLensMass=StandbyOpticalScale*StandbyOpticalScale;
        readonly struct LensState
        {
            internal readonly double Mass, Horizon, Starlight;
            internal LensState(double mass,double horizon,double starlight)
            {
                Mass=mass;Horizon=horizon;Starlight=starlight;
            }
        }
        static readonly LensState standbyLens=new LensState(StandbyLensMass,0,1);
        LensState coreLens=standbyLens;
        readonly ContainerVisual starDepth=new ContainerVisual();
        readonly DrawingVisual lensPatch=new DrawingVisual(),sourceBeacons=new DrawingVisual();
        readonly DrawingGroup backgroundBeacons=new DrawingGroup();
        readonly DrawingGroup lensedFlyers=new DrawingGroup();
        readonly MatrixTransform beaconPlacement=new MatrixTransform();
        double standbyBeaconWeight;
        Rect patchBounds=Rect.Empty,starWindow=Rect.Empty;
        readonly GateOptics starOptics=GateOptics.Create();

        void InitializeStarLens()
        {
            backgroundBeacons.Transform=beaconPlacement;
            using(var dc=backgroundBeacons.Open()){
                DrawRearStar(dc,new Point(),Color.FromRgb(178,216,255),1.35);
                DrawRearStar(dc,new Point(-11,-7.5),Color.FromRgb(221,229,255),1.12);
                DrawRearStar(dc,new Point(12,10),Color.FromRgb(255,202,150),1.32);
                DrawRearStar(dc,new Point(-20,16),Color.FromRgb(150,203,254),1.02);
            }
            using(var dc=sourceBeacons.RenderOpen())dc.DrawDrawing(backgroundBeacons);
            starDepth.Children.Add(distant);starDepth.Children.Add(middle);starDepth.Children.Add(close);starDepth.Children.Add(sourceBeacons);
            starDepth.Children.Add(clickStars);
            lensPatch.Effect=starOptics;
            InitializeLensPatch();
        }
        static void DrawRearStar(DrawingContext dc,Point p,Color hue,double radius)
        {
            dc.DrawEllipse(Glow(A(hue,.68)),null,p,radius*3.8,radius*3.8);
            dc.DrawEllipse(Ink(hue,.90),null,p,radius,radius);
        }

        void AlignBeacons()
        {
            if(viewport.IsEmpty)return;
            var gate=ProjectedGate;double s=SurroundingStarScale;
            // Keep the resting source outside alignment so its halo reads as
            // a short arc. Pointer parallax can still carry it across the aim.
            double bias=standbyBeaconWeight*CoreScale;
            beaconPlacement.Matrix=new Matrix(s,0,0,s,gate.X+position.X*7*s+2.8*bias,gate.Y+position.Y*5*s-2.1*bias);
        }

        void SetCoreLens(LensState lens)
        {
            coreLens=lens;
            double radius=Math.Sqrt(lens.Mass);
            coronaScale.ScaleX=coronaScale.ScaleY=radius;
            coronaShell.Opacity=lens.Horizon;
            starOptics?.SetLensMass(lens.Mass);
            starOptics?.SetLensMix(lens.Horizon);
            LayoutStarLens();
        }

        void LayoutStarLens()
        {
            starOptics?.Device(this);
            bool present=!disposed && !viewport.IsEmpty && !highContrast;
            sourceBeacons.Opacity=present?1:0;
            bool opticsReady=present && starOptics!=null && CoreScale>0;
            bool strong=opticsReady && (Live || coreLens.Horizon>0);
            double strength=opticsReady && coreLens.Horizon<1?coreLens.Starlight:0;
            standbyBeaconWeight=opticsReady?seedStar.Opacity:0;
            if(present)AlignBeacons();
            bool standby=!strong && strength>0;
            double aperture=strong?LensRadius:StandbyLensRadius;
            // Keep the backing rectangle stable during the morph. Both images
            // use the same expanding radius inside that rectangle.
            double radius=LensRadius*Math.Max(.0001,coronaScale.ScaleX);
            starOptics?.SetStandby(new Point(strength,2*aperture/radius),standby);
            var box=Rect.Empty;
            if(strong || standby){
                var gate=ProjectedGate;double r=aperture*CoreScale;
                box=new Rect(gate.X-r,gate.Y-r,2*r,2*r);
            }
            var window=new Rect(RenderSize);
            Rect grid=Rect.Empty;
            bool tilesOk=strong && TryGetPerimeter(window,box,out grid);
            if(box==patchBounds && starWindow==window && integerPatch==grid)return;
            integerPatch=grid;
            patchBounds=box;starWindow=window;
            if(!box.IsEmpty){
                lensWindow.Rect=window;lensBox.Rect=box;
                skySample.Viewbox=flyerSample.Viewbox=box;
            }
            if(SwitchWindowLens(window,strong?box:Rect.Empty))return;
            if(tilesOk)SetGutters(window,grid);
            localStraight.Clip=tilesOk?perimeter:null;
            // The stellar images are composited over the sky with an intact core.
            straightSky.Clip=strong?outsideLens:null;
            straightSky.Opacity=1;straightTiles.Opacity=tilesOk?1:0;lensPatch.Opacity=box.IsEmpty?0:1;
        }
    }
}
