using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        internal const int MaximumNamedStars = 16;
        readonly DrawingGroup stellarTransition = new DrawingGroup();
        Dictionary<BitmapSource,double> fadingImages;
        readonly DrawingGroup chart = new DrawingGroup(), markers = new DrawingGroup(), foreground = new DrawingGroup();
        readonly DrawingGroup connectionSignals = new DrawingGroup();
        readonly DrawingGroup interactionBounds = new DrawingGroup(), flight = new DrawingGroup();
        readonly Dictionary<string, BitmapSource> skies = new Dictionary<string, BitmapSource>();
        readonly LinkedList<string> recentSkies = new LinkedList<string>();
        readonly Dictionary<string, string> selectedNames = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> directNames = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Color> pigments = new Dictionary<string,Color>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> slots = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> appRates = new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
        internal bool MeasuredAppStreams { get; set; }
        readonly Dictionary<string, Point> positions = new Dictionary<string,Point>(StringComparer.OrdinalIgnoreCase);
        static readonly Point[] Anchors = {
            new Point(.114,.56), new Point(.324,.82), new Point(.519,.46),
            new Point(.550,.79), new Point(.271,.43), new Point(.044,.76),
            new Point(.668,.55), new Point(.395,.62), new Point(.174,.91),
            new Point(.606,.70), new Point(.078,.42), new Point(.708,.47),
            new Point(.467,.87), new Point(.236,.63), new Point(.372,.94), new Point(.680,.89)
        };
        static readonly Color[] Starlight = {Color.FromRgb(118,211,242), Color.FromRgb(177,169,255),
            Color.FromRgb(135,225,178), Color.FromRgb(240,175,216), Color.FromRgb(236,195,152)};
        static readonly Typeface StarTypeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal,FontWeights.Medium,FontStretches.Normal);
        string stellarPhase = "stopped", requestedSky;
        internal string returningDirect;
        Rect viewport = Rect.Empty;
        Geometry coreAperture;
        sealed class ChartMarker
        {
            internal string Path;
            internal Point Anchor,Position;
            internal RiverTrace River;
            internal bool Selected;
        }
        readonly List<ChartMarker> markerLayout=new List<ChartMarker>();
        readonly TranslateTransform gateCaptionPosition=new TranslateTransform();
        double chartExpansion;
        bool ChartIsExpandingOrClosing {get{return chartExpansion>0 && chartExpansion<1;}}
        double SurroundingStarScale {get{return CoreScale*(1/CoreMagnification+(1-1/CoreMagnification)*chartExpansion);}}
        Size overlaySize;
        double tunnelRate = -1;
        int artworkRequest, skyFadeRevision;
        bool chartDirty, chartQueued, fadingSky;
        BitmapSource stellarImage;
        internal DrawingGroup InteractionDrawing { get { return foreground; } }
        internal readonly DrawingGroup OpticalEmission=new DrawingGroup();
        readonly DrawingGroup emittingStars=new DrawingGroup(),rearAppSources=new DrawingGroup();

        void InitializeStellar()
        {
            InitializeObservatory();
            InitializeCore();
            OpticalEmission.Children.Add(emittingStars);
            var guides = new DrawingVisual();
            using(var d=guides.RenderOpen()){d.DrawDrawing(observingSky);d.DrawDrawing(chart);}
            var stars = new DrawingVisual();
            rearTransit.Children.Add(streamBack);
            using(var d=stars.RenderOpen()) {
                d.DrawDrawing(markers); d.DrawDrawing(connectionSignals);
                d.DrawDrawing(landingRings); d.DrawDrawing(coreFrame); d.DrawDrawing(observingNearApproaches); d.DrawDrawing(streamFront); d.DrawDrawing(infallLayer);
            }
            // Both emission layers inherit the exact transforms of their source. The near disk,
            // the opaque shadow and the seed are drawn in front of the emitted light.
            var coronaPlacement=new ContainerVisual { Transform=corePlacement };
            var seedTransform=new TransformGroup();
            seedTransform.Children.Add(starScale);seedTransform.Children.Add(standbyScale);
            stellarCoronaFilm.Transform=seedTransform;
            diamondFilm.Transform=holeScale;
            coronaPlacement.Children.Add(stellarCoronaFilm);coronaPlacement.Children.Add(diamondFilm);
            constellations.Children.Add(guides);
            CoronaArtwork.Children.Add(coronaPlacement);constellations.Children.Add(CoronaArtwork);
            constellations.Children.Add(stars);
            constellations.Transform=mapShift;
            emittingStars.Transform=mapShift;
            var moving=new DrawingGroup{Transform=mapShift};
            moving.Children.Add(novaOrigin);moving.Children.Add(outsideFormation);moving.Children.Add(flight);
            foreground.Children.Add(interactionBounds);foreground.Children.Add(moving);foreground.Children.Add(survey);
            Milestone.Transform=remnantShift;
        }

        void SetInteractionBounds(double w,double h)
        {
            var next = new Size(w,h);
            if(next == overlaySize)return;
            overlaySize=next;
            using(var d=interactionBounds.Open())d.DrawRectangle(Brushes.Transparent,null,new Rect(next));
            var clip = new RectangleGeometry(new Rect(next)); clip.Freeze(); foreground.ClipGeometry = clip;
            UpdateFormationClip();
        }

        internal void SetStellarPhase(string phase)
        {
            if(String.IsNullOrEmpty(phase)||phase==stellarPhase)return;
            bool wasLive = Live;
            stellarPhase=phase;
            if(wasLive!=Live && (closingGesture>0 || gestureMass>0)){
                // The next phase starts at the visible field under the switch.
                ReleaseGestureForPhase();
            }
            if (wasLive != Live) {
                SettleRivers(false);
                if(!Live){DrainAccretion();ResetDiskTraffic();appRates.Clear();ClearConnections();}
                else {ClearLights();SettleInfall();}
                FormCore(true);
            }
            else SyncStandby();
            LayoutStarLens();EnsureStellarArt();QueueChart();
        }

        string SkyKey { get {
            switch(stellarPhase){
                case "active":return "eclipse";
                case "starting":return "dawn";
                case "restoring":return "amber";
                case "waiting":case "unknown":return "mist";
                default:return "night";
            }
        }}

        async void EnsureStellarArt()
        {
            if(disposed||highContrast)return;
            var key=SkyKey;
            if(key==requestedSky)return;
            requestedSky=key;
            int revision=++artworkRequest;
            BitmapSource texture;
            if(!skies.TryGetValue(key,out texture))
            {
                try { texture=await Task.Run(()=>StellarSkyArt.Load(key)).ConfigureAwait(false); }
                catch(Exception ex) when(ex is System.IO.IOException || ex is NotSupportedException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.COMException) {
                    if(!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(()=>{if(revision==artworkRequest)requestedSky=null;});
                    return;
                }
                if(Dispatcher.HasShutdownStarted)return;
                try {await Dispatcher.InvokeAsync(()=>{if(!disposed && revision==artworkRequest)InstallSky(key,texture);},DispatcherPriority.Render);}
                catch(OperationCanceledException) { }
                return;
            }
            InstallSky(key,texture);
        }

        void InstallSky(string key,BitmapSource texture)
        {
            skies[key]=texture;recentSkies.Remove(key); recentSkies.AddFirst(key);
            if(recentSkies.Count>3){skies.Remove(recentSkies.Last.Value);recentSkies.RemoveLast();}
            var prior=stellarImage;
            bool blend=running&&prior!=null&&UiMotion.CanAnimate(this)&&ActualWidth>0&&ActualHeight>0;
            Dictionary<BitmapSource,double> previous=null;
            if(blend){
                previous=new Dictionary<BitmapSource,double>();
                double carry=fadingSky?stellarTransition.Opacity:0;
                if(fadingImages!=null)foreach(var item in fadingImages){
                    double contribution=item.Value*carry;
                    if(contribution>.0001)previous[item.Key]=contribution;
                }
                previous.TryGetValue(prior,out double existing);
                previous[prior]=existing+1-carry;
            }
            StopSkyFade();
            if(blend){
                fadingSky=true;fadingImages=previous;stellarTransition.Opacity=1;
            }
            stellarImage=texture;DrawSurface();LayoutStarLens();
            using(var d=frostBase.Open())d.DrawDrawing(skyBase);
            if(blend){
                int revision=++skyFadeRevision;
                var fade=UiMotion.Ease(1,0,350,refreshRate);
                fade.FillBehavior=FillBehavior.HoldEnd;
                fade.Completed+=(s,e)=>{if(revision==skyFadeRevision){StopSkyFade();SamplingChanged?.Invoke(this,EventArgs.Empty);}};
                stellarTransition.BeginAnimation(DrawingGroup.OpacityProperty,fade);
            }
            SamplingChanged?.Invoke(this,EventArgs.Empty);
        }

        void DrawSkyTransition()
        {
            if(!fadingSky)return;
            // Every blended photograph uses today's layout, including while the
            // chart expands under a fade. An interrupted fade keeps its visible mix.
            using(var dc=stellarTransition.Open()){
                double total=0;
                foreach(var image in fadingImages){
                    if(image.Value<=0)continue;
                    total+=image.Value;dc.PushOpacity(image.Value/total);
                    PaintObservedSky(dc,image.Key);
                    dc.Pop();
                }
            }
        }

        void StopSkyFade()
        {
            ++skyFadeRevision;fadingSky=false;fadingImages=null;
            stellarTransition.BeginAnimation(DrawingGroup.OpacityProperty,null);
            stellarTransition.Children.Clear();stellarTransition.Opacity=0;
        }

        internal void SetStellarViewport(Rect bounds,double scale,double expansion,double expandedHeight)
        {
            if(bounds.IsEmpty || bounds.Width<160 || bounds.Height<30 || scale<=0){bounds=Rect.Empty;scale=0;}
            bool captionBelow=expandedHeight>=2*OuterRingRadius*scale+18;
            if(bounds==viewport && CoreScale==scale && chartExpansion==expansion && CaptionBelow==captionBelow)return;
            CaptionBelow=captionBelow;
            bool wasMoving=ChartIsExpandingOrClosing;
            bool chartSlide=!viewport.IsEmpty && !bounds.IsEmpty && bounds.TopLeft==viewport.TopLeft &&
                bounds.Width==viewport.Width && CoreScale==scale && (expansion!=chartExpansion || Math.Abs(bounds.Height-viewport.Height)<1.1);
            double oldBottom=viewport.Bottom;
            viewport=bounds;CoreScale=scale;chartExpansion=expansion;
            Milestone.SetChart(bounds,MilestoneCanvas);
            if(lastPointer.HasValue)Aim(lastPointer.Value);
            // When expanded details take vertical space, only the outer optical arcs can extend
            // behind the glass. Incoming grains on either side of the chart share this aperture.
            coreAperture=Geometry.Empty;
            if(!bounds.IsEmpty){
                var gate=GateCenter;
                var rim=new Rect(gate.X-OuterRingRadius*scale-2,gate.Y-OuterRingRadius*scale-2,
                    2*OuterRingRadius*scale+4,2*OuterRingRadius*scale+4);
                var chartBounds=new RectangleGeometry(bounds);
                coreAperture=bounds.Contains(rim)?(Geometry)chartBounds:new CombinedGeometry(GeometryCombineMode.Union,chartBounds,new RectangleGeometry(rim));
                coreAperture.Freeze();
            }
            coreFrame.ClipGeometry=riverBack.ClipGeometry=rearTransit.ClipGeometry=riverFront.ClipGeometry=streamFront.ClipGeometry=infallLayer.ClipGeometry=coreAperture;
            if(!bounds.IsEmpty)PositionCore();
            LayoutStarLens();UpdateFormationClip();
            if(wasMoving && !ChartIsExpandingOrClosing)QueueFrost();
            if(viewport.IsEmpty){if(formationRunning)FinishFormation();SettleRivers(Live);}
            if(observing && !viewport.IsEmpty && !highContrast){
                PlaceCore();
                if(!observationCameraMoving && CanMoveMatter && Live && riverProgress>=1)UpdateTrafficLights();
                else DrawObservingSky(flightClock.Elapsed.TotalSeconds);
                return;
            }
            if(chartSlide){
                foreach(var pair in appFlights){
                    if(pair.Value.ToStar && slots.TryGetValue(pair.Key,out int slot))
                        pair.Value.FollowChart(Project(Anchors[slot].X,Anchors[slot].Y),0);
                    else if(!pair.Value.ToStar)pair.Value.FollowChart(null,viewport.Bottom-oldBottom);
                }
            }else SettleFlights();
            ClearConnections();
            SyncCoreOrbit();
            if(viewport.IsEmpty){ClearChart();LayoutStarLens();return;}
            if(chartSlide && ChartIsExpandingOrClosing && !chartDirty && !highContrast){ArrangeChart();return;}
            QueueChart();
        }

        internal void SetStarColor(string path,Color color)
        {
            if(String.IsNullOrWhiteSpace(path))return;
            // Keep an icon's hue but lift nearly black artwork into the night-sky luminance range.
            var max=Math.Max(color.R,Math.Max(color.G,color.B));
            double mix=max<190?(190.0-max)/(255-max):.08;
            Color value=Color.FromRgb((byte)(color.R+(255-color.R)*mix),(byte)(color.G+(255-color.G)*mix),(byte)(color.B+(255-color.B)*mix));
            Color prior;if(pigments.TryGetValue(path,out prior)&&prior==value)return;
            if(pigments.Count>1024)foreach(var key in pigments.Keys.Where(p=>!selectedNames.ContainsKey(p)&&!directNames.ContainsKey(p)).Take(512).ToArray())pigments.Remove(key);
            pigments[path]=value;
            if (appFlights.TryGetValue(path, out var flying)) flying.SetColor(value);
            if(selectedNames.ContainsKey(path))QueueChart();
        }

        internal void SetApplications(IEnumerable<string> selection)
        {
            var names=(selection??Enumerable.Empty<string>()).Where(p=>!String.IsNullOrWhiteSpace(p))
                .Select(p=>new KeyValuePair<string,string>(p,System.IO.Path.GetFileNameWithoutExtension(p)));
            SetApplications(names,null);
        }

        static Dictionary<string,string> Catalog(IEnumerable<KeyValuePair<string,string>> items)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(items!=null)foreach(var app in items)
                if(!String.IsNullOrWhiteSpace(app.Key))result[app.Key]=String.IsNullOrWhiteSpace(app.Value)?System.IO.Path.GetFileNameWithoutExtension(app.Key):app.Value;
            return result;
        }

        static bool Replace(Dictionary<string,string> current,Dictionary<string,string> next)
        {
            if(current.Count==next.Count && next.All(p=>current.TryGetValue(p.Key,out var name)&&name==p.Value))return false;
            current.Clear();foreach(var pair in next)current.Add(pair.Key,pair.Value);return true;
        }

        internal void SetApplications(IEnumerable<KeyValuePair<string,string>> selected,IEnumerable<KeyValuePair<string,string>> direct)
        {
            if(disposed)return;
            var selectedList=Catalog(selected);
            var chosen=selectedList.Keys.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(MaximumNamedStars).ToArray();
            var directList=Catalog(direct);
            var plain=directList.Keys.Where(p=>!selectedList.ContainsKey(p)).OrderByDescending(p=>String.Equals(p,returningDirect,StringComparison.OrdinalIgnoreCase)).ThenBy(PathSeed).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
            bool changed=Replace(selectedNames,chosen.ToDictionary(p=>p,p=>selectedList[p],StringComparer.OrdinalIgnoreCase));
            changed|=Replace(directNames,plain.ToDictionary(p=>p,p=>directList[p],StringComparer.OrdinalIgnoreCase));
            if(!changed)return;
            fallOnArrival.RemoveWhere(path=>!selectedNames.ContainsKey(path));
            foreach(var path in infalls.Keys.Concat(diskHits.Keys).Where(p=>!selectedNames.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()){
                if(!infalls.TryGetValue(path,out var fall) || !fall.Retracting)CancelInfall(path);
            }
            foreach(var path in channels.Keys.Where(p=>!selectedNames.ContainsKey(p)).ToArray())channels.Remove(path);
            foreach(var path in landings.Keys.Where(p=>!selectedNames.ContainsKey(p)).ToArray()) CancelLanding(path);
            foreach(var path in slots.Keys.Where(p=>!selectedNames.ContainsKey(p)).ToArray())slots.Remove(path);
            var used=new HashSet<int>(slots.Values);
            foreach(var path in chosen.Where(p=>!slots.ContainsKey(p)))
            {
                int index=0;while(used.Contains(index))index++;
                slots[path]=index;used.Add(index);
            }
            QueueChart();
        }

        internal void SetTraffic(double tunnelBytesPerSecond,double uplinkBytesPerSecond)
        {
            bool transmitting=tunnelRate>0;tunnelRate=tunnelBytesPerSecond;
            if(transmitting!=(tunnelRate>0)) OpacityTo(coreHalo,tunnelRate>0 ? 1 : .60,200,running,refreshRate);
        }

        internal void SetAppTraffic(IReadOnlyDictionary<string,double> rates)
        {
            ObserveAppBurst(rates);
            appRates.Clear();
            if(rates!=null)foreach(var pair in rates)
                if(pair.Value>=0&&UiMotion.IsFinite(pair.Value))appRates[pair.Key]=pair.Value;
            UpdateApplicationStarRates();
            UpdateTrafficLights();
        }

        void QueueChart()
        {
            chartDirty=true;
            if(disposed||chartQueued)return;
            chartQueued=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{
                chartQueued=false;if(!disposed&&chartDirty)DrawChart();
            }));
        }

        static Color A(Color color,double alpha){color.A=(byte)(255*UiMotion.Unit(alpha));return color;}
        static Brush Ink(Color color,double alpha=1){return SkyAtlas.Brush(A(color,alpha));}
        struct StrokeEntry {internal Pen Pen;internal Color Color;internal double Width;}
        [ThreadStatic] static StrokeEntry[] strokeCache;
        static Pen Stroke(Color color,double alpha,double width){
            if(strokeCache==null)strokeCache=new StrokeEntry[2048];
            color=A(color,alpha);
            int slot=(color.A*257+color.R*59+color.G*11+color.B+width.GetHashCode())&2047;
            var entry=strokeCache[slot];
            if(entry.Pen!=null && entry.Color==color && entry.Width==width)return entry.Pen;
            var pen=new Pen(SkyAtlas.Brush(color),width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            pen.Freeze();strokeCache[slot]=new StrokeEntry{Pen=pen,Color=color,Width=width};return pen;
        }
        static void Light(DrawingContext d,Point p,Color c,double size,double power)
        {
            d.DrawEllipse(Glow(A(c,power*.68)),null,p,size*7.5,size*7.5);
            d.DrawEllipse(Glow(A(c,power*.83)),null,p,size*3.5,size*3.5);
            var ray=Stroke(c,.61*power,.62);
            d.DrawLine(ray,new Point(p.X-size*2.9,p.Y),new Point(p.X+size*2.9,p.Y));
            d.DrawLine(ray,new Point(p.X,p.Y-size*2.3),new Point(p.X,p.Y+size*2.3));
            d.DrawEllipse(Ink(c,power),null,p,size,size);
            d.DrawEllipse(Ink(Colors.White,.92*power),null,p,size*.46,size*.46);
        }
        void Label(DrawingContext d,string value,Point p,double size,double width,double opacity)
        {
            if(width<19)return;
            string text=value.Length>100?value.Substring(0,100):value;
            var print=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,StarTypeface,
                size,Ink(Color.FromRgb(235,243,254),opacity),VisualTreeHelper.GetDpi(this).PixelsPerDip){
                MaxTextWidth=width,MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis};
            d.DrawText(print,p);
        }
        Point Project(double x,double y)
        {
            var gate=GateCenter;
            double starWidth=Math.Min(viewport.Width,(gate.X-viewport.X-AccretionPath.OuterPlate*CoreScale-12)/.75);
            starWidth=viewport.Width+(starWidth-viewport.Width)*chartExpansion;
            // Keep the named stars below the convergence point as its position follows the larger rim.
            double horizon=gate.Y-viewport.Y;
            double height=y>.405?horizon+(y-.405)/.595*(viewport.Height-horizon):y/.405*horizon;
            return new Point(viewport.X+x*starWidth,viewport.Y+height);
        }
        bool CaptionBelow {get;set;}
        Point? observationFocus;
        internal Point ObservationFocus {get{return GateCenter;}}
        internal double ViewportExpansion {get{return chartExpansion;}}
        bool observationCameraMoving;
        internal void SetObservationCamera(Point? focus,Vector offset,bool moving){
            bool restoreVector=moving && !observationCameraMoving && observing && observingRibbons.IsRasterized;
            observationFocus=focus;observationCameraMoving=moving;
            distant.Offset=middle.Offset=close.Offset=offset;
            if(restoreVector)DrawObservingSky(flightClock.Elapsed.TotalSeconds);
        }
        Point GateCenter {get{
            if(observationFocus.HasValue)return observationFocus.Value;
            double w=viewport.Width,h=viewport.Height;
            double restingX=Math.Min(.834*w,w-87*(CoreScale/CoreMagnification)-2);
            double expandedX=Math.Min(.834*w,w-87*CoreScale-2);
            double restingY=Math.Min(.405*h,(h-(h>72?16:0))*.5);
            double expandedY=(h-Math.Min(16,Math.Max(0,h-2*OuterRingRadius*CoreScale-2)))*.5;
            var normal=new Point(viewport.X+restingX+(expandedX-restingX)*chartExpansion,
                viewport.Y+restingY+(expandedY-restingY)*chartExpansion);
            var middle=new Point(viewport.Left+w*.50,viewport.Top+h*.49);return normal+(middle-normal)*observationBlend;
        }}
        Color Accent {get{return SkyKey=="amber"||SkyKey=="eclipse"||SkyKey=="dawn"?Color.FromRgb(255,203,139):
            SkyKey=="mist"?Color.FromRgb(185,199,220):Color.FromRgb(121,223,250);}}
        Color AppInk(string path){Color c;return pigments.TryGetValue(path,out c)?c:Starlight[(int)(PathSeed(path)%Starlight.Length)];}
        bool Live {get{return stellarPhase=="active";}}

        void ClearChart()
        {
            SettleApplicationStars();
            emittingStars.Children.Clear();rearAppSources.Children.Clear();
            markerLayout.Clear();chartDirty=false;ClearConnections();chart.Children.Clear();riverBack.Children.Clear();riverFront.Children.Clear();
            markers.Children.Clear();positions.Clear();ClearLights();
            ClearSurvey();deepSky.Children.Clear();cometFilm.Clear();pulsarFilm.Clear();stellarCoronaFilm.Clear();diamondFilm.Clear();ClearObservingStars();
            coreFrame.Opacity=0;
            StarMapChanged?.Invoke();
        }

        Point GateCaptionLocation()
        {
            var gate=GateCenter;
            var expanded=CaptionBelow?new Point(gate.X-31,Math.Min(viewport.Bottom-15,gate.Y+OuterRingRadius*CoreScale+1)):
                new Point(Math.Min(viewport.Right-66,gate.X+OuterRingRadius*CoreScale+3),Math.Min(viewport.Bottom-16,gate.Y+22*CoreScale));
            var resting=new Point(gate.X-31,gate.Y+40*CoreScale/CoreMagnification+1);
            return resting+(expanded-resting)*chartExpansion;
        }

        // Moving the list changes the projection while the stars keep their clocks.
        void ArrangeChart()
        {
            var clip=new RectangleGeometry(viewport);clip.Freeze();
            chart.ClipGeometry=coreAperture;markers.ClipGeometry=clip;landingRings.ClipGeometry=clip;
            PlaceCore();var gate=GateCenter;
            foreach(var marker in markerLayout){
                var p=Project(marker.Anchor.X,marker.Anchor.Y);marker.Position=p;
                if(marker.Selected)positions[marker.Path]=p;
                if(appStars.TryGetValue(marker.Path,out var star))star.Reposition(p,gate);
                if(landings.TryGetValue(marker.Path,out var landing))landing.Circle.Center=p;
                if(marker.River!=null)marker.River.Place(CapturePath(marker.Path,p),AppInk(marker.Path),riverProgress);
            }
            var at=GateCaptionLocation();gateCaptionPosition.X=at.X;gateCaptionPosition.Y=at.Y;
            DrawSurvey();
            if(HasDeepSky)DrawDeepSky(flightClock.Elapsed.TotalSeconds);
            if(observing)DrawObservingSky(flightClock.Elapsed.TotalSeconds);
            UpdateTrafficLights();
            StarMapChanged?.Invoke();
        }

        void DrawChart()
        {
            if(viewport.IsEmpty||disposed||highContrast){ClearChart();return;}
            chartDirty=false;positions.Clear();markerLayout.Clear();
            foreach(var pair in slots.OrderBy(p=>p.Value)){
                markerLayout.Add(new ChartMarker{Path=pair.Key,Anchor=Anchors[pair.Value],Selected=true,
                    River=Live && !IsFlyingIn(pair.Key)?new RiverTrace():null});
            }
            var plain=new[]{new Point(.092,.85),new Point(.561,.90),new Point(.747,.09)};int n=0;
            foreach(var pair in directNames.OrderByDescending(x=>String.Equals(x.Key,returningDirect,StringComparison.OrdinalIgnoreCase)).ThenBy(x=>PathSeed(x.Key)))markerLayout.Add(new ChartMarker{Path=pair.Key,Anchor=plain[n++]});
            ArrangeChart();
            using(var d=chart.Open())using(var rear=riverBack.Open())using(var near=riverFront.Open())
                foreach(var marker in markerLayout)if(marker.River!=null){
                    d.PushTransform(channels[marker.Path].ApproachView);d.DrawDrawing(marker.River.Art);d.Pop();
                    rear.DrawDrawing(marker.River.Disk.Rear);near.DrawDrawing(marker.River.Disk.Front);
                }
            using(var d=markers.Open()){
                foreach(var marker in markerLayout){
                    var path=marker.Path;var p=marker.Position;
                    if(!marker.Selected){
                        PaintAppStar(d,path,p,false,1.06,.53);
                        continue;
                    }
                    if(IsFlyingIn(path)) { PaintAppStar(d,path,p,false,1.05,.24);continue; }
                    if(landings.TryGetValue(path,out var landing)) {
                        using(var ink=landing.Star.Open())DrawAppMarker(ink,marker);
                        d.DrawDrawing(landing.Star);
                    }else DrawAppMarker(d,marker);
                }
                if(viewport.Height>72){
                    d.PushTransform(gateCaptionPosition);
                    DrawGateCaption(d,new Point(),viewport.Right-gateCaptionPosition.X-1);
                    d.Pop();
                }
            }
            PruneApplicationStars(flightClock.Elapsed.TotalSeconds);
            using(var dc=emittingStars.Open())using(var rear=rearAppSources.Open()){
                foreach(var m in markerLayout){
                    if(!appStars.TryGetValue(m.Path,out var lamp) || IsFlyingIn(m.Path))continue;
                    dc.DrawDrawing(lamp.Art);
                    if(!m.Selected)rear.DrawDrawing(lamp.Art);
                }
            }
        }

        void DrawAppMarker(DrawingContext d,ChartMarker marker)
        {
            var path=marker.Path;
            PaintAppStar(d,path,marker.Position,true,AppStarRadius(path),infalls.ContainsKey(path) ? .40 : Live?1:.71);
        }

        internal bool TryGetStarPosition(string path,out Point point)
        {
            if(chartDirty)DrawChart();
            point=new Point();
            return !String.IsNullOrEmpty(path)&&positions.TryGetValue(path,out point);
        }

        void ClearConnections()
        {
            foreach(var flash in connectionSignals.Children.OfType<DrawingGroup>().ToArray())flash.BeginAnimation(DrawingGroup.OpacityProperty,null);
            connectionSignals.Children.Clear();
        }

        internal void PulseConnection(string path)
        {
            Point star;
            if(!running||!Live||highContrast||!SystemParameters.ClientAreaAnimation||IsFlyingIn(path)||!TryGetStarPosition(path,out star))return;
            if(connectionSignals.Children.Count>=16)return;
            var flash=new DrawingGroup();
            using(var d=flash.Open()){
                var color=AppInk(path);
                d.DrawEllipse(Glow(A(color,.95)),null,star,15,15);
                d.DrawEllipse(Brushes.White,null,star,1.25,1.25);
            }
            var animation=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(480),FillBehavior=FillBehavior.Stop};
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1,TimeSpan.FromMilliseconds(75)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(0,TimeSpan.FromMilliseconds(480),new KeySpline(.2,0,0,1)));
            animation.Completed+=(s,e)=>connectionSignals.Children.Remove(flash);
            flash.Opacity=0;connectionSignals.Children.Add(flash);
            flash.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.AtRefresh(animation,refreshRate));
        }
        void SettleApplications() {SettleApplicationStars();StopSkyFade();ClearLights();ClearConnections();SettleFlights();FormCore(false);}
        void ReleaseStellar(){LayoutStarLens();++artworkRequest;skies.Clear();recentSkies.Clear();LayoutStarLens();ClearChart();}
    }
}
