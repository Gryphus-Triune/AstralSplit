using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly ContainerVisual constellations=new ContainerVisual();
        readonly DrawingGroup frostSky=new DrawingGroup(), frostNear=new DrawingGroup(), frostBase=new DrawingGroup(),
            frostMist=new DrawingGroup(), frostLower=new DrawingGroup(), frostFar=new DrawingGroup(), frostMiddle=new DrawingGroup(),
            frostBand=new DrawingGroup(), frostBandBase=new DrawingGroup(), frostSweep=new DrawingGroup(), skyBase=new DrawingGroup(), skyAura=new DrawingGroup();
        readonly DrawingGroup[] frostTints={new DrawingGroup(),new DrawingGroup(),new DrawingGroup(),new DrawingGroup()};
        readonly List<DrawingGroup> frostStars=new List<DrawingGroup>();
        internal Drawing FrostSky { get { return frostSky; } }
        readonly DrawingGroup controlSky=new DrawingGroup();
        readonly DrawingGroup[] controlStates={new DrawingGroup(),new DrawingGroup(),new DrawingGroup(),new DrawingGroup()};
        readonly OpticalMap[] controlMaps=new OpticalMap[4];
        int controlVariant,controlRevision;
        bool blendingControls;
        internal OpticalMap ControlMap {get{return blendingControls || fadingSky || !FrostReady?null:controlMaps[controlVariant];}}
        internal Drawing ControlSky { get { return !fadingSky && FrostReady?controlSky:frostSky; } }
        internal Point SamplingOffset { get { return new Point(middle.Slide.X,middle.Slide.Y); } }
        internal event EventHandler SamplingChanged;
        readonly DrawingGroup rimStarlight=new DrawingGroup();
        internal Drawing RimStarlight { get { return rimStarlight; } }
        readonly List<ContainerVisual> lightViews=new List<ContainerVisual>();
        readonly Dictionary<ContainerVisual,SkyAtlas> lightAtlases=new Dictionary<ContainerVisual,SkyAtlas>();
        DispatcherTimer frostResize;
        ImageDrawing lastFrostBase;
        bool temporaryFrost;
        DispatcherOperation frostUpdate;
        SkyAtlas bakedAtlas;
        Size bakedSize;
        double bakedHeader,bakedScale;
        bool bakedDark,frostBaked;
        BitmapSource bakedStellarImage;
        ImageDrawing[] bakedSkyLayers;
        Action cancelObsoleteFrost;
        double FrostScale { get { return ArtworkResolution.PixelsPerDip(this,ActualWidth+164,Math.Max(ActualHeight+64,SkyAtlas.StarHeight+164)); } }
        internal bool FrostReady {get{return !observing && frostBaked && bakedStellarImage==stellarImage && bakedAtlas==atlas && bakedSize==RenderSize && bakedDark==dark && bakedHeader==headerHeight && bakedScale==FrostScale;}}
        static readonly BlurEffect glassBlur=CreateBlur();
        static BlurEffect CreateBlur()
        {
            var blur=new BlurEffect {Radius=FrostedPane.BlurRadius,KernelType=KernelType.Gaussian,RenderingBias=RenderingBias.Quality};
            blur.Freeze();return blur;
        }
        void InitializeFrost()
        {
            foreach(var color in controlStates)controlSky.Children.Add(color);
            using(var dc=backdrop.Art.Open()) {dc.DrawDrawing(skyBase);dc.DrawDrawing(stellarTransition);dc.DrawDrawing(skyAura);}
            frostMist.Transform=atmosphere.Slide; frostFar.Transform=distant.Slide; frostMiddle.Transform=middle.Slide;
            frostLower.Transform=lower.Slide;
            rimStarlight.Transform=lower.Slide;
            frostBand.Children.Add(frostBandBase);
            foreach(var color in frostTints) frostBand.Children.Add(color);
            foreach(var layer in new[]{frostBase,stellarTransition,skyAura,frostMist,frostLower,volume.Art,frostFar,frostMiddle})
                frostSky.Children.Add(layer);
        }
        internal void AttachLight(ContainerVisual viewport)
        {
            if(lightViews.Contains(viewport))return;
            var ribbonView=new DrawingVisual {Transform=new TransformGroup {Children={drift,galaxy.Slide}},CacheMode=new BitmapCache(ArtworkResolution.Ratio)};
            using(var dc=ribbonView.RenderOpen())dc.DrawDrawing(frostBand);
            var scanView=new DrawingVisual {Transform=sweep.Transform,CacheMode=new BitmapCache(ArtworkResolution.Ratio)};
            using(var dc=scanView.RenderOpen())dc.DrawDrawing(frostSweep);
            var nearby=new ContainerVisual {Transform=close.Slide};
            viewport.Children.Add(ribbonView);viewport.Children.Add(scanView);viewport.Children.Add(nearby);
            lightViews.Add(viewport);
            RefreshGlassStars(nearby);
        }
        internal void DetachLight(ContainerVisual viewport)
        {
            lightViews.Remove(viewport);
            if(viewport.Children.Count>2)lightAtlases.Remove((ContainerVisual)viewport.Children[2]);
            viewport.Children.Clear();
        }
        void RefreshGlassStars(ContainerVisual container)
        {
            if(atlas==null)return;
            if(!lightAtlases.TryGetValue(container,out var previous) || previous!=atlas){
                container.Children.Clear();lightAtlases[container]=atlas;
                foreach(var star in frostNear.Children){
                    var image=new DrawingVisual();using(var dc=image.RenderOpen())dc.DrawDrawing(star);
                    container.Children.Add(image);
                }
            }
            for(int i=0;i<container.Children.Count;i++){
                var pt=atlas.Near[i].Point;var lamp=(DrawingVisual)container.Children[i];
                double opacity=pt.X<=ActualWidth+NearTravel+12 && pt.Y<=ActualHeight+NearTravel+12?1:0;
                if(lamp.Opacity!=opacity)lamp.Opacity=opacity;
            }
        }
        void DelayFrostResize(){
            if(disposed || (!frostBaked && cancelObsoleteFrost==null))return;
            if(frostResize==null){
                frostResize=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMilliseconds(125)};
                frostResize.Tick+=(s,e)=>{frostResize.Stop();QueueFrost();};
            }
            frostResize.Stop();frostResize.Start();cancelObsoleteFrost?.Invoke();
        }
        void StopFrostResize(){if(frostResize!=null)frostResize.Stop();}
        void PlaceFrostBase(){
            if(FrostReady){
                if(temporaryFrost && lastFrostBase!=null)using(var dc=frostBase.Open())dc.DrawDrawing(lastFrostBase);
                temporaryFrost=false;return;
            }
            temporaryFrost=true;
            using(var dc=frostBase.Open()){
                if(lastFrostBase!=null && bakedStellarImage==stellarImage && bakedDark==dark && bakedSize.Width>0 && bakedSize.Height>0){
                    dc.PushTransform(new ScaleTransform(ActualWidth/bakedSize.Width,ActualHeight/bakedSize.Height));
                    dc.DrawDrawing(lastFrostBase);dc.Pop();
                }else dc.DrawDrawing(skyBase);
            }
        }
        void QueueFrost()
        {
            if(cancelObsoleteFrost!=null){cancelObsoleteFrost();return;}
            if(frostResize!=null && frostResize.IsEnabled)return;
            if(disposed || observing || !observationMonitor.IsEmpty || highContrast || ChartIsExpandingOrClosing || ActualWidth<1 || ActualHeight<1 || frostUpdate!=null || FrostReady) return;
            if(atlas==null){
                using(var dc=frostBase.Open())dc.DrawDrawing(skyBase);
                using(var dc=frostMist.Open())dc.DrawDrawing(atmosphere.Art);
                return;
            }
            if(building)return;
            frostUpdate=Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{
                frostUpdate=null;
                if(disposed || observing || !observationMonitor.IsEmpty || highContrast || ChartIsExpandingOrClosing || ActualWidth<1 || ActualHeight<1 ||
                    (frostResize!=null && frostResize.IsEnabled)) return;
                var size=RenderSize;
                if(FrostReady) return;
                bool theme=dark;double header=headerHeight,scale=FrostScale;var currentAtlas=atlas;var artwork=stellarImage;
                var reusable=bakedAtlas==currentAtlas && bakedSize==size && bakedScale==scale && bakedHeader==header && bakedDark==theme ? bakedSkyLayers : null;
                var baseArt=Frozen(skyBase);var mistArt=Frozen(atmosphere.Art);var scanArt=Frozen(sweep.Art);
                var cancellation=new CancellationTokenSource();
                bool Current() { return !disposed && !highContrast && observationMonitor.IsEmpty && size==RenderSize && theme==dark &&
                    header==headerHeight && artwork==stellarImage && currentAtlas==atlas && scale==FrostScale; }
                cancelObsoleteFrost=()=>{if(!Current())cancellation.Cancel();};
                var thread=new Thread(()=>{
                    ImageDrawing[] images=null;var maps=new OpticalMap[4];
                    try {
                        images=BakeFrost(size,header,currentAtlas,theme,baseArt,mistArt,scanArt,scale,cancellation.Token,reusable);
                        for(int i=0;i<maps.Length;i++)if(images[11+i]!=null)maps[i]=new OpticalMap((BitmapSource)images[11+i].ImageSource);
                    }
                    catch(Exception ex) when(ex is OutOfMemoryException || ex is COMException || ex is InvalidOperationException || ex is OperationCanceledException) { images=null; }
                    finally {Dispatcher.CurrentDispatcher.InvokeShutdown();}
                    if(Dispatcher.HasShutdownStarted)return;
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
                        cancelObsoleteFrost=null;
                        bool obsolete=cancellation.IsCancellationRequested || !Current();
                        cancellation.Dispose();
                        if(disposed || highContrast)return;
                        if(obsolete){QueueFrost();return;}
                        if(images==null)return;
                        var targets=new[]{frostBase,frostMist,frostFar,frostMiddle,frostLower,frostBandBase,
                            frostTints[0],frostTints[1],frostTints[2],frostTints[3],frostSweep,
                            controlStates[0],controlStates[1],controlStates[2],controlStates[3]};
                        for(int i=0;i<images.Length;i++) using(var dc=targets[i].Open()) if(images[i]!=null)dc.DrawDrawing(images[i]);
                        Array.Copy(maps,controlMaps,maps.Length);
                        lastFrostBase=images[0];temporaryFrost=false;
                        bakedSkyLayers=new ImageDrawing[11];Array.Copy(images,1,bakedSkyLayers,1,10);
                        bakedStellarImage=artwork;bakedSize=size;bakedHeader=header;bakedScale=scale;bakedDark=theme;bakedAtlas=currentAtlas;frostBaked=true;
                        UpdateSkyClip();
                        SamplingChanged?.Invoke(this,EventArgs.Empty);
                    }));
                }){ IsBackground=true,Name="AstralSplit glass artwork" };
                thread.SetApartmentState(ApartmentState.STA);thread.Start();
            }));
        }
        static DrawingGroup Frozen(DrawingGroup drawing)
        {
            var copy=drawing.CloneCurrentValue();copy.Freeze();return copy;
        }
        // Gaussian convolution is translation invariant. Bake the real artwork at its own origin once,
        // then share the sharp scene's transforms and clocks. Pointer motion never reads pixels back.
        static ImageDrawing Bake(Rect area,double scale,Action<DrawingContext> draw,bool diffuse,ref byte[] pixels)
        {
            var origin=new DrawingVisual(); var content=new DrawingVisual(); origin.Children.Add(content);
            if(diffuse)content.Effect=glassBlur;
            using(var dc=content.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-area.X,-area.Y));draw(dc);dc.Pop();
            }
            int w=Math.Max(1,(int)Math.Ceiling(area.Width*scale)),h=Math.Max(1,(int)Math.Ceiling(area.Height*scale));
            var bitmap=new RenderTargetBitmap(w,h,96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(origin);
            int bytes=checked(w*h*4);
            if(pixels==null || pixels.Length<bytes)pixels=new byte[bytes];
            bitmap.CopyPixels(pixels,w*4,0);
            var texture=BitmapSource.Create(w,h,96*scale,96*scale,PixelFormats.Pbgra32,null,pixels,w*4);
            texture.Freeze();
            var image=new ImageDrawing(texture,new Rect(area.X,area.Y,w/scale,h/scale));image.Freeze();return image;
        }
        static ImageDrawing[] BakeFrost(Size size,double header,SkyAtlas atlas,bool night,DrawingGroup basis,DrawingGroup mist,DrawingGroup scan,double scale,CancellationToken cancellation,ImageDrawing[] reusable)
        {
            // BitmapSource.Create copies its pixels. All fifteen immutable layers can share the transfer buffer.
            byte[] transfer=null;
            ImageDrawing Layer(Rect area,Action<DrawingContext> draw,bool diffuse=true){
                cancellation.ThrowIfCancellationRequested();
                return Bake(area,scale,draw,diffuse,ref transfer);
            }
            double w=size.Width,h=size.Height;var result=new ImageDrawing[15];
            var body=new Rect(-32,-32,w+64,h+64);
            var top=new Rect(-82,-82,w+164,Math.Min(h+164,Math.Max(header+160,SkyAtlas.StarHeight+164)));
            result[0]=Layer(body,dc=>dc.DrawDrawing(basis));
            // A palette swap only changes the stellar photograph; every fixed-size optical layer is reusable.
            if(reusable!=null)Array.Copy(reusable,1,result,1,10);
            else {
                result[1]=Layer(body,dc=>dc.DrawDrawing(mist));
                var field=new Rect(-SkyAtlas.Padding,-SkyAtlas.Padding,atlas.Width+2*SkyAtlas.Padding,atlas.Far.Height);
                result[2]=Layer(top,dc=>dc.DrawImage(atlas.Far,field));result[3]=Layer(top,dc=>dc.DrawImage(atlas.Middle,field));
                result[4]=night?atlas.NightTransmission:atlas.DayField;
                result[5]=Layer(top,dc=>DrawBand(dc,w,atlas.Galaxy));
                for(int i=0;i<atlas.TintImages.Length;i++)
                {
                    var image=atlas.TintImages[i]; result[6+i]=Layer(top,dc=>DrawBand(dc,w,image));
                }
                var scanBounds=new Rect(Math.Max(260,w-86)-116,-9,232,200);
                result[10]=Layer(scanBounds,dc=>dc.DrawDrawing(scan));
            }
            // Small apertures use one precomposed transmitted texture per state. Panning a sample
            // changes only its texture coordinates, so each button does not draw every sky layer.
            for(int i=0;i<4;i++)
            {
                double stars=i==2?0.65:i==1?0.42:i==3?0.36:0.30,band=i==2?0.075:i==1?0.055:i==3?0.04:0.03;
                int tint=i;
                result[11+i]=Layer(new Rect(size),dc=>{
                    dc.DrawDrawing(result[0]);dc.DrawDrawing(result[1]);
                    dc.PushOpacity(stars);dc.DrawDrawing(result[4]);dc.Pop();
                    dc.PushOpacity(band);dc.DrawDrawing(result[5]);dc.DrawDrawing(result[6+tint]);dc.Pop();
                    dc.PushOpacity(stars);dc.DrawDrawing(result[2]);dc.DrawDrawing(result[3]);dc.Pop();
                },false);
            }
            return result;
        }
        static void BandTransform(DrawingContext dc,double w)
        {
            dc.PushTransform(new TranslateTransform(Math.Max(260,w-86),91)); dc.PushTransform(new RotateTransform(-22));
            dc.PushTransform(new ScaleTransform(Math.Min(1600,Math.Max(1060,w*1.4)),260));
        }
        static void DrawBand(DrawingContext dc,double w,ImageSource image)
        {
            BandTransform(dc,w);dc.DrawImage(image,BandBounds); dc.Pop();dc.Pop();dc.Pop();
        }
        void FrostStar(SkyAtlas.NearStar star)
        {
            // A near star is much smaller than the frost kernel. Its integrated light becomes a soft spot.
            var light=new DrawingGroup();
            var center=star.Color;center.A=(byte)(star.Radius*35);
            using(var dc=light.Open()) dc.DrawEllipse(Mist(center.A,center.R,center.G,center.B),null,star.Point,5.5,5.5);
            var intensity=new DrawingGroup();intensity.Children.Add(light);
            frostNear.Children.Add(intensity);frostStars.Add(light);
        }
        void TintFrost(int variant,double stars,double band,bool motion)
        {
            for(int i=0;i<frostTints.Length;i++) OpacityTo(frostTints[i],i==variant?1:0,900,motion,refreshRate);
            if(variant!=controlVariant || !motion)
            {
                int revision=++controlRevision;controlVariant=variant;
                blendingControls=motion;SamplingChanged?.Invoke(this,EventArgs.Empty);
                for(int i=0;i<controlStates.Length;i++){
                    var tone=controlStates[i];double from=tone.Opacity,to=i==variant?1:0;
                    tone.Opacity=to;
                    var fade=motion?UiMotion.Ease(from,to,600,refreshRate):null;
                    if(fade!=null && i==variant)fade.Completed+=(s,e)=>{
                        if(revision==controlRevision){blendingControls=false;SamplingChanged?.Invoke(this,EventArgs.Empty);}
                    };
                    tone.BeginAnimation(DrawingGroup.OpacityProperty,fade);
                }
            }
            foreach(var part in new[]{frostFar,frostMiddle}) OpacityTo(part,stars,600,motion,refreshRate);
            foreach(DrawingGroup star in frostNear.Children) OpacityTo(star,stars,600,motion,refreshRate);
            OpacityTo(frostLower,stars,600,motion,refreshRate);
            OpacityTo(rimStarlight,0.32*stars,600,motion,refreshRate);
            OpacityTo(frostBand,band,600,motion,refreshRate);
        }
    }
}
