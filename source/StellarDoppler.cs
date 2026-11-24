using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly DrawingGroup uploadLimb=new DrawingGroup{Opacity=0}, downloadLimb=new DrawingGroup{Opacity=0};
        readonly DrawingGroup diskFlare=new DrawingGroup{Opacity=0}, flarePlume=new DrawingGroup();
        readonly DrawingGroup bipolarJets=new DrawingGroup{Opacity=0};
        readonly ScaleTransform flareRise=new ScaleTransform();
        double lastUpload=-1,lastDownload=-1,meanUpload,meanDownload;
        double uploadExposure,downloadExposure;
        double lastFlare=Double.NegativeInfinity;
        bool neutralFlare;

        void InitializeDiskTraffic()
        {
            foreach(var layer in new[]{uploadLimb,downloadLimb}){
                var texture=StellarSkyArt.ReadImage(layer==uploadLimb?"upload-glow.png":"download-glow.png");
                const double width=174;
                double height=width*texture.PixelHeight/texture.PixelWidth;
                using(var dc=layer.Open())dc.DrawImage(texture,new Rect(-width/2,-height/2,width,height));
            }
            flarePlume.Transform=flareRise;
            diskFlare.Children.Add(flarePlume);
            var tilt=new RotateTransform(-11);tilt.Freeze();bipolarJets.Transform=tilt;
            var jet=FrozenPath("M -2.1,-3 Q -1.0,-23 0,-46 Q 1.0,-23 2.1,-3 Z");
            using(var dc=bipolarJets.Open()){
                foreach(int sign in new[]{1,-1}){
                    var turn=new ScaleTransform(1,sign);turn.Freeze();dc.PushTransform(turn);
                    var tail=sign==1?Color.FromRgb(163,211,255):Color.FromRgb(255,181,141);
                    var beam=new LinearGradientBrush(A(tail,0),Colors.White,90);
                    beam.GradientStops.Add(new GradientStop(A(tail,.66),.52));beam.Freeze();
                    dc.DrawGeometry(beam,null,jet);dc.Pop();
                }
            }
        }

        static double LimbExposure(double rate)
        {
            return .68*UiMotion.Unit(Math.Log(1+rate/4096.0)/Math.Log(480));
        }

        void SetDiskExposure(double up,double down)
        {
            if(up==uploadExposure && down==downloadExposure)return;
            uploadExposure=up;downloadExposure=down;
            SyncDiskExposure();
        }

        void SyncDiskExposure()
        {
            bool motion=IsLoaded && !viewport.IsEmpty && !highContrast && SystemParameters.ClientAreaAnimation &&
                (RenderCapability.Tier>>16)>=2 && RenderOptions.ProcessRenderMode!=System.Windows.Interop.RenderMode.SoftwareOnly;
            if(motion && !running){
                // Counters can continue while a visible, unfocused window is paused.
                Hold(uploadLimb,DrawingGroup.OpacityProperty);Hold(downloadLimb,DrawingGroup.OpacityProperty);
                return;
            }
            PaintLimb(uploadLimb,uploadExposure,motion);
            PaintLimb(downloadLimb,downloadExposure,motion);
        }

        void PaintLimb(DrawingGroup layer,double target,bool motion)
        {
            double from=layer.Opacity;
            var property=DrawingGroup.OpacityProperty;
            layer.Opacity=motion?from:target;
            layer.BeginAnimation(property,null);
            if(!motion || from==target)return;
            // Keep the base at the visible value until WPF samples the replacement clock.
            var fade=UiMotion.Ease(from,target,target>from?760:960,refreshRate,.35,0,.35,1);
            fade.FillBehavior=FillBehavior.HoldEnd;
            layer.BeginAnimation(property,fade);
        }

        internal void SetDiskTraffic(double? upload,double? download)
        {
            bool animate=running && Live && !highContrast && SystemParameters.ClientAreaAnimation && !viewport.IsEmpty;
            if(!Live || !upload.HasValue || !download.HasValue || !UiMotion.IsFinite(upload.Value) ||
                !UiMotion.IsFinite(download.Value) || upload.Value<0 || download.Value<0){
                if(!Live)ResetDiskTraffic();
                else{
                    lastUpload=lastDownload=-1;
                    SetDiskExposure(0,0);
                }
                return;
            }
            double up=upload.Value,down=download.Value;
            SetDiskExposure(LimbExposure(up),LimbExposure(down));
            if(lastUpload>=0 && animate){
                double u=Burst(up,lastUpload,meanUpload),d=Burst(down,lastDownload,meanDownload);
                if(Math.Max(u,d)>0)TriggerDiskFlare(u>=d,null);
            }
            meanUpload=lastUpload<0?up:.76*meanUpload+.24*up;
            meanDownload=lastDownload<0?down:.76*meanDownload+.24*down;
            lastUpload=up;lastDownload=down;
        }

        static double Burst(double value,double previous,double baseline)
        {
            double gain=value-Math.Max(previous,baseline);
            return gain>=96*1024 && value>=2.25*Math.Max(previous,baseline)?gain:0;
        }

        void ObserveAppBurst(IReadOnlyDictionary<string,double> sample)
        {
            if(!Live || !CanMoveMatter || lastUpload>=0 || sample==null)return;
            string source=null;double strongest=0;
            foreach(var pair in sample){
                if(!slots.ContainsKey(pair.Key) || !UiMotion.IsFinite(pair.Value) || pair.Value<0 || !appRates.TryGetValue(pair.Key,out double previous))continue;
                double gain=Burst(pair.Value,previous,previous);
                if(gain>strongest){strongest=gain;source=pair.Key;}
            }
            if(source==null)return;
            var anchor=Anchors[slots[source]];var channel=CapturePath(source,Project(anchor.X,anchor.Y));
            var local=(channel.Entry-channel.Gate)/channel.Scale;
            TriggerDiskFlare(null,new Point(local.X,local.Y));
        }

        void TriggerDiskFlare(bool? upload,Point? localImpact)
        {
            if(localImpact.HasValue && riverProgress<1)return;
            double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            if(now-lastFlare<2.6)return;
            lastFlare=now;neutralFlare=!upload.HasValue;ShowDiskFlare(upload,localImpact);
        }

        void ResetDiskTraffic()
        {
            lastUpload=lastDownload=-1;lastFlare=Double.NegativeInfinity;neutralFlare=false;
            SetDiskExposure(0,0);
        }

        void StopDiskFlare()
        {
            diskFlare.BeginAnimation(DrawingGroup.OpacityProperty,null);diskFlare.Opacity=0;
            bipolarJets.BeginAnimation(DrawingGroup.OpacityProperty,null);bipolarJets.Opacity=0;
            flareRise.BeginAnimation(ScaleTransform.ScaleYProperty,null);
        }

        static readonly Geometry plumeSkin=FrozenPath("M-7,1 C1,-6 8,-20 3,-38 C11,-26 10,-15 4,-7 C10,-10 13,-14 13,-18 C13,-8 6,-2 1,1 Z");
        static readonly Geometry plumeCore=FrozenPath("M-3,1 C1,-4 6,-13 6,-24 C9,-11 5,-4 2,0 Z");
        static Geometry FrozenPath(string path){var shape=Geometry.Parse(path);shape.Freeze();return shape;}
        void ShowDiskFlare(bool? upload,Point? localImpact)
        {
            double side=upload==true?1:-1;
            var hue=!upload.HasValue?Color.FromRgb(255,211,153):upload.Value?Color.FromRgb(112,205,255):Color.FromRgb(255,120,82);
            var trail=new LinearGradientBrush(A(hue,0),A(hue,.90),90);
            trail.GradientStops.Insert(1,new GradientStop(A(hue,.38),.38));trail.Freeze();
            using(var dc=flarePlume.Open()){
                dc.DrawEllipse(Glow(A(hue,.94)),null,new Point(),16,6.5);
                dc.DrawGeometry(trail,null,plumeSkin);dc.DrawGeometry(Ink(hue,.60),null,plumeCore);
                dc.DrawEllipse(Glow(Colors.White),null,new Point(),6,2);
            }
            var center=localImpact??new Point(side*40,4-side*7.8);
            var place=new MatrixTransform(side,0,0,1,center.X,center.Y);place.Freeze();
            diskFlare.Transform=place;
            var pulse=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(760),FillBehavior=FillBehavior.Stop};
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            pulse.KeyFrames.Add(new SplineDoubleKeyFrame(.96,TimeSpan.FromMilliseconds(134),new KeySpline(.18,0,.24,1)));
            pulse.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(760),new KeySpline(.20,0,.4,1)));
            flareRise.ScaleY=1;flareRise.BeginAnimation(ScaleTransform.ScaleYProperty,UiMotion.Ease(.12,1,730,refreshRate));
            diskFlare.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(pulse,refreshRate));
            var jets=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(600),FillBehavior=FillBehavior.Stop};
            jets.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            jets.KeyFrames.Add(new SplineDoubleKeyFrame(.22,TimeSpan.FromMilliseconds(120),new KeySpline(.20,0,.3,1)));
            jets.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(600),new KeySpline(.20,0,.4,1)));
            bipolarJets.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(jets,refreshRate));
        }
    }
}
