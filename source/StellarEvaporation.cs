using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        static readonly DependencyProperty EvaporationTimeProperty=DependencyProperty.Register(
            "EvaporationTime",typeof(double),typeof(StarfieldLayer),new PropertyMetadata(0d,
                (obj,args)=>((StarfieldLayer)obj).PaintEvaporation((double)args.NewValue)));
        readonly DrawingGroup thermalRadiation=new DrawingGroup(),escapingQuanta=new DrawingGroup(),remnantGas=new DrawingGroup();
        readonly DrawingGroup terminalFlash=new DrawingGroup(),radiationShell=new DrawingGroup();
        readonly DrawingGroup thermalScene=new DrawingGroup(),terminalScene=new DrawingGroup();
        readonly DrawingGroup thermalAfterimage=new DrawingGroup();
        readonly DrawingGroup rearDiskAfterimage=new DrawingGroup(),frontDiskAfterimage=new DrawingGroup();
        readonly DrawingGroup tunCaption=new DrawingGroup(),stellarCaption=new DrawingGroup();
        readonly ScaleTransform drainingRings=new ScaleTransform(),heatRadius=new ScaleTransform(),shellRadius=new ScaleTransform();
        readonly DrawingGroup[] thermalColors=new DrawingGroup[6];
        readonly ReturnGrain[] remnantGrains=new ReturnGrain[24];
        EvaporationQuanta[] quanta=new EvaporationQuanta[0];
        bool evaporationRunning,resumingEvaporation,terminalEmitted,standbyRelit,drainingFormation;
        double evaporationElapsed,evaporationMass,terminalFlashAt,thermalStart,initialMass,initialRoom,initialHorizon,
            initialSeed,initialSeedSize,initialDisk;
        double resumeMass,resumeChart,resumeSeed,resumeSeedSize,resumeShadow,resumeStarBody,resumeStarAlpha;
        LensState lensAtEvaporation;
        double outerAngle,innerAngle,photonAngle,coronaAngle;
        int terminalRevision,thermalRevision;

        void InitializeEvaporation()
        {
            thermalScene.Children.Add(thermalRadiation);thermalScene.Children.Add(escapingQuanta);
            coreObject.Children.Add(thermalScene);coreObject.Children.Add(remnantGas);coreObject.Children.Add(thermalAfterimage);
            thermalRadiation.Transform=heatRadius;
            InitializeThermalArt();
            radiationShell.Transform=shellRadius;
            terminalScene.Children.Add(radiationShell);terminalScene.Children.Add(terminalFlash);
            foreground.Children.Add(terminalScene);
            thermalScene.Opacity=terminalScene.Opacity=remnantGas.Opacity=0;
        }

        void DrawGateCaption(DrawingContext context,Point at,double width)
        {
            string resting=stellarPhase=="restoring"?"恒星  /  恢复":
                stellarPhase=="stopped"?"恒星  /  待命":"恒星  /  等待";
            using(var dc=tunCaption.Open())Label(dc,"黑洞  /  TUN",at,11,width,.94);
            using(var dc=stellarCaption.Open())Label(dc,resting,at,11,width,.68);
            context.DrawDrawing(tunCaption);context.DrawDrawing(stellarCaption);
        }

        void SetGateCaption(bool tun,bool animate)
        {
            OpacityTo(tunCaption,tun?1:0,150,animate,refreshRate);
            OpacityTo(stellarCaption,tun?0:1,150,animate,refreshRate);
        }

        void StartEvaporation()
        {
            ++thermalRevision;
            initialMass=UiMotion.Unit(holeScale.ScaleX);initialHorizon=blackHole.Opacity;
            initialRoom=ChartReveal;initialSeed=seedStar.Opacity;initialSeedSize=starScale.ScaleX;
            initialDisk=backRing.Opacity;
            lensAtEvaporation=coreLens;
            outerAngle=outerOrbit.Angle;innerAngle=innerOrbit.Angle;
            photonAngle=photonOrbit.Angle;coronaAngle=coronaOrbit.Angle;
            drainingFormation=formationRunning;
            thermalStart=drainingFormation?150:120;
            terminalFlashAt=thermalStart+930*Math.Max(.35,initialMass);
            terminalEmitted=false;standbyRelit=false;evaporationRunning=true;
            evaporationElapsed=0;evaporationMass=initialMass;
            thermalScene.Opacity=1;remnantGas.Opacity=0;
            PrepareQuanta();
            ChangeFormation(true);
            // An interrupted formation owns its deposit mask until its 150 ms abort completes.
            if(!drainingFormation)backRing.Transform=frontRing.Transform=drainingRings;
            PaintEvaporation(0);
            LayoutStarLens();
            double end=terminalFlashAt+730;
            var clock=UiMotion.Linear(0,end,end,refreshRate);
            clock.FillBehavior=FillBehavior.HoldEnd;
            int revision=thermalRevision;
            clock.Completed+=(s,e)=>{
                if(revision!=thermalRevision || !evaporationRunning)return;
                PaintEvaporation(end);
                StopEvaporation(false,true);
                PaintCoreReveal(0);
                LayoutStarLens();
            };
            SetValue(EvaporationTimeProperty,0d);
            BeginAnimation(EvaporationTimeProperty,clock);
        }

        void PaintEvaporation(double ms)
        {
            if(!evaporationRunning)return;
            evaporationElapsed=ms;
            double t=UiMotion.Unit((ms-thermalStart)/(terminalFlashAt-thermalStart));
            double remaining=Math.Pow(1-t,1d/3);
            evaporationMass=initialMass*remaining;
            SetValue(CoreRevealProperty,evaporationMass);
            holeScale.ScaleX=holeScale.ScaleY=evaporationMass;
            double after=ms-terminalFlashAt;
            blackHole.Opacity=after>=0?0:initialHorizon+(1-initialHorizon)*SmoothBand(0,thermalStart,ms);
            if(!formationRunning){
                double drain=UiMotion.Unit(ms/220);
                drainingRings.ScaleX=drainingRings.ScaleY=initialMass*(1-.4*drain);
                backRing.Transform=frontRing.Transform=drainingRings;
                backRing.Opacity=frontRing.Opacity=drainingFormation?0:initialDisk*(1-SmoothBand(0,1,drain));
                double turn=Math.Min(ms,220)/1000;
                outerOrbit.Angle=outerAngle+turn*720/7.8;innerOrbit.Angle=innerAngle+turn*720/3.65;
                coronaOrbit.Angle=coronaAngle+turn*720/22;photonOrbit.Angle=photonAngle+turn*720/5.15;
            }
            DrawHawkingLight(ms);
            if(after>=0 && !terminalEmitted){
                terminalEmitted=true;
                PlayTerminalEmission();
                SetGateCaption(false,true);
            }
            DrawReturningGas(after);
            double ignitionAge=after-350;
            double seedFraction=UiMotion.Unit(ignitionAge/380);
            if(ignitionAge<=0){
                seedStar.Opacity=initialSeed*(1-SmoothBand(0,thermalStart,ms));
                starScale.ScaleX=starScale.ScaleY=initialSeedSize;
            }else{
                // An underdamped ignition has one slight overshoot before the six-second breathing begins.
                double seconds=ignitionAge/1000;
                double envelope=Math.Exp(-11.7*seconds);
                double displacement=Math.Cos(19.7*seconds)+11.7/19.7*Math.Sin(19.7*seconds);
                double radius=1-.8*envelope*displacement*(1-SmoothBand(.78,1,seedFraction));
                starScale.ScaleX=starScale.ScaleY=radius;
                seedStar.Opacity=SmoothBand(0,160,ignitionAge);
                if(stellarPhase=="stopped" && !standbyClock.IsRunning){
                    double gain=SmoothBand(0,300,ignitionAge);
                    standbyBody.Opacity=1-.18*gain;standbyScale.ScaleX=standbyScale.ScaleY=1-.05*gain;
                }
                if(!standbyRelit && ignitionAge>=300){
                    standbyRelit=true;
                    // The terminal wave has already passed. Let the new star
                    // send its own smaller wake through the same glass layer.
                    standbyRippleAt=flightClock.Elapsed.TotalSeconds;
                    if(stellarPhase=="stopped"){
                        prominenceFadeStart=standbyClock.Elapsed.TotalMilliseconds;
                        standbyArt.Opacity=0;
                        SyncStandby();
                    }
                }
            }
            if(t<1){
                // The final mass loss is too steep to sample at display
                // refresh rates. Fade the remaining bend into the flash.
                double visible=1-SmoothBand(.84,1,t);
                double starlight=lensAtEvaporation.Horizon<1?lensAtEvaporation.Starlight*visible:0;
                SetCoreLens(new LensState(lensAtEvaporation.Mass*remaining,lensAtEvaporation.Horizon*visible,starlight));
            }else
                SetCoreLens(new LensState(StandbyLensMass*seedStar.Opacity,0,seedStar.Opacity));
            // Keep the camera and the application list steady during mass loss and the flash.
            SetCoreRoom(initialRoom*(1-SmoothBand(30,460,after)));
        }

        void CaptureReconnection()
        {
            resumeMass=evaporationMass;resumeChart=ChartReveal;resumeShadow=blackHole.Opacity;
            resumeSeed=seedStar.Opacity;resumeSeedSize=starScale.ScaleX;
            resumeStarBody=standbyScale.ScaleX;resumeStarAlpha=standbyBody.Opacity;
            if(!formationRunning && backRing.Opacity>.005){
                FadeSnapshot(rearDiskAfterimage,backRing);
                FadeSnapshot(frontDiskAfterimage,frontRing);
            }
        }

        void FadeSnapshot(DrawingGroup ghost,DrawingGroup current)
        {
            var snapshot=Frozen(current);
            ghost.BeginAnimation(DrawingGroup.OpacityProperty,null);
            ghost.Children.Clear();ghost.Children.Add(snapshot);
            var fade=UiMotion.Ease(1,0,150,refreshRate);
            fade.Completed+=(s,e)=>{
                if(ghost.Children.Count==0 || !ReferenceEquals(ghost.Children[0],snapshot))return;
                ghost.Children.Clear();ghost.BeginAnimation(DrawingGroup.OpacityProperty,null);
            };
            ghost.Opacity=0;ghost.BeginAnimation(DrawingGroup.OpacityProperty,fade);
        }

        void PreserveThermalEmission()
        {
            bool visible=thermalRadiation.Opacity>.001 || remnantGas.Opacity>.001;
            if(!visible)foreach(DrawingGroup grain in escapingQuanta.Children)
                if(grain.Opacity>.001){visible=true;break;}
            if(!visible)return;
            var picture=new DrawingGroup();
            picture.Children.Add(thermalScene);picture.Children.Add(remnantGas);
            FadeSnapshot(thermalAfterimage,picture);
        }

        double BlendReconnection(double progress)
        {
            double u=resumeMass>=1?1:UiMotion.Unit((progress-resumeMass)/(1-resumeMass));
            double star=SmoothBand(0,.40,u);
            holeScale.ScaleX=holeScale.ScaleY=progress;
            blackHole.Opacity=resumeShadow+(1-resumeShadow)*SmoothBand(0,.43,u);
            starScale.ScaleX=starScale.ScaleY=resumeSeedSize+(.045-resumeSeedSize)*star;
            seedStar.Opacity=resumeSeed*(1-star);
            standbyScale.ScaleX=standbyScale.ScaleY=resumeStarBody+(1-resumeStarBody)*star;
            standbyBody.Opacity=resumeStarAlpha+(1-resumeStarAlpha)*star;
            return resumeChart+(1-resumeChart)*SmoothBand(.46,1,u);
        }

        void StopEvaporation(bool returningToLive,bool settle)
        {
            ++thermalRevision;
            evaporationRunning=false;
            BeginAnimation(EvaporationTimeProperty,null);
            if(returningToLive)PreserveThermalEmission();
            thermalScene.Opacity=remnantGas.Opacity=0;
            escapingQuanta.Children.Clear();quanta=Array.Empty<EvaporationQuanta>();
            if(settle){
                thermalAfterimage.BeginAnimation(DrawingGroup.OpacityProperty,null);thermalAfterimage.Children.Clear();
                rearDiskAfterimage.BeginAnimation(DrawingGroup.OpacityProperty,null);rearDiskAfterimage.Children.Clear();
                frontDiskAfterimage.BeginAnimation(DrawingGroup.OpacityProperty,null);frontDiskAfterimage.Children.Clear();
                if(!running || disposed || highContrast || !SystemParameters.ClientAreaAnimation || viewport.IsEmpty)
                    StopTerminalEmission();
                if(!standbyClock.IsRunning){standbyScale.ScaleX=standbyScale.ScaleY=standbyBody.Opacity=1;}
            }
        }
    }
}
