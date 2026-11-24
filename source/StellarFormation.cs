using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly Stopwatch formationClock=new Stopwatch();
        readonly DrawingGroup birthWave=new DrawingGroup(), ignition=new DrawingGroup(), ejecta=new DrawingGroup(), novaOrigin=new DrawingGroup();
        readonly DrawingGroup afterglow=new DrawingGroup();
        readonly DrawingGroup dustBehind=new DrawingGroup(), dustAhead=new DrawingGroup();
        readonly DrawingGroup outsideFormation=new DrawingGroup(), outsideLocal=new DrawingGroup();
        readonly ScaleTransform birthScale=new ScaleTransform(), ignitionScale=new ScaleTransform(), ejectaScale=new ScaleTransform();
        readonly TranslateTransform novaCenter=new TranslateTransform();
        AccretingDust birthDust;
        bool formationRunning,formationAborting;
        double gainFrom,gainAt;

        void InitializeFormation()
        {
            birthWave.Transform=birthScale;ignition.Transform=ignitionScale;novaOrigin.Transform=novaCenter;
            ejecta.Transform=ejectaScale;
            novaOrigin.Children.Add(birthWave);novaOrigin.Children.Add(ejecta);novaOrigin.Children.Add(ignition);
            var shell=new RadialGradientBrush();
            shell.GradientStops.Add(new GradientStop(Colors.Transparent,0));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(0,246,135,66),.82));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(42,244,131,83),.885));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(18,255,183,116),.912));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(103,255,190,132),.937));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(235,255,246,213),.950));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(156,184,216,255),.956));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(33,151,133,248),.972));
            shell.GradientStops.Add(new GradientStop(Color.FromArgb(0,148,151,242),1));shell.Freeze();
            using(var d=birthWave.Open())d.DrawEllipse(shell,null,new Point(),100,100);
            using(var d=ignition.Open()){
                d.DrawEllipse(Glow(Color.FromArgb(135,206,146,245)),null,new Point(),63,51);
                d.DrawEllipse(Glow(Color.FromRgb(255,177,94)),null,new Point(),35,30);
                var rays=Geometry.Parse("M-53,0 Q-5,-1 -1,-4 L0,-28 L1,-4 Q4,-1 53,0 Q4,1 1,4 L0,28 L-1,4 Q-5,1 -53,0 Z");
                rays.Freeze();d.DrawGeometry(Ink(Color.FromRgb(255,224,178),.78),null,rays);
                d.DrawEllipse(Glow(Colors.White),null,new Point(),13,13);
            }
            using(var d=ejecta.Open()){
                int i=0;
                foreach(double azimuth in new[]{-167d,-130,-114,-86,-48,-22,14,35,74,122,147}){
                    double head=34+(17*i)%31,tail=head-7-(i*5)%14;
                    Color hue=i%4==0?Color.FromRgb(173,209,255):Color.FromRgb(255,204,145);
                    var ink=new LinearGradientBrush(A(hue,0),A(hue,.91),0);
                    ink.GradientStops.Insert(1,new GradientStop(A(hue,.29),.54));ink.Freeze();
                    d.PushTransform(new RotateTransform(azimuth));
                    var streak=new StreamGeometry();
                    using(var line=streak.Open()){
                        line.BeginFigure(new Point(tail,-.10),true,true);
                        line.LineTo(new Point(head,-.40-(i%3)*.1),true,false);line.LineTo(new Point(head-.7,.38),true,false);
                    }
                    streak.Freeze();d.DrawGeometry(ink,null,streak);d.DrawEllipse(Glow(A(hue,.78)),null,new Point(head,0),1.8,1.0);
                    d.Pop();i++;
                }
            }
            using(var d=afterglow.Open()){
                d.DrawEllipse(Glow(Color.FromArgb(190,255,194,129)),null,new Point(-12,-16),40,24);
                d.DrawEllipse(Glow(Color.FromArgb(146,178,158,248)),null,new Point(19,5),26,18);
            }
            birthDust=new AccretingDust(dustBehind,dustAhead){Surface=this};
            // Both sides of the chart clip use the same grains and placement. The
            // accretion disk itself belongs exclusively to the black hole.
            outsideLocal.Transform=corePlacement;
            outsideLocal.Children.Add(dustBehind);outsideLocal.Children.Add(dustAhead);
            birthWave.Opacity=ignition.Opacity=ejecta.Opacity=afterglow.Opacity=0;
        }

        static double SmoothBand(double lower,double upper,double x)
        {
            double t=UiMotion.Unit((x-lower)/(upper-lower));
            return t*t*(3-2*t);
        }

        void UpdateFormationClip()
        {
            if(!formationRunning || overlaySize.Width<=0 || viewport.IsEmpty)return;
            var clip=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(new Rect(overlaySize)),
                coreAperture);
            clip.Freeze();outsideFormation.ClipGeometry=clip;
        }

        double FormationGain(double elapsed)
        {
            double target=formationAborting?0:1;
            return gainFrom+(target-gainFrom)*SmoothBand(gainAt,gainAt+150,elapsed);
        }

        void MaskOrbit(Brush mask){
            // Deposits are indexed by material azimuth in the neutral plane. Warp the
            // mask with that material, so the particles light the place where they land.
            farDisk.OpacityMask=nearDisk.OpacityMask=diskAttitude.SetExposure(mask)?null:mask;
            opticalArc.OpacityMask=photonAttitude.SetExposure(mask)?null:mask;
            jetSource.OpacityMask=jetAttitude.SetExposure(mask)?null:mask;
        }

        void ChangeFormation(bool animate)
        {
            if(!animate || overlaySize.Width<1 || overlaySize.Height<1){FinishFormation();SettleRivers(Live);return;}
            if(formationRunning){
                bool abort=!Live;
                if(formationAborting!=abort){
                    double now=formationClock.Elapsed.TotalMilliseconds;
                    gainFrom=FormationGain(now);gainAt=now;formationAborting=abort;
                }
                return;
            }
            if(!Live){backRing.Transform=frontRing.Transform=holeScale;return;}
            formationAborting=false;gainFrom=1;gainAt=0;formationRunning=true;
            backRing.Transform=frontRing.Transform=Transform.Identity;
            MaskOrbit(birthDust.Mask);
            outsideFormation.Children.Add(outsideLocal);UpdateFormationClip();
            formationClock.Restart();DrawFormation(0);
            UiFrames.Rendering+=AdvanceFormation;
        }

        void AdvanceFormation(object sender,EventArgs args)
        {
            if(!running || disposed || highContrast || viewport.IsEmpty || !SystemParameters.ClientAreaAnimation){
                FinishFormation();SettleRivers(Live);return;
            }
            double now=formationClock.Elapsed.TotalMilliseconds;
            if((formationAborting && now>=gainAt+150) || (!formationAborting && now>=Math.Max(birthDust.Duration,gainAt+150))){
                FinishFormation();
                if(Live){diskGathering=true;TryRevealRivers();}
                return;
            }
            DrawFormation(now);
        }

        void DrawFormation(double ms)
        {
            double gain=FormationGain(ms);
            var gate=GateCenter;
            double deposited=birthDust.Draw(ms,overlaySize,gate,CoreScale);
            // This is actual deposited dust, so a changed frame rate or slow
            // presentation cannot expose a connection before the first disk arc.
            if(Live && !formationAborting && deposited>=.025){diskGathering=true;TryRevealRivers();}
            dustBehind.Opacity=dustAhead.Opacity=gain;
            backRing.Opacity=frontRing.Opacity=gain;
            outsideFormation.Opacity=blackHole.Opacity;
            novaCenter.X=gate.X;novaCenter.Y=gate.Y;
            double dx=Math.Max(gate.X,overlaySize.Width-gate.X),dy=Math.Max(gate.Y,overlaySize.Height-gate.Y);
            double reach=1.26*Math.Sqrt(dx*dx+dy*dy)+20;
            double u=UiMotion.Unit((ms-75)/1320);
            double radius=1+(reach-1)*(1-Math.Pow(1-u,1.32));
            birthScale.ScaleX=birthScale.ScaleY=radius/100;
            birthWave.Opacity=gain*SmoothBand(0,.065,u)*(1-.42*SmoothBand(.23,.84,u))*(1-SmoothBand(.86,1,u));
            double flash=UiMotion.Unit(ms/550);
            ignitionScale.ScaleX=ignitionScale.ScaleY=CoreScale/CoreMagnification*(.20+1.36*(1-Math.Pow(1-flash,2.4)));
            ignition.Opacity=gain*SmoothBand(0,.23,flash)*(1-SmoothBand(.24,1,flash));
            double debris=UiMotion.Unit(ms/850);
            ejectaScale.ScaleX=ejectaScale.ScaleY=CoreScale/CoreMagnification*(.10+2.45*(1-Math.Pow(1-debris,1.5)));
            ejecta.Opacity=gain*.87*SmoothBand(.10,.27,debris)*(1-SmoothBand(.38,1,debris));
            afterglow.Opacity=gain*SmoothBand(.10,.31,flash)*(1-SmoothBand(850,1700,ms));
        }

        void FinishFormation()
        {
            if(formationRunning)UiFrames.Rendering-=AdvanceFormation;
            formationClock.Reset();formationRunning=false;formationAborting=false;
            outsideFormation.Children.Clear();outsideFormation.ClipGeometry=null;
            birthWave.Opacity=ignition.Opacity=ejecta.Opacity=afterglow.Opacity=0;
            dustBehind.Opacity=dustAhead.Opacity=0;
            birthDust.Clear();
            backRing.Transform=frontRing.Transform=Live?Transform.Identity:holeScale;
            MaskOrbit(null);
            backRing.Opacity=frontRing.Opacity=Live?1:0;
        }
    }
}
