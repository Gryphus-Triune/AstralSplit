using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        // The same object occupies the convergence point before, during and after activation.
        // Redrawing the chart for new icons or traffic must not restart the reveal.
        readonly DrawingGroup coreFrame = new DrawingGroup(), coreObject = new DrawingGroup();
        readonly DrawingGroup seedStar = new DrawingGroup(), blackHole = new DrawingGroup();
        readonly DrawingGroup standbyBody = new DrawingGroup();
        readonly DrawingGroup standbyArt = new DrawingGroup { Opacity = 0 };
        readonly ScaleTransform standbyScale = new ScaleTransform();
        readonly Stopwatch standbyClock = new Stopwatch();
        double? prominenceFadeStart;
        StandbyProminence prominence;
        TimeSpan lastStandbyFrame = TimeSpan.MinValue;
        readonly DrawingGroup coreHalo = new DrawingGroup { Opacity = .60 };
        readonly ScaleTransform starScale = new ScaleTransform(), holeScale = new ScaleTransform();
        readonly MatrixTransform corePlacement = new MatrixTransform();
        readonly MatrixTransform matterToCore = new MatrixTransform();
        readonly DrawingGroup rearPlane = new DrawingGroup(), lensedFlow = new DrawingGroup(), nearPlane = new DrawingGroup();
        readonly RotateTransform outerOrbit = new RotateTransform(18), innerOrbit = new RotateTransform(139),
            photonOrbit = new RotateTransform(72), coronaOrbit=new RotateTransform(-24);
        bool orbitRunning;
        int? orbitRate;
        const double ShadowPlateWidth=174;
        // RenderBlackHoleArt.py bakes 108 source pixels per unit in its 840-pixel-wide plate.
        const double ShadowArtUnit=108*ShadowPlateWidth/840;
        static readonly Lazy<BitmapSource> shadowTexture =
            new Lazy<BitmapSource>(() => StellarSkyArt.ReadImage("black-hole.png"));
        static readonly Lazy<BitmapSource> frontTexture =
            new Lazy<BitmapSource>(() => StellarSkyArt.ReadImage("front-disk.png"));
        static readonly Lazy<BitmapSource> plasmaTexture =
            new Lazy<BitmapSource>(() => StellarSkyArt.ReadImage("accretion-flow.png"));
        static readonly Lazy<BitmapSource> photonTexture =
            new Lazy<BitmapSource>(() => StellarSkyArt.ReadImage("photon-flow.png"));
        static readonly Lazy<BitmapSource> lensTexture =
            new Lazy<BitmapSource>(() => StellarSkyArt.ReadImage("lens-arcs.png"));
        readonly DrawingGroup corona=new DrawingGroup(),coronaShell=new DrawingGroup(),jetSource=new DrawingGroup();
        readonly ScaleTransform coronaScale=new ScaleTransform();
        readonly DrawingGroup farDisk=new DrawingGroup(),nearDisk=new DrawingGroup(),opticalArc=new DrawingGroup();
        readonly DrawingGroup backRing=new DrawingGroup(), frontRing=new DrawingGroup();
        Color coreTint;
        static readonly DependencyProperty CoreRevealProperty=DependencyProperty.Register(
            "CoreReveal",typeof(double),typeof(StarfieldLayer),new PropertyMetadata(0d,
                (d,e)=>((StarfieldLayer)d).PaintCoreReveal((double)e.NewValue)));
        int revealRevision;
        double lensRevealFrom;
        LensState lensAtReveal;
        internal double ChartReveal { get; private set; }
        internal event Action ChartRevealChanged;

        void InitializeCore()
        {
            seedStar.Transform = starScale;
            standbyBody.Transform = standbyScale;
            standbyBody.Children.Add(seedStar);
            // The accepted study uses two image pixels per DIP at the full-size core.
            double sampleToLocal = 1 / (2 * CoreMagnification * MaximumCoreFit);
            var artScale = new ScaleTransform(sampleToLocal,sampleToLocal);
            artScale.Freeze();
            standbyArt.Transform = artScale;
            standbyBody.Children.Add(standbyArt);
            RenderOptions.SetBitmapScalingMode(blackHole, BitmapScalingMode.HighQuality);
            coreObject.Transform = corePlacement;
            coreObject.Children.Add(standbyBody);
            coreObject.Children.Add(blackHole);
            coreFrame.Children.Add(coreObject);
            InitializeAccretion();
            InitializeDiskTraffic();
            using(var d=coreHalo.Open())
                d.DrawEllipse(Glow(A(Color.FromRgb(255,203,139),.46)),null,new Point(),86,42);
            corona.Transform=coronaOrbit;
            using(var dc=corona.Open())dc.DrawImage(lensTexture.Value,new Rect(-59,-59,118,118));
            var texture=shadowTexture.Value;
            double height=ShadowPlateWidth*texture.PixelHeight/texture.PixelWidth;
            var plate=new Rect(-ShadowPlateWidth/2,-height/2,ShadowPlateWidth,height);
            coronaShell.Transform=coronaScale;coronaShell.Children.Add(corona);jetSource.Children.Add(bipolarJets);
            using(var d=farDisk.Open()){
                d.DrawDrawing(rearPlane);d.DrawDrawing(magneticRear);
                d.PushTransform(matterToCore);d.DrawDrawing(riverBack);d.Pop();
            }
            using(var d=opticalArc.Open()){
                d.DrawImage(StellarSkyArt.ReadImage("ring-emission.png"),plate);d.DrawDrawing(lensedFlow);
            }
            using(var d=nearDisk.Open()){
                d.DrawImage(frontTexture.Value,plate);d.DrawDrawing(nearPlane);d.DrawDrawing(magneticNear);
                d.DrawDrawing(uploadLimb);d.DrawDrawing(downloadLimb);d.DrawDrawing(diskFlare);
                d.PushTransform(matterToCore);d.DrawDrawing(riverFront);d.Pop();
            }
            using(var d=backRing.Open()){
                d.DrawDrawing(photonAttitude.ProjectDepositPlate(opticalArc));d.DrawDrawing(diskAttitude.ProjectDepositPlate(farDisk));
            }
            using(var d=frontRing.Open()){
                d.DrawDrawing(diskAttitude.ProjectDepositPlate(nearDisk));d.DrawDrawing(jetAttitude.ProjectDepositPlate(jetSource));
            }
            var farMatter=new DrawingGroup{Transform=matterToCore};
            farMatter.Children.Add(rearTransit);
            using(var d=blackHole.Open()){
                d.PushTransform(holeScale);
                d.DrawDrawing(coreHalo);d.DrawDrawing(afterglow);
                d.Pop();d.DrawDrawing(dustBehind);d.DrawDrawing(coronaShell);d.DrawDrawing(backRing);d.DrawDrawing(rearDiskAfterimage);
                d.DrawDrawing(observingRibbons.Rear);d.DrawDrawing(farMatter);
                d.PushTransform(holeScale);d.DrawImage(texture,plate);d.Pop();
                d.DrawDrawing(frontRing);d.DrawDrawing(observingRibbons.Front);d.DrawDrawing(frontDiskAfterimage);d.DrawDrawing(dustAhead);
            }
            InitializeFormation();
            InitializeEvaporation();
            PaintCore();
            FormCore(false);
        }

        void PaintCore()
        {
            Color tint = Accent;
            if (coreTint == tint) return;
            coreTint = tint;
            using (var d = seedStar.Open()) {
                d.PushTransform(new ScaleTransform(.75/CoreMagnification,.75/CoreMagnification));
                d.DrawEllipse(Glow(A(tint, .30)), null, new Point(), 39, 33);
                Light(d, new Point(), tint, 4.2, 1);
                var spike = Geometry.Parse("M-16,0 Q-2,-.8 0,-13 Q.7,-1.2 15,0 Q1,.8 0,12 Q-.8,1 -16,0Z");
                spike.Freeze();
                d.DrawGeometry(Ink(Colors.White, .93), null, spike);
                d.Pop();
            }
        }

        static DrawingGroup ProjectOrbit(BitmapSource image,RotateTransform rotation,double radius,
            double flattening,double height)
        {
            var disk=new DrawingGroup {Transform=rotation};
            using(var dc=disk.Open())dc.DrawImage(image,new Rect(-radius,-radius,2*radius,2*radius));
            var perspective=new MatrixTransform(1,0,0,flattening,0,height);perspective.Freeze();
            var projected=new DrawingGroup {Transform=perspective};
            projected.Children.Add(disk);
            return projected;
        }

        void InitializeAccretion()
        {
            var tilt=new RotateTransform(-11);tilt.Freeze();
            rearPlane.Transform=lensedFlow.Transform=nearPlane.Transform=tilt;
            // Only the material turns. Lens and shadow stay fixed in the observer's frame.
            var photon=ProjectOrbit(photonTexture.Value,photonOrbit,33.2,.95,-.5);
            var exposure=new LinearGradientBrush{
                MappingMode=BrushMappingMode.Absolute,StartPoint=new Point(0,-22),EndPoint=new Point(0,22)};
            exposure.GradientStops.Add(new GradientStop(Colors.White,0));
            exposure.GradientStops.Add(new GradientStop(Color.FromArgb(172,255,255,255),1));
            exposure.Freeze();
            lensedFlow.OpacityMask=exposure;
            // Inside the solid part of the baked shadow even the inner optical image is hidden.
            var shadow=Geometry.Parse("M-21.9,0 A21.9,21.9 0 0 1 21.9,0 A21.9,18.4 0 0 1 -21.9,0 Z");
            var rim=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(new Rect(-82,-40,164,80)),shadow);
            rim.Freeze();rearPlane.ClipGeometry=lensedFlow.ClipGeometry=rim;lensedFlow.Children.Add(photon);
            AddDiskOrbit(outerOrbit,AccretionPath.OuterPlate,AccretionPath.OuterFlattening,AccretionPath.OuterMidline);
            AddDiskOrbit(innerOrbit,AccretionPath.InnerPlate,AccretionPath.InnerFlattening,AccretionPath.InnerMidline);
        }

        void AddDiskOrbit(RotateTransform rotation,double radius,double flattening,double midline)
        {
            // Split the projected equator in camera space. Both halves use the
            // same rotating material and clock, so no seam travels with the texture.
            var orbit=ProjectOrbit(plasmaTexture.Value,rotation,radius,flattening,midline);
            double h=radius*flattening+1;
            var upper=new RectangleGeometry(new Rect(-radius,midline-h,2*radius,h));upper.Freeze();
            var lower=new RectangleGeometry(new Rect(-radius,midline,2*radius,h));lower.Freeze();
            var far=new DrawingGroup{ClipGeometry=upper};far.Children.Add(orbit);rearPlane.Children.Add(far);
            var near=new DrawingGroup{ClipGeometry=lower};near.Children.Add(orbit);nearPlane.Children.Add(near);
        }

        internal const double CoreMagnification=2, OuterRingRadius=52;
        const double MaximumCoreFit=1.62;
        double CoreScale { get; set; }
        internal static double FitCore(double width,double referenceHeight)
        {
            if(width<160 || referenceHeight<30)return 0;
            double caption=referenceHeight>72?16:0;
            return CoreMagnification*Math.Min(MaximumCoreFit,Math.Min(width/456,(referenceHeight-caption-4)/84));
        }

        void PositionCore()
        {
            double scale = CoreScale;
            var p = GateCenter;
            corePlacement.Matrix = new Matrix(scale, 0, 0, scale, p.X, p.Y);
            if(scale>0)matterToCore.Matrix=new Matrix(1/scale,0,0,1/scale,-p.X/scale,-p.Y/scale);
        }

        void PlaceCore()
        {
            coreFrame.Opacity = 1;
            PositionCore();
            PaintCore();
            LayoutStarLens();
        }

        void FormCore(bool animate)
        {
            bool morph = animate && running && !highContrast && !viewport.IsEmpty && SystemParameters.ClientAreaAnimation;
            double from=(double)GetValue(CoreRevealProperty),target=Live?1:0;
            int revision=++revealRevision;
            if (morph && !Live && from>0 && blackHole.Opacity>.001) {
                SetValue(CoreRevealProperty,from);
                BeginAnimation(CoreRevealProperty,null);
                StartEvaporation();
                resumingEvaporation=false;
                SyncCoreOrbit();
                return;
            }
            resumingEvaporation=morph && Live && evaporationRunning;
            if(resumingEvaporation){
                CaptureReconnection();
                from=resumeMass;
            }
            lensRevealFrom=from;
            lensAtReveal=coreLens;
            StopEvaporation(resumingEvaporation,!morph);
            SetGateCaption(Live,morph && resumingEvaporation);
            if(!morph || from==target){
                SetValue(CoreRevealProperty,target);
                BeginAnimation(CoreRevealProperty,null);
                PaintCoreReveal(target);
            }else{
                SetValue(CoreRevealProperty,from);
                var motion=UiMotion.Ease(from,target,Live?560:310,refreshRate,.36,0,.23,1);
                motion.FillBehavior=FillBehavior.HoldEnd;
                motion.Completed+=(s,e)=>{
                    if(revision!=revealRevision)return;
                    SetValue(CoreRevealProperty,target);
                    BeginAnimation(CoreRevealProperty,null);
                };
                BeginAnimation(CoreRevealProperty,motion,HandoffBehavior.SnapshotAndReplace);
            }
            ChangeFormation(morph);
            SyncCoreOrbit();
            if(!morph){LayoutStarLens();}
        }

        void PaintCoreReveal(double progress)
        {
            if(evaporationRunning)return;
            double collapse=SmoothBand(0,.40,progress);
            starScale.ScaleX=starScale.ScaleY=1-.955*collapse;
            seedStar.Opacity=1-collapse;
            holeScale.ScaleX=holeScale.ScaleY=.045+.955*progress;
            blackHole.Opacity=SmoothBand(0,.43,progress);
            if(progress<=0)SyncStandby();

            // Make room only once the seed has disappeared and the horizon is opaque.
            double room=SmoothBand(.46,1,progress);
            if(resumingEvaporation)room=BlendReconnection(progress);
            double target=Live?1:0;
            double travel=lensRevealFrom==target?1:UiMotion.Unit((progress-lensRevealFrom)/(target-lensRevealFrom));
            double style=SmoothBand(0,.40,travel);
            double mass=Live?1:StandbyLensMass;
            SetCoreLens(new LensState(
                lensAtReveal.Mass+(mass-lensAtReveal.Mass)*travel,
                lensAtReveal.Horizon+(target-lensAtReveal.Horizon)*style,
                lensAtReveal.Starlight+(1-lensAtReveal.Starlight)*style));
            SetCoreRoom(room);
            if(progress>=1)TryRevealRivers();
        }

        void SetCoreRoom(double room)
        {
            if(room==ChartReveal)return;
            ChartReveal=room;
            ChartRevealChanged?.Invoke();
        }

        void SyncCoreOrbit()
        {
            SyncObservatory();
            if(evaporationRunning && (!running || disposed || highContrast || viewport.IsEmpty || !SystemParameters.ClientAreaAnimation)){
                FormCore(false);
                return;
            }
            SyncStandby();
            bool active=Live && running && !disposed && !highContrast && !viewport.IsEmpty && SystemParameters.ClientAreaAnimation;
            if(orbitRunning==active && (!active || orbitRate==refreshRate)) return;
            orbitRunning=active; orbitRate=refreshRate;
            Spin(outerOrbit,7.8,active);
            Spin(innerOrbit,3.65,active);
            Spin(photonOrbit,5.15,active);
            Spin(coronaOrbit,22,active);
            SyncDiskExposure();
            if(!active){
                lastUpload=lastDownload=-1;
                if(!running || highContrast || !SystemParameters.ClientAreaAnimation)StopDiskFlare();
            }
        }

        void SyncStandby()
        {
            bool standby = !disposed && !highContrast && (stellarPhase=="stopped" || stellarPhase=="waiting" || stellarPhase=="unknown") &&
                (!evaporationRunning || evaporationElapsed>=terminalFlashAt+650) &&
                SystemParameters.ClientAreaAnimation && (double)GetValue(CoreRevealProperty)<=0;
            if (!standby || viewport.IsEmpty || !running || !IsLoaded || !IsVisible)
            {
                PauseStandby();
                if (!standby && (standbyArt.Opacity!=0 || prominenceFadeStart.HasValue || standbyClock.ElapsedTicks!=0))
                {
                    standbyClock.Reset();
                    prominenceFadeStart=null;
                    standbyArt.Opacity=0;
                    standbyScale.ScaleX=standbyScale.ScaleY=standbyBody.Opacity=1;
                }
                return;
            }
            if (standbyClock.IsRunning) return;
            if (prominence==null)
            {
                prominence=new StandbyProminence();
                standbyArt.Children.Add(prominence.Art);
            }
            standbyArt.Opacity=1;
            PaintStandby(standbyClock.Elapsed.TotalSeconds%6/6);
            lastStandbyFrame=TimeSpan.MinValue;
            standbyClock.Start();
            UiFrames.Rendering+=StandbyFrame;
        }

        void PauseStandby()
        {
            if (!standbyClock.IsRunning) return;
            standbyClock.Stop();
            if(prominenceFadeStart.HasValue){
                // A deactivation can land between composed frames. Resume the exact
                // alpha that was visible rather than skipping that partial frame.
                double shown=1-Math.Pow(1-UiMotion.Unit(standbyArt.Opacity),1.0/3);
                prominenceFadeStart=standbyClock.Elapsed.TotalMilliseconds-80*shown;
            }
            UiFrames.Rendering-=StandbyFrame;
        }

        void StandbyFrame(object sender,EventArgs args)
        {
            var frame=((RenderingEventArgs)args).RenderingTime;
            if (frame==lastStandbyFrame) return;
            lastStandbyFrame=frame;
            PaintStandby(standbyClock.Elapsed.TotalSeconds%6/6);
        }

        void PaintStandby(double phase)
        {
            double breath=.5-.5*Math.Cos(2*Math.PI*phase);
            if(prominenceFadeStart.HasValue){
                // Use the same pausable clock as the arcs, including on the first
                // 80 ms of re-ignition when the window can be hidden mid-fade.
                double t=UiMotion.Unit((standbyClock.Elapsed.TotalMilliseconds-prominenceFadeStart.Value)/80);
                standbyArt.Opacity=1-Math.Pow(1-t,3);
                if(t>=1)prominenceFadeStart=null;
            }
            standbyScale.ScaleX=standbyScale.ScaleY=(.95+.12*breath)*(1-.32*gestureMass);
            standbyBody.Opacity=.82+.18*breath;
            if(!nativeProminence)prominence.Paint(phase);
        }

        void Spin(RotateTransform material,double seconds,bool active)
        {
            double angle=material.Angle%360;
            material.Angle=angle;
            if(!active){material.BeginAnimation(RotateTransform.AngleProperty,null);return;}
            var turn=new DoubleAnimation(angle,angle+360,TimeSpan.FromSeconds(seconds)){RepeatBehavior=RepeatBehavior.Forever};
            material.BeginAnimation(RotateTransform.AngleProperty,UiMotion.AtRefresh(turn,refreshRate));
        }

    }
}
