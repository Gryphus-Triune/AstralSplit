using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        static readonly double[] thermalMasses={1,.70,.45,.28,.15,.05};
        static readonly Color[] thermalHues={Color.FromRgb(122,30,18),Color.FromRgb(255,90,42),
            Color.FromRgb(255,180,94),Color.FromRgb(255,240,207),Color.FromRgb(207,225,255),Color.FromRgb(169,184,255)};
        static readonly Brush[] quantumBrushes=MakeQuantumBrushes();

        static int ThermalBin(double mass)
        {
            int bin=0;
            while(bin<thermalMasses.Length-2 && mass<thermalMasses[bin+1])bin++;
            return bin;
        }
        static double ThermalFraction(double mass,int bin)
        {
            return UiMotion.Unit((thermalMasses[bin]-mass)/(thermalMasses[bin]-thermalMasses[bin+1]));
        }
        static Brush[] MakeQuantumBrushes()
        {
            var result=new Brush[32];
            for(int i=0;i<result.Length;i++){
                double m=i/(double)(result.Length-1);
                int bin=ThermalBin(m);
                double mix=ThermalFraction(m,bin);
                var a=thermalHues[bin];var b=thermalHues[bin+1];
                var color=Color.FromRgb((byte)(a.R+(b.R-a.R)*mix),(byte)(a.G+(b.G-a.G)*mix),(byte)(a.B+(b.B-a.B)*mix));
                var brush=new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop(Colors.White,0));
                brush.GradientStops.Add(new GradientStop(color,.23));
                brush.GradientStops.Add(new GradientStop(A(color,.30),.52));
                brush.GradientStops.Add(new GradientStop(A(color,0),1));brush.Freeze();result[i]=brush;
            }
            return result;
        }

        static RadialGradientBrush EmissionRing(Color color,bool shell)
        {
            var b=new RadialGradientBrush();
            if(shell){
                b.GradientStops.Add(new GradientStop(A(color,0),0));
                b.GradientStops.Add(new GradientStop(A(color,0),.966));
                b.GradientStops.Add(new GradientStop(A(color,.09),.977));
                b.GradientStops.Add(new GradientStop(A(color,.74),.985));
                b.GradientStops.Add(new GradientStop(A(Colors.White,.94),.990));
                b.GradientStops.Add(new GradientStop(A(color,0),1));
            }else{
                b.GradientStops.Add(new GradientStop(A(color,0),0));
                b.GradientStops.Add(new GradientStop(A(color,0),.57));
                b.GradientStops.Add(new GradientStop(A(color,.24),.618));
                b.GradientStops.Add(new GradientStop(A(color,.97),.646));
                b.GradientStops.Add(new GradientStop(A(color,.64),.669));
                b.GradientStops.Add(new GradientStop(A(color,.15),.733));
                b.GradientStops.Add(new GradientStop(A(color,.05),.82));
                b.GradientStops.Add(new GradientStop(A(color,0),1));
            }
            b.Freeze();return b;
        }

        void InitializeThermalArt()
        {
            for(int i=0;i<thermalColors.Length;i++){
                var ring=new DrawingGroup();
                using(var dc=ring.Open())dc.DrawEllipse(EmissionRing(thermalHues[i],false),null,new Point(),34,32);
                thermalColors[i]=ring;thermalRadiation.Children.Add(ring);
            }
            var cold=Color.FromRgb(159,193,255);
            using(var dc=radiationShell.Open())dc.DrawEllipse(EmissionRing(cold,true),null,new Point(),140,140);
            using(var dc=terminalFlash.Open()){
                dc.DrawEllipse(Glow(A(cold,.85)),null,new Point(),42,39);
                dc.DrawEllipse(Glow(Color.FromRgb(214,234,255)),null,new Point(),21,18);
                var spikes=FrozenPath("M-24,0 Q-2.6,-.55 -.7,-2 L0,-14 L.7,-2 Q2,-.5 24,0 Q2,.5 .7,2 L0,15 L-.7,2 Q-2,.5 -24,0 Z");
                dc.DrawGeometry(Ink(Color.FromRgb(221,238,255),.80),null,spikes);
                dc.DrawEllipse(Glow(Colors.White),null,new Point(),5,5);
            }
            var rng=new Random(30918);
            for(int i=0;i<remnantGrains.Length;i++)
                remnantGrains[i]=new ReturnGrain(i,rng,remnantGas);
        }

        sealed class ReturnGrain
        {
            readonly double angle,radius,turn,delay;
            readonly DrawingGroup sprite=new DrawingGroup{Opacity=0};
            readonly MatrixTransform pose=new MatrixTransform();
            internal ReturnGrain(int i,Random random,DrawingGroup parent)
            {
                angle=(i+random.NextDouble()*.6)*2*Math.PI/24;
                radius=46+24*random.NextDouble();turn=1.4+.8*random.NextDouble();delay=70*random.NextDouble();
                double r=.65+random.NextDouble()*.65;
                var c=i%4==0?Color.FromRgb(255,208,153):i%3==0?Color.FromRgb(182,179,250):Color.FromRgb(163,212,255);
                using(var dc=sprite.Open()){
                    dc.DrawEllipse(Glow(A(c,.76)),null,new Point(),3.2*r,1.55*r);
                    dc.DrawEllipse(Ink(c,.88),null,new Point(),.78*r,.60*r);
                }
                sprite.Transform=pose;parent.Children.Add(sprite);
            }
            internal void Draw(double elapsed)
            {
                double u=UiMotion.Unit((elapsed-delay)/(420-delay));
                sprite.Opacity=SmoothBand(0,.17,u)*(1-SmoothBand(.79,1,u));
                if(sprite.Opacity==0)return;
                double progress=u*u;
                double a=angle+turn*progress;
                double r=radius*(1-progress);
                double x=Math.Cos(a)*r,y=Math.Sin(a)*r*.70;
                var tangent=new Vector(-Math.Cos(a)-turn*(1-progress)*Math.Sin(a),
                    .70*(-Math.Sin(a)+turn*(1-progress)*Math.Cos(a)));
                tangent.Normalize();double stretch=1+1.7*progress;
                pose.Matrix=new Matrix(tangent.X*stretch,tangent.Y*stretch,-tangent.Y,tangent.X,x,y);
            }
        }

        void DrawReturningGas(double after)
        {
            remnantGas.Opacity=after>=50 && after<=470?1:0;
            if(remnantGas.Opacity>0)foreach(var grain in remnantGrains)grain.Draw(after-50);
        }

        sealed class EvaporationQuanta
        {
            readonly double birth,lifetime,radialSpeed,initialX,initialY,dx,dy;
            readonly DrawingGroup sprite=new DrawingGroup{Opacity=0};
            readonly TranslateTransform shift=new TranslateTransform();
            internal EvaporationQuanta(double born,double mass,Random rng,DrawingGroup parent)
            {
                birth=born;lifetime=380+rng.NextDouble()*240;
                double angle=rng.NextDouble()*Math.PI*2;
                dx=Math.Cos(angle);dy=Math.Sin(angle);
                initialX=22*mass*dx;initialY=20.9*mass*dy;
                radialSpeed=(60+rng.NextDouble()*80)/Math.Sqrt(Math.Max(.02,mass));
                var glow=quantumBrushes[(int)Math.Round(mass*(quantumBrushes.Length-1))];
                double size=.66+.56*rng.NextDouble();
                using(var dc=sprite.Open())dc.DrawEllipse(glow,null,new Point(),1.5*size,1.5*size);
                sprite.Transform=shift;parent.Children.Add(sprite);
            }
            internal void Draw(double ms,double coreScale)
            {
                double age=ms-birth;
                if(age<=0 || age>=lifetime){sprite.Opacity=0;return;}
                double progress=age/lifetime;
                sprite.Opacity=SmoothBand(0,.07,progress)*(1-SmoothBand(.35,1,progress));
                // Speeds in display-independent pixels remain consistent at both core magnifications.
                double distance=radialSpeed*age/(1000*Math.Max(.01,coreScale));
                shift.X=initialX+dx*distance;shift.Y=initialY+dy*distance;
            }
        }

        void PrepareQuanta()
        {
            var random=new Random(22417);var grains=new List<EvaporationQuanta>();
            // Integrate the emission budget before starting the clock. A missed frame
            // changes the positions sampled, never the number or birth time of the quanta.
            double credit=0;
            escapingQuanta.Children.Clear();
            for(double ms=thermalStart;ms<terminalFlashAt;ms+=2){
                double u=(ms-thermalStart)/(terminalFlashAt-thermalStart);
                double m=initialMass*Math.Pow(1-u,1d/3);
                credit+=.002*Math.Min(220,14/Math.Max(.0001,m*m));
                if(credit>=1){credit-=1;grains.Add(new EvaporationQuanta(ms,m,random,escapingQuanta));}
            }
            quanta=grains.ToArray();
        }

        void DrawHawkingLight(double ms)
        {
            var m=evaporationMass;
            heatRadius.ScaleX=heatRadius.ScaleY=m;
            thermalRadiation.Opacity=m<=0?0:Math.Min(1,.12/(m*m))*SmoothBand(thermalStart,thermalStart+100,ms);
            if(thermalRadiation.Opacity>0){
                int bin=ThermalBin(m);double mix=ThermalFraction(m,bin);
                for(int i=0;i<thermalColors.Length;i++){
                    double alpha=i==bin?1-mix:i==bin+1?mix:0;
                    thermalColors[i].Opacity=alpha*(i==0?.25:1);
                }
            }
            foreach(var grain in quanta)grain.Draw(ms,CoreScale);
        }

        void PlayTerminalEmission()
        {
            terminalRippleAt=flightClock.Elapsed.TotalSeconds;
            // Keep this subscene at the position of the flash. A new connection or
            // a contracting chart cannot drag a radiation front that already escaped.
            StopTerminalEmission();
            var origin=new MatrixTransform(corePlacement.Matrix);origin.Freeze();
            terminalScene.Transform=origin;terminalScene.Opacity=1;
            var flash=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(90),FillBehavior=FillBehavior.Stop};
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(1,TimeSpan.FromMilliseconds(30)));
            flash.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(90),new KeySpline(.2,0,.3,1)));
            terminalFlash.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(flash,refreshRate));
            shellRadius.ScaleX=shellRadius.ScaleY=1;
            var expand=UiMotion.Ease(0,1,420,refreshRate,.18,0,.28,1);
            shellRadius.BeginAnimation(ScaleTransform.ScaleXProperty,expand);shellRadius.BeginAnimation(ScaleTransform.ScaleYProperty,expand);
            int ticket=terminalRevision;
            var fade=UiMotion.Ease(.8,0,420,refreshRate,.28,.1,.55,1);
            fade.Completed+=(s,e)=>{if(ticket==terminalRevision)StopTerminalEmission();};
            radiationShell.BeginAnimation(DrawingGroup.OpacityProperty,fade);
        }

        void StopTerminalEmission()
        {
            ++terminalRevision;
            terminalScene.Opacity=terminalFlash.Opacity=radiationShell.Opacity=0;
            terminalFlash.BeginAnimation(DrawingGroup.OpacityProperty,null);
            radiationShell.BeginAnimation(DrawingGroup.OpacityProperty,null);
            shellRadius.BeginAnimation(ScaleTransform.ScaleXProperty,null);shellRadius.BeginAnimation(ScaleTransform.ScaleYProperty,null);
        }
    }
}
