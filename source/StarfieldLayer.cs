using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer : FrameworkElement
    {
        sealed class Layer : DrawingVisual
        {
            internal readonly DrawingGroup Art = new DrawingGroup();
            internal readonly TranslateTransform Slide = new TranslateTransform();
            internal Layer()
            {
                Transform = Slide;
                using (var dc = RenderOpen()) dc.DrawDrawing(Art);
            }
        }

        readonly VisualCollection visuals;
        internal readonly MilestoneSky Milestone=new MilestoneSky();
        readonly ContainerVisual scene = new ContainerVisual();
        readonly NebulaSky volume=new NebulaSky();
        readonly Layer backdrop = new Layer(), galaxy = new Layer(), ribbon = new Layer(), distant = new Layer(), middle = new Layer(),
            close = new Layer(), lower = new Layer(), atmosphere = new Layer(), sweep = new Layer(), ripple = new Layer(), meteor = new Layer();
        readonly ContainerVisual lines = new ContainerVisual(), namedStars = new ContainerVisual();
        readonly TranslateTransform drift = new TranslateTransform(), sweepSlide = new TranslateTransform();
        readonly GradientStop skyTint = new GradientStop();
        readonly DrawingGroup[] galaxyTints = { new DrawingGroup(), new DrawingGroup(), new DrawingGroup(), new DrawingGroup() };
        readonly List<DrawingGroup> twinkles = new List<DrawingGroup>();
        readonly List<Layer> nearLights = new List<Layer>();
        readonly List<int> visibleTwinkles = new List<int>();
        readonly List<Clock> ambient = new List<Clock>();
        ClockGroup twinkleCycle;
        readonly List<ClockGroup> scan = new List<ClockGroup>();
        readonly Random weather = new Random();
        readonly DispatcherTimer meteorTimer;
        SkyAtlas atlas;
        string state = "StatusOff", atlasKey;
        bool building, checkAgain, dark, highContrast, running, disposed, parallaxAttached, playingMeteor, waving;
        int? refreshRate;
        double headerHeight = 210, lastFrame;
        readonly Dictionary<FrostedPane,Rect> covered=new Dictionary<FrostedPane,Rect>();
        Point aim,position,tiltAim,tiltLook;
        Point? lastPointer;
        Vector speed,tiltVelocity;
        readonly Stopwatch parallaxTime = new Stopwatch();
        static readonly LinearGradientBrush Night = Gradient(Color.FromRgb(7,11,30), Color.FromRgb(17,20,55), Color.FromRgb(27,23,70));
        static readonly LinearGradientBrush Deep = Gradient(Color.FromRgb(27,23,70), Color.FromRgb(20,22,49), Color.FromRgb(14,17,34));
        static readonly LinearGradientBrush Pearl = Gradient(Color.FromRgb(240,247,255),Color.FromRgb(250,251,255),Color.FromRgb(240,247,253));
        static readonly Brush[] daylight = { Mist(156,140,219,248), Mist(159,179,173,243), Mist(120,144,221,214) };
        static readonly Brush[] midnight = { Mist(122,39,126,149), Mist(112,103,84,161), Mist(88,57,120,144) };
        const int FarTravel=14, MiddleTravel=32, NearTravel=64, GalaxyTravel=25, MistTravel=22;
        static readonly RadialGradientBrush StarGlow = Glow(Color.FromArgb(125,201,224,255));
        static readonly RadialGradientBrush NamedGlow = Glow(Color.FromArgb(180,140,224,255));
        internal bool Ready { get { return atlas != null; } }
        internal bool AmbientRunning { get { return running; } }
        internal bool WaveRunning { get { return waving; } }
        internal bool IsTrackingPointer { get { return parallaxAttached; } }

        public StarfieldLayer()
        {
            visuals = new VisualCollection(this) { scene };
            IsHitTestVisible = false;
            ClipToBounds = true;
            InitializeStellar();
            InitializeStarLens();
            var cloudLayer=new DrawingVisual();
            using(var dc=cloudLayer.RenderOpen())dc.DrawDrawing(volume.Art);
            var rewards=new DrawingVisual();
            using(var dc=rewards.RenderOpen())dc.DrawDrawing(deepSky);
            var unselected=new DrawingVisual{Transform=mapShift};
            using(var dc=unselected.RenderOpen())dc.DrawDrawing(rearAppSources);
            foreach(var layer in new ContainerVisual[]{backdrop,atmosphere,lower})compositionBase.Children.Add(layer);
            foreach(var layer in new ContainerVisual[]{galaxy,sweep,Milestone,rewards,starDepth,unselected})compositionUpper.Children.Add(layer);
            naturalSky.Children.Add(compositionBase);naturalSky.Children.Add(cloudLayer);naturalSky.Children.Add(compositionUpper);
            straightSky.Children.Add(naturalSky);
            localStraight.Children.Add(straightSky);transmittedSky.Children.Add(localStraight);straightTiles.Opacity=0;transmittedSky.Children.Add(straightTiles);transmittedSky.Children.Add(lensPatch);
            foreach(var layer in new ContainerVisual[]{transmittedSky,constellations,ripple,meteor})scene.Children.Add(layer);
            IsVisibleChanged+=(s,e)=>SyncStandby();
            Unloaded+=(s,e)=>{Milestone.Settle();PauseStandby();ClearClickStars();};
            ribbon.Transform = drift;
            galaxy.Children.Add(ribbon);
            RenderOptions.SetBitmapScalingMode(ribbon, BitmapScalingMode.Linear);
            sweep.Transform = new TransformGroup { Children={sweep.Slide,sweepSlide} };
            InitializeFrost();
            meteorTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            meteorTimer.Tick += MeteorDue;
            Loaded += (s,e) => {RefreshDisplay();SyncStandby();};
            SizeChanged += (s,e) => { DelayFrostResize(); if (atlasKey == null) EnsureAtlas(); DrawSurface(); };
            SetState(state, false, new Point());
        }

        protected override int VisualChildrenCount { get { return visuals?.Count ?? 0; } }
        protected override Visual GetVisualChild(int index) { return visuals[index]; }
        protected override void OnDpiChanged(DpiScale from, DpiScale to) { base.OnDpiChanged(from, to); RefreshDisplay(); }

        static LinearGradientBrush Gradient(Color top, Color mid, Color bottom)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(0,1) };
            b.GradientStops.Add(new GradientStop(top, 0)); b.GradientStops.Add(new GradientStop(mid, 0.58)); b.GradientStops.Add(new GradientStop(bottom, 1));
            b.Freeze();
            return b;
        }

        [ThreadStatic] static RadialGradientBrush[] glowBrushes;
        [ThreadStatic] static Color[] glowKeys;
        static RadialGradientBrush Glow(Color c)
        {
            if(glowBrushes==null){glowBrushes=new RadialGradientBrush[2048];glowKeys=new Color[2048];}
            int slot=(c.A*131+c.R*97+c.G*17+c.B)&2047;
            if(glowBrushes[slot]!=null && glowKeys[slot]==c)return glowBrushes[slot];
            var b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(c, 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(c.A * 0.32), c.R,c.G,c.B), 0.25));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0,c.R,c.G,c.B), 1));
            b.Freeze();glowBrushes[slot]=b;glowKeys[slot]=c;return b;
        }

        static Brush Mist(byte a,byte r,byte g,byte b)
        {
            var brush=new RadialGradientBrush(Color.FromArgb(a,r,g,b),Color.FromArgb(0,r,g,b));
            brush.Freeze(); return brush;
        }

        static void DrawStar(DrawingContext dc, Point p, double radius, Color color, bool spikes, bool named)
        {
            dc.DrawEllipse(named ? NamedGlow : StarGlow, null, p, radius * 5.5, radius * 5.5);
            if (spikes)
            {
                var pen = new Pen(SkyAtlas.Brush(Color.FromArgb(100,color.R,color.G,color.B)), 0.65);
                pen.Freeze();
                dc.DrawLine(pen, new Point(p.X-4,p.Y), new Point(p.X+4,p.Y));
                dc.DrawLine(pen, new Point(p.X,p.Y-4), new Point(p.X,p.Y+4));
            }
            dc.DrawEllipse(SkyAtlas.Brush(color), null, p, radius, radius);
        }

        internal void SetHeaderHeight(double value)
        {
            if (Math.Abs(headerHeight - value) < 0.2) return;
            headerHeight = value;
            DrawSurface();
            LayoutStarLens();
        }

        internal void SetTheme(bool isDark, bool contrast)
        {
            bool contrastChanged=contrast!=highContrast;
            dark = isDark; highContrast = contrast;
            if(contrastChanged){ if(contrast)SettleApplications();SyncStandby();QueueChart(); }
            scene.Opacity = contrast ? 0 : 1;
            if (!contrast) { EnsureAtlas(); EnsureStellarArt(); }
            DrawSurface();
        }

        internal void RefreshDisplay()
        {
            EnsureAtlas(); EnsureStellarArt();
            LayoutStarLens();
            if (running) SetFrameRate(DisplayRefresh.For(this));
        }

        void SetFrameRate(int? rate)
        {
            if (refreshRate == rate) return;
            refreshRate = rate;
            volume.Activity(running && SystemParameters.ClientAreaAnimation,rate);
            SyncAppStarRefresh();
            SyncCoreOrbit();
            if (ambient.Count == 0) return;
            // Parent clocks have infinite duration, so time preserves the direction of a reversing child.
            TimeSpan driftTime = ambient[0].CurrentTime ?? TimeSpan.Zero;
            TimeSpan starTime = twinkleCycle?.CurrentTime ?? TimeSpan.Zero;
            TimeSpan scanTime = scan.Count == 0 ? TimeSpan.Zero : scan[0].CurrentTime ?? TimeSpan.Zero;
            DetachCycles();
            StartAmbient(driftTime,starTime,scanTime);
        }

        internal void EnsureAtlas()
        {
            if (disposed || highContrast || ActualWidth <= 0 || ActualHeight <= 0) return;
            var source = PresentationSource.FromVisual(this) as HwndSource;
            var dpi=VisualTreeHelper.GetDpi(this);
            BuildAtlas(source?.Handle??IntPtr.Zero,dpi.DpiScaleX,Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        }

        internal void PrepareOpeningAtlas(Window window)
        {
            // The cloaked HWND already identifies the monitor before WPF's first layout.
            // DPI and monitor changes still pass through the normal atlas-key checks.
            if(disposed || SystemParameters.HighContrast)return;
            var hwnd=new WindowInteropHelper(window).Handle;
            var target=hwnd!=IntPtr.Zero?HwndSource.FromHwnd(hwnd)?.CompositionTarget:null;
            if(target!=null){
                var matrix=target.TransformToDevice;
                BuildAtlas(hwnd,matrix.M11,Math.Max(matrix.M11,matrix.M22));
            }
        }

        async void BuildAtlas(IntPtr hwnd,double displayScale,double pixelScale)
        {
            if (building) { checkAgain = true; return; }
            double dpi = Math.Max(1, Math.Min(4, displayScale));
            var area = SystemParameters.WorkArea;
            if (hwnd != IntPtr.Zero)
            {
                var screen = Forms.Screen.FromHandle(hwnd).Bounds;
                area = new Rect(0,0,screen.Width / dpi,screen.Height / dpi);
            }
            int width = (int)Math.Max(SkyAtlas.ReferenceWidth, Math.Min(7680 / dpi, Math.Ceiling(area.Width)));
            int height = (int)Math.Max(760, Math.Min(4320 / dpi, Math.Ceiling(area.Height)));
            double scale=ArtworkResolution.PixelsPerDip(pixelScale,width+2*SkyAtlas.Padding,height+SkyAtlas.Padding);
            var today = DateTime.Today;
            int day = today.Year * 10000 + today.Month * 100 + today.Day;
            string key = String.Join("/", day, width, height, scale);
            if (key == atlasKey) return;
            GalaxyGpu.Prepare();
            atlasKey = key;
            building = true;
            SkyAtlas result = null;
            try
            {
                result = await Task.Run(() => new SkyAtlas(width, height, scale, day)).ConfigureAwait(false);
            }
            catch (OutOfMemoryException) { /* Keep the existing atlas or the static gradient under memory pressure. */ }
            if (Dispatcher.HasShutdownStarted) return;
            try {
                await Dispatcher.InvokeAsync(() => {
                    building = false;
                    if (disposed) return;
                    if (checkAgain) { checkAgain = false; EnsureAtlas(); }
                    if (!building && result != null) Install(result);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
        }

        void Install(SkyAtlas result)
        {
            bool first = atlas == null;
            DetachCycles(); StopMeteor();
            atlas = result;ResetCaustic();
            var region = new Rect(-SkyAtlas.Padding,-SkyAtlas.Padding,result.Width+2*SkyAtlas.Padding,result.Far.Height);
            for (int i=0;i<galaxyTints.Length;i++)
                using (var dc=galaxyTints[i].Open()) dc.DrawImage(result.TintImages[i],BandBounds);
            using (var dc = distant.Art.Open()) dc.DrawImage(result.Far, region);
            using (var dc = middle.Art.Open()) dc.DrawImage(result.Middle, region);
            twinkles.Clear(); nearLights.Clear(); close.Children.Clear(); frostNear.Children.Clear(); frostStars.Clear();
            foreach (var star in result.Near)
            {
                var sprite = new DrawingGroup();
                using (var dc = sprite.Open()) DrawStar(dc, star.Point, star.Radius, star.Color, star.Spikes, false);
                var lamp = new Layer();
                lamp.Art.Children.Add(sprite);
                twinkles.Add(sprite);
                nearLights.Add(lamp);
                close.Children.Add(lamp);
                FrostStar(star);
            }
            DrawSurface();
            SetState(state, false, new Point());
            if (first && running)
            {
                Reveal(distant,600,0); Reveal(middle,600,80); Reveal(ribbon,900,0);
                foreach (var lamp in nearLights) Reveal(lamp,600,160);
            }
            if (running) StartAmbient();
        }

        void Reveal(Layer layer,int ms,int delay)
        {
            layer.Art.BeginAnimation(DrawingGroup.OpacityProperty,UiMotion.Ease(0,layer.Art.Opacity,ms,refreshRate,0.2,0,0,1,delay));
        }

        static readonly Rect BandBounds = new Rect(-0.62,-0.5,1,1);

        void PaintField(DrawingContext dc){
            dc.DrawDrawing(dark || observing?atlas.NightField:atlas.DayField);
        }

        Rect observationMonitor=Rect.Empty;
        readonly List<DrawingGroup> previewPhotos=new List<DrawingGroup>();
        double previewExposure;
        internal void PrepareObservationPhoto(Rect monitor){
            if(observationMonitor==monitor)return;
            observationMonitor=monitor;
            // A click during cold startup may beat the background artwork decoder.
            // Start with the real photograph even on that first transition.
            if(!monitor.IsEmpty && stellarImage==null)stellarImage=StellarSkyArt.ReadImage(SkyKey+".jpg");
            DrawSurface();
        }
        internal void RevealObservationPhoto(double exposure){
            if(exposure==previewExposure)return;
            previewExposure=exposure;
            foreach(var photo in previewPhotos)photo.Opacity=exposure;
            if(!observing && !observationMonitor.IsEmpty)ShowHomeChart(1-exposure);
        }
        void PaintObservedSky(DrawingContext dc,BitmapSource image)
        {
            if(!observing)StellarSkyArt.Paint(dc,image,RenderSize,Math.Min(ActualHeight,headerHeight),!dark);
            if(observationMonitor.IsEmpty){
                if(observing)StellarSkyArt.Paint(dc,image,RenderSize,ActualHeight,false,true);
                return;
            }
            if(observing){StellarSkyArt.PaintMonitor(dc,image,observationMonitor,RenderSize);return;}
            var preview=new DrawingGroup{Opacity=previewExposure};
            using(var layer=preview.Open())StellarSkyArt.PaintMonitor(layer,image,observationMonitor,RenderSize);
            previewPhotos.Add(preview);
            dc.DrawDrawing(preview);
        }

        void DrawSurface()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 1 || h < 1) return;
            SetInteractionBounds(w,h);
            LayoutStarLens();
            double normalEdge=Math.Min(h,headerHeight);
            previewPhotos.Clear();
            double edge=observing?h:normalEdge;
            volume.Layout(w,edge,highContrast);
            UpdateSkyClip();
            using (var dc = skyBase.Open())
            {
                dc.DrawRectangle(Night, null, new Rect(0,0,w,edge));
                if (h > edge) dc.DrawRectangle(dark?Deep:Pearl, null, new Rect(0,edge,w,h-edge));
                PaintObservedSky(dc,stellarImage);
            }
            DrawSkyTransition();
            PlaceFrostBase();
            using (var dc = skyAura.Open())
            {
                double glowHeight = dark ? Math.Min(h,edge+96) : edge;
                var center = new Point(0.79,0.56*edge/glowHeight);
                var tint = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = 0.67, RadiusY = dark ? 1-center.Y : 1.08 };
                tint.GradientStops.Add(skyTint);
                tint.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                dc.DrawRectangle(tint, null, new Rect(0,0,w,glowHeight));
            }
            using (var dc=atmosphere.Art.Open())
            {
                double body=Math.Max(1,h-edge);
                var colors=midnight;
                dc.PushOpacity(0.14);
                dc.DrawEllipse(colors[0],null,new Point(0.87*w,edge+0.32*body),0.57*w,0.45*body);
                dc.DrawEllipse(colors[1],null,new Point(0.12*w,edge+0.66*body),0.68*w,0.56*body);
                dc.DrawEllipse(colors[2],null,new Point(0.76*w,edge+1.08*body),0.60*w,0.38*body); dc.Pop();
            }
            lower.Opacity = 1;
            if(atlas!=null)
            {
                using(var dc=lower.Art.Open())PaintField(dc);
                using(var dc=rimStarlight.Open())PaintField(dc);
            }
            QueueFrost();
            if (atlas == null) return;
            foreach(var view in lightViews)RefreshGlassStars((ContainerVisual)view.Children[2]);
            // A fixed-angle ribbon crosses the switch at all widths. Its texture only moves as a single layer.
            double anchor = Math.Max(260,w-86);
            using (var dc = ribbon.Art.Open())
            {
                BandTransform(dc,w);
                dc.DrawImage(atlas.Galaxy,BandBounds);
                foreach (var wash in galaxyTints) dc.DrawDrawing(wash);
                dc.Pop(); dc.Pop(); dc.Pop();
            }
            using (var dc = sweep.Art.Open())
            {
                dc.PushTransform(new TranslateTransform(anchor,91)); dc.PushTransform(new RotateTransform(-22));
                dc.DrawEllipse(ScanGlow,null,new Point(),80,13);
                dc.Pop(); dc.Pop();
            }
            if (running && ambient.Count != 0) SyncTwinkles();
        }
        internal void CoverWithGlass(FrostedPane pane,Rect area)
        {
            // Allow the rounded bevel and its anti-aliased boundaries to see the sharp sky.
            if(!area.IsEmpty){if(area.Width<=34 || area.Height<=34)area=Rect.Empty;else area.Inflate(-17,-17);}
            if(area.IsEmpty){if(!covered.Remove(pane))return;}
            else{if(covered.TryGetValue(pane,out var old) && old==area)return;covered[pane]=area;}
            UpdateSkyClip();
        }
        void UpdateSkyClip()
        {
            var bounds=new Rect(RenderSize);
            Geometry region=new RectangleGeometry(bounds);
            if(observationMonitor.IsEmpty && !observing && FrostReady && covered.Count>0){
                var cutouts=new GeometryGroup{FillRule=FillRule.Nonzero};foreach(var area in covered.Values){
                    var interior=Rect.Intersect(area,bounds);if(!interior.IsEmpty)cutouts.Children.Add(new RectangleGeometry(interior));
                }
                region=new CombinedGeometry(GeometryCombineMode.Exclude,region,cutouts);
            }
            region.Freeze();scene.Clip=region;
        }
        static readonly RadialGradientBrush ScanGlow = Glow(Color.FromArgb(104,146,204,255));

        internal void SetState(string value, bool animate, Point origin)
        {
            bool changed = state != value;
            state = value;
            bool on = state == "StatusOn", busy = state == "StatusBusy", warn = state == "StatusWarning";
            bool motion = animate && running;
            volume.Light(value,motion && SystemParameters.ClientAreaAnimation);
            double stars = on ? 0.65 : busy ? 0.42 : warn ? 0.36 : 0.30, band = on ? 0.075 : busy ? 0.055 : warn ? 0.04 : 0.03;
            int variant = on ? 2 : busy ? 1 : warn ? 3 : 0;
            TintFrost(variant,stars,band,motion);
            Color tint = SkyAtlas.TintColors[variant];
            var aura = Color.FromArgb(on ? (byte)15 : (byte)8,tint.R,tint.G,tint.B);
            UiMotion.PaintColor(skyTint,GradientStop.ColorProperty,aura,900,refreshRate,motion);
            for (int i=0;i<galaxyTints.Length;i++) OpacityTo(galaxyTints[i],i==variant?1:0,900,motion,refreshRate);
            foreach (var layer in new[] { distant, middle, lower }) OpacityTo(layer.Art,stars,600,motion,refreshRate);
            foreach (var lamp in nearLights) OpacityTo(lamp.Art,stars,600,motion,refreshRate);
            OpacityTo(ribbon.Art,band,600,motion,refreshRate);
            if (changed)
            {
                if (twinkleCycle != null) twinkleCycle.Controller.SpeedRatio = state == "StatusOff" ? 0.58 : 1;
                SyncScan();
                if (on) ScheduleMeteor(); else StopMeteor();
                if (on && motion) StartWave(viewport.IsEmpty ? origin : GateCenter);
                else if (!on) StopWave(motion);
            }
        }

        static void OpacityTo(DrawingGroup art, double value, int ms, bool motion, int? rate)
        {
            double from = art.Opacity;
            art.Opacity = value;
            art.BeginAnimation(DrawingGroup.OpacityProperty, motion && Math.Abs(from-value)>0.0001 ? UiMotion.Ease(from,value,ms,rate) : null);
        }

        internal void SetActivity(bool active)
        {
            if (disposed) return;
            bool next = active && !highContrast && (RenderCapability.Tier >> 16) >= 2 &&
                RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly;
            volume.Activity(next && SystemParameters.ClientAreaAnimation,refreshRate);
            if (running && next) SetFrameRate(DisplayRefresh.For(this));
            if (running != next)
            {
                running = next;
                QueueChart();
                if (next)
                {
                    SetFrameRate(DisplayRefresh.For(this));
                    if (ambient.Count == 0) StartAmbient();
                    else {
                        foreach (var clock in ambient) clock.Controller.Resume();
                        foreach (var clock in scan) clock.Controller.Resume();
                        SyncTwinkles(); SyncScan();
                    }
                    SetState(state,true,new Point());
                }
                else
                {
                    foreach (var clock in ambient) clock.Controller.Pause();
                    foreach (var clock in scan) clock.Controller.Pause();
                    ResetCaustic();StopWave(false); StopMeteor(); ClearClickStars(); StopTracking(!SystemParameters.ClientAreaAnimation);
                    SettleApplications();
                    if (!SystemParameters.ClientAreaAnimation || highContrast || (RenderCapability.Tier >> 16) < 2 ||
                        RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly) SetState(state,false,new Point());
                    else {
                        foreach (var lamp in nearLights) Hold(lamp.Art,DrawingGroup.OpacityProperty);
                        foreach (var layer in new[] { distant,middle,lower,ribbon }) Hold(layer.Art,DrawingGroup.OpacityProperty);
                        Hold(skyTint,GradientStop.ColorProperty);
                        foreach (var wash in galaxyTints) Hold(wash,DrawingGroup.OpacityProperty);
                        foreach (var part in new[]{frostFar,frostMiddle,frostNear,frostLower,frostBand,rimStarlight}) Hold(part,DrawingGroup.OpacityProperty);
                        foreach (var wash in frostTints) Hold(wash,DrawingGroup.OpacityProperty);
                        if(blendingControls){
                            ++controlRevision;blendingControls=false;
                            for(int i=0;i<controlStates.Length;i++)OpacityTo(controlStates[i],i==controlVariant?1:0,0,false,refreshRate);
                            SamplingChanged?.Invoke(this,EventArgs.Empty);
                        }
                        foreach (DrawingGroup star in frostNear.Children) Hold(star,DrawingGroup.OpacityProperty);
                    }
                }
            }
            if (running && !meteorTimer.IsEnabled) ScheduleMeteor();
            SyncCoreOrbit();
            UpdateTrafficLights();
        }

        static void Hold(Animatable owner, DependencyProperty property)
        {
            var value = owner.GetValue(property);
            owner.SetValue(property,value);
            owner.BeginAnimation(property,null);
        }

        ClockGroup Loop(Animatable target, DependencyProperty dp, double from, double to, double seconds, double phase, bool reverse, TimeSpan elapsed)
        {
            var timeline = new DoubleAnimation(from,to,TimeSpan.FromSeconds(seconds)) { AutoReverse = reverse, RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(-phase * seconds * (reverse ? 2 : 1)),
                EasingFunction = reverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null };
            var cycle = new ParallelTimeline { Duration = Duration.Forever, Children = { timeline } };
            var clock = (ClockGroup)UiMotion.AtRefresh(cycle,refreshRate).CreateClock(true);
            target.ApplyAnimationClock(dp,(AnimationClock)clock.Children[0]);
            clock.Controller.SeekAlignedToLastTick(elapsed,TimeSeekOrigin.BeginTime);
            return clock;
        }

        void StartAmbient(TimeSpan driftTime = default(TimeSpan),TimeSpan starTime = default(TimeSpan),TimeSpan scanTime = default(TimeSpan))
        {
            if (atlas == null || ambient.Count > 0 || !running) return;
            ambient.Add(Loop(drift,TranslateTransform.XProperty,-12,12,40,0.25,true,driftTime));
            SyncTwinkles(starTime); SyncScan(scanTime); ScheduleMeteor();
        }

        void SyncTwinkles(TimeSpan? savedTime = null)
        {
            var visible = new List<int>();
            for (int i = 0; i < twinkles.Count; i++)
            {
                var star = atlas.Near[i];
                if (star.Point.X <= ActualWidth+NearTravel+8 && star.Point.Y <= ActualHeight+NearTravel+8) visible.Add(i);
            }
            if (System.Linq.Enumerable.SequenceEqual(visible,visibleTwinkles)) return;
            var elapsed = savedTime ?? twinkleCycle?.CurrentTime ?? TimeSpan.Zero;
            if (twinkleCycle != null) { twinkleCycle.Controller.Remove(); ambient.Remove(twinkleCycle); twinkleCycle=null; }
            foreach (int i in visibleTwinkles) { twinkles[i].BeginAnimation(DrawingGroup.OpacityProperty,null);frostStars[i].BeginAnimation(DrawingGroup.OpacityProperty,null); }
            visibleTwinkles.Clear(); visibleTwinkles.AddRange(visible);
            if (visible.Count==0) return;
            var cycle = UiMotion.AtRefresh(new ParallelTimeline { Duration = Duration.Forever },refreshRate);
            foreach (int i in visible)
            {
                var star=atlas.Near[i];
                cycle.Children.Add(new DoubleAnimation(0.35,1,TimeSpan.FromSeconds(star.Period/2)) {
                    BeginTime=TimeSpan.FromSeconds(-star.Phase*star.Period), AutoReverse=true,
                    RepeatBehavior=RepeatBehavior.Forever, EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut }
                });
            }
            twinkleCycle=(ClockGroup)cycle.CreateClock(true);
            for (int i=0;i<visible.Count;i++)
            {
                var clock=(AnimationClock)twinkleCycle.Children[i];
                twinkles[visible[i]].ApplyAnimationClock(DrawingGroup.OpacityProperty,clock);
                frostStars[visible[i]].ApplyAnimationClock(DrawingGroup.OpacityProperty,clock);
            }
            twinkleCycle.Controller.SpeedRatio=state=="StatusOff"?0.58:1;
            twinkleCycle.Controller.SeekAlignedToLastTick(elapsed,TimeSeekOrigin.BeginTime);
            ambient.Add(twinkleCycle);
        }

        void SyncScan(TimeSpan elapsed = default(TimeSpan))
        {
            if (state != "StatusBusy")
            {
                foreach (var clock in scan) clock.Controller.Remove();
                scan.Clear(); sweep.Opacity = frostSweep.Opacity = 0;
            }
            else if (running && atlas != null && scan.Count == 0)
            {
                sweep.Opacity = frostSweep.Opacity = 1;
                scan.Add(Loop(sweepSlide,TranslateTransform.XProperty,-520,170,1.4,0,false,elapsed));
                scan.Add(Loop(sweepSlide,TranslateTransform.YProperty,210,-69,1.4,0,false,elapsed));
            }
        }

        void DetachCycles()
        {
            foreach (var clock in ambient) clock.Controller.Remove();
            foreach (var clock in scan) clock.Controller.Remove();
            ambient.Clear(); scan.Clear(); visibleTwinkles.Clear(); twinkleCycle=null; sweep.Opacity = frostSweep.Opacity = 0;
        }

        bool observationAimHeld;
        internal void HoldObservationAim(bool hold){
            if(hold==observationAimHeld)return;
            observationAimHeld=hold;
            if(hold || !IsVisible)return;
            var point=System.Windows.Input.Mouse.GetPosition(this);
            if(new Rect(RenderSize).Contains(point))Aim(point);else CenterAim();
        }
        internal void Aim(Point cursor)
        {
            if (observationAimHeld || !running || ActualWidth <= 0) return;
            lastPointer=cursor;
            var window=new Rect(RenderSize);
            MoveAim(PointerIn(cursor,window),PointerIn(cursor,viewport.IsEmpty?window:viewport));
        }
        static Point PointerIn(Point cursor,Rect frame){
            return new Point(2*UiMotion.Unit((cursor.X-frame.X)/Math.Max(1,frame.Width))-1,
                2*UiMotion.Unit((cursor.Y-frame.Y)/Math.Max(1,frame.Height))-1);
        }
        internal void CenterAim(){if(observationAimHeld)return;lastPointer=null;if(running)MoveAim(new Point(),new Point());}
        void MoveAim(Point next,Point angle)
        {
            if ((aim-next).LengthSquared<.00001 && (tiltAim-angle).LengthSquared<.00001)return;
            aim=next;tiltAim=angle;
            if (parallaxAttached) return;
            lastFrame = 0; parallaxTime.Restart(); parallaxAttached = true;
            UiFrames.Rendering += TrackPointer;
        }

        void TrackPointer(object sender, EventArgs args)
        {
            double now = parallaxTime.Elapsed.TotalSeconds, dt = now - lastFrame;
            if (dt <= 0) return;
            lastFrame = now;
            double decay=Math.Exp(-8*dt);
            AdvanceLook(ref position,ref speed,aim,dt,decay);
            AdvanceLook(ref tiltLook,ref tiltVelocity,tiltAim,dt,decay);
            if((position-aim).LengthSquared<.000015 && speed.LengthSquared<.0005 &&
                (tiltLook-tiltAim).LengthSquared<.000015 && tiltVelocity.LengthSquared<.0005){
                position=aim;tiltLook=tiltAim;StopTracking(false);
            }
            MoveLayers();
        }

        static void AdvanceLook(ref Point value,ref Vector velocity,Point target,double dt,double decay){
            Vector d=value-target,coeff=velocity+8*d;
            value=target+(d+coeff*dt)*decay;velocity=(velocity-8*coeff*dt)*decay;
        }
        void MoveLayers()
        {
            var look=position+windowLag*.018;
            volume.Aim(look);
            bool moved=mapShift.X!=look.X*5 || mapShift.Y!=look.Y*5;
            if(moved){
                Shift(mapShift,look,5);Shift(cometShift,look,6);Shift(pulsarShift,look,3.5);
                Shift(gasShift,look,2.8);Shift(remnantShift,look,4);
                LayoutStarLens();
                foreach(var arc in surveyArcs.Values)arc.FollowSky();
                StarMapChanged?.Invoke();
            }
            ChangeAttitude();
            Translate(distant,FarTravel); Translate(middle,MiddleTravel); Translate(lower,MiddleTravel); Translate(close,NearTravel);
            AlignBeacons();TrackBackgroundSources();
            Translate(galaxy,GalaxyTravel); Translate(sweep,GalaxyTravel); Translate(atmosphere,MistTravel);
            SamplingChanged?.Invoke(this,EventArgs.Empty);
        }
        static void Shift(TranslateTransform plane,Point look,double depth){plane.X=look.X*depth;plane.Y=look.Y*depth;}
        readonly TranslateTransform mapShift=new TranslateTransform(),cometShift=new TranslateTransform(),pulsarShift=new TranslateTransform(),
            gasShift=new TranslateTransform(),remnantShift=new TranslateTransform();
        Point MapToScreen(Point p){return mapShift.Transform(p);}
        Point ProjectedGate {get{return MapToScreen(GateCenter);}}
        void Translate(Layer layer, int depth) { layer.Slide.X = (position.X+windowLag.X*.018)*depth; layer.Slide.Y = (position.Y+windowLag.Y*.018)*depth; }
        void StopTracking(bool center)
        {
            if (parallaxAttached) { UiFrames.Rendering -= TrackPointer; parallaxAttached = false; }
            speed=tiltVelocity=new Vector();parallaxTime.Reset();
            if (center) { position=aim=tiltLook=tiltAim=new Point();lastPointer=null;MoveLayers(); }
        }

        internal void Release()
        {
            disposed = true; running = false; frostUpdate?.Abort(); cancelObsoleteFrost?.Invoke();
            SyncObservatory();
            observingRibbons.Dispose();volume.Release();StopFrostResize();
            PauseStandby();
            DetachCycles(); StopMeteor(); StopWave(false); ClearClickStars(); StopTracking(false);
            SettleApplications(); ReleaseStellar(); meteorTimer.Tick -= MeteorDue;
        }
    }
}
