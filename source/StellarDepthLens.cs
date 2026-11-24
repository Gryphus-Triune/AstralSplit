using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        readonly DrawingGroup crossImages=new DrawingGroup{Opacity=0};
        readonly DrawingGroup standbyCrossImages=new DrawingGroup{Opacity=0};
        readonly DrawingGroup standbyCrossLayer=new DrawingGroup{Opacity=0};
        readonly ScaleTransform crossExpansion=new ScaleTransform(1,1);
        readonly ScaleTransform standbyCrossExpansion=new ScaleTransform();
        readonly Dictionary<object,Vector> previousSources=new Dictionary<object,Vector>();
        readonly HashSet<object> insideCaustic=new HashSet<object>();
        double lastCross=double.NegativeInfinity;
        double LensEventScale {get{return CoreScale*(Live?holeScale.ScaleX:StandbyOpticalScale);}}
        bool LensEventReady {get{return CanMoveMatter && !formationRunning && !evaporationRunning &&
            (Live?holeScale.ScaleX>.55:seedStar.Opacity>.85 && gestureMass<.08);}}
        void ResetCaustic(bool finishStandbyFlash=false){
            previousSources.Clear();insideCaustic.Clear();lastCross=double.NegativeInfinity;
            crossImages.BeginAnimation(DrawingGroup.OpacityProperty,null);crossImages.Opacity=0;
            if(!finishStandbyFlash){
                standbyCrossImages.BeginAnimation(DrawingGroup.OpacityProperty,null);standbyCrossImages.Opacity=0;
            }
        }
        void ForgetSource(object key){previousSources.Remove(key);insideCaustic.Remove(key);}
        void TraceSource(object key,Point world,Color hue){
            if(!LensEventReady)return;
            var v=(world-ProjectedGate)/Math.Max(.01,LensEventScale);double r=v.Length;
            bool tracked=previousSources.TryGetValue(key,out var previous);
            previousSources[key]=v;
            if(insideCaustic.Contains(key)){
                if(r>5.5 && Math.Abs(r-35)>4)insideCaustic.Remove(key);
                return;
            }
            if(!tracked || (v-previous).LengthSquared<.0002)return;
            var move=v-previous;
            double near=(previous+move*UiMotion.Unit(-Vector.Multiply(previous,move)/move.LengthSquared)).Length;
            bool crossed=near<=2.9 || (near<=36.2 && Math.Max(previous.Length,r)>=33.8);
            if(!crossed)return;
            insideCaustic.Add(key);FlashCross(v,hue);
        }
        void FlashCross(Vector direction,Color hue){
            if(!LensEventReady)return;
            double now=flightClock.Elapsed.TotalSeconds;
            if(now-lastCross<.86)return;
            lastCross=now;
            var art=Live?crossImages:standbyCrossImages;
            var expansion=Live?crossExpansion:standbyCrossExpansion;
            double size=Live?1:StandbyOpticalScale;
            double angle=Math.Atan2(direction.Y,direction.X);
            var glow=Glow(A(hue,.95));var edge=Stroke(HeatMix(hue,Colors.White,.68),.82,.55);
            using(var d=art.Open()){
                for(int j=0;j<4;j++){
                    double a=angle+j*Math.PI*.5,r=j%2==0?34.5:32.8;
                    var ray=new Vector(Math.Cos(a),Math.Sin(a));var tangent=new Vector(-ray.Y,ray.X);
                    var p=new Point(r*ray.X,r*ray.Y);
                    d.PushTransform(new MatrixTransform(tangent.X,tangent.Y,ray.X,ray.Y,p.X,p.Y));
                    d.DrawEllipse(glow,null,new Point(),8,3.8);d.DrawEllipse(Brushes.White,null,new Point(),1.1,.62);
                    var arc=new StreamGeometry();using(var path=arc.Open()){
                        path.BeginFigure(new Point(-9,-1.4),false,false);
                        path.QuadraticBezierTo(new Point(0,1.5),new Point(9,-1.4),true,true);
                    }
                    arc.Freeze();d.DrawGeometry(null,edge,arc);d.Pop();
                }
            }
            art.Transform=expansion;
            var flash=new DoubleAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            flash.KeyFrames.Add(new SplineDoubleKeyFrame(size,TimeSpan.FromMilliseconds(105),new KeySpline(.13,0,.18,1)));
            flash.KeyFrames.Add(new SplineDoubleKeyFrame(.52*size,TimeSpan.FromMilliseconds(265),new KeySpline(.24,0,.5,1)));
            flash.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(850),new KeySpline(.22,0,.40,1)));
            flash.FillBehavior=FillBehavior.Stop;
            art.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(flash,refreshRate));
            expansion.ScaleX=expansion.ScaleY=size;
            var bloom=UiMotion.Ease(.82*size,size,210,refreshRate);
            expansion.BeginAnimation(ScaleTransform.ScaleXProperty,bloom);
            expansion.BeginAnimation(ScaleTransform.ScaleYProperty,bloom);
        }
        void TrackBackgroundSources(){
            if(!LensEventReady)return;
            TraceSource(-1,beaconPlacement.Transform(new Point()),Color.FromRgb(178,216,255));
            if(atlas==null)return;
            var drift=new Vector(close.Slide.X,close.Slide.Y);
            for(int i=0;i<atlas.Near.Count;i++){
                var star=atlas.Near[i];TraceSource(i,star.Point+drift,star.Color);
            }
        }
        void AlignBeacons()
        {
            if(viewport.IsEmpty)return;
            var gate=ProjectedGate;double s=SurroundingStarScale;
            // Keep the resting source outside alignment so its halo reads as
            // a short arc. Pointer parallax can still carry it across the aim.
            double bias=standbyCrossLayer.Opacity*CoreScale;
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
            standbyCrossLayer.Opacity=opticsReady?seedStar.Opacity:0;
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
