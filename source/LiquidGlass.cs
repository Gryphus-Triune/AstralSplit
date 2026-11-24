using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AstralSplit
{
    sealed class OpticalMap
    {
        internal readonly ImageBrush Brush;
        internal readonly int Width,Height;
        readonly BitmapSource texture;
        Int32Rect region;
        byte[] pixels;
        internal OpticalMap(BitmapSource texture)
        {
            this.texture=texture;
            Brush=new ImageBrush(texture);Brush.Freeze();
            Width=texture.PixelWidth;Height=texture.PixelHeight;
        }
        // Only the caption lights use CPU samples. Keep their neighborhood, not a second full-size atlas.
        internal void ReadRegion(Rect aperture,double sx,double sy)
        {
            int x=Math.Min(Width-1,Math.Max(0,(int)Math.Floor(aperture.Left*sx)-1));
            int y=Math.Min(Height-1,Math.Max(0,(int)Math.Floor(aperture.Top*sy)-1));
            int right=Math.Max(x+1,Math.Min(Width,(int)Math.Ceiling(aperture.Right*sx)+2));
            int bottom=Math.Max(y+1,Math.Min(Height,(int)Math.Ceiling(aperture.Bottom*sy)+2));
            if(pixels!=null && x>=region.X && y>=region.Y && right<=region.X+region.Width && bottom<=region.Y+region.Height)return;
            x=Math.Max(0,x-160);y=Math.Max(0,y-96);
            region=new Int32Rect(x,y,Math.Min(Width,right+160)-x,Math.Min(Height,bottom+96)-y);
            int stride=region.Width*4;
            if(pixels==null || pixels.Length<stride*region.Height)pixels=new byte[stride*region.Height];
            texture.CopyPixels(region,pixels,stride,0);
        }
        internal void Sample(double x,double y,byte[] target,int index)
        {
            x=Math.Max(0,Math.Min(Width-1,x));y=Math.Max(0,Math.Min(Height-1,y));
            int ix=(int)x,iy=(int)y,a=4*((iy-region.Y)*region.Width+ix-region.X);
            int b=ix+1<Width?a+4:a,c=iy+1<Height?a+4*region.Width:a,d=c+b-a;
            double u=x-ix,v=y-iy;
            for(int k=0;k<4;k++){
                double top=pixels[a+k]+(pixels[b+k]-pixels[a+k])*u;
                double bottom=pixels[c+k]+(pixels[d+k]-pixels[c+k])*u;
                target[index+k]=(byte)Math.Round(top+(bottom-top)*v);
            }
        }
    }

    // A traffic light covers only a few hundred device pixels. Keep its glass coating cached
    // and move the refracted image underneath, without creating a GPU effect target per light.
    sealed class CaptionRefraction
    {
        internal readonly WriteableBitmap Image;
        readonly Point[] aperture;
        byte[] painted,pending;
        OpticalMap previous;
        Rect previousArea=Rect.Empty;
        internal CaptionRefraction(Size face,int w,int h)
        {
            Image=new WriteableBitmap(w,h,96,96,PixelFormats.Pbgra32,null);
            aperture=new Point[w*h];painted=new byte[w*h*4];pending=new byte[painted.Length];
            double radius=Math.Min(face.Width,face.Height)/2,bevel=Math.Max(2.2,Math.Min(8,face.Height*0.29));
            for(int y=0;y<h;y++)for(int x=0;x<w;x++){
                double px=(x+0.5)/w*face.Width,py=(y+0.5)/h*face.Height;
                double nx=px-face.Width/2,ny=py-face.Height/2,r=Math.Sqrt(nx*nx+ny*ny);
                double sin=UiMotion.Unit(1-(radius-r)/bevel);
                double bend=1.9*bevel*Math.Tan(Math.Asin(sin)-Math.Asin(sin/1.48))/1.1;
                double f=bend/Math.Max(0.001,r);
                aperture[y*w+x]=new Point(UiMotion.Unit((px-nx*f)/face.Width),UiMotion.Unit((py-ny*f)/face.Height));
            }
        }
        internal void Update(OpticalMap sky,Rect rect,Size world,double transmission)
        {
            if(sky==previous && rect==previousArea)return;
            bool force=sky!=previous;previous=sky;previousArea=rect;
            double sx=sky.Width/world.Width,sy=sky.Height/world.Height;
            sky.ReadRegion(rect,sx,sy);
            int change=0;
            for(int i=0;i<aperture.Length;i++){
                var uv=aperture[i];int p=i*4;
                sky.Sample((rect.X+uv.X*rect.Width)*sx-0.5,(rect.Y+uv.Y*rect.Height)*sy-0.5,pending,p);
                for(int k=0;k<4;k++)change=Math.Max(change,Math.Abs(pending[p+k]-painted[p+k]));
            }
            if(!force && change*transmission<0.75)return;
            Image.WritePixels(new Int32Rect(0,0,Image.PixelWidth,Image.PixelHeight),pending,Image.PixelWidth*4,0);
            var swap=painted;painted=pending;pending=swap;
        }
    }
    sealed class GlassEffect : ShaderEffect
    {
        static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassEffect), 0, SamplingMode.Bilinear);
        static readonly DependencyProperty ProfileProperty = RegisterPixelShaderSamplerProperty("Profile", typeof(GlassEffect), 1, SamplingMode.Bilinear);
        static readonly DependencyProperty AtlasProperty = RegisterPixelShaderSamplerProperty("Atlas",typeof(GlassEffect),2,SamplingMode.Bilinear);
        static readonly DependencyProperty ShapeProperty = Constant("Shape", new Point3D(1,1,0.5), 0);
        internal static readonly DependencyProperty LightProperty = Constant("Light", new Point(-0.54,-0.84), 1);
        static readonly DependencyProperty FinishProperty = Constant("Finish", new Point3D(1,0,0), 2);
        static readonly DependencyProperty OpticsProperty = Constant("Optics", new Point3D(1,1,1), 3);
        static readonly DependencyProperty ViewportProperty = Constant("Viewport", new Point4D(0,0,1,1), 4);
        static readonly DependencyProperty ApertureProperty=Constant("Aperture",new Point4D(0,0,1,1),5);
        static readonly DependencyProperty BevelShapeProperty=Constant("BevelShape",new Point4D(0,0,1,1),6);
        static readonly PixelShader program = LoadProgram("LiquidGlass"), projectedProgram=LoadProgram("ProjectedGlass");
        internal bool NativeProjected{get{return PixelShader!=null && (PixelShader==projectedProgram || PixelShader==projected2);}}
        static readonly PixelShader plain2=LoadProgram("LiquidGlass2"), projected2=LoadProgram("ProjectedGlass2");
        readonly bool projection;
        internal static bool Hardware3(Visual v) { return RenderOptions.ProcessRenderMode!=System.Windows.Interop.RenderMode.SoftwareOnly &&
            RenderCapability.IsPixelShaderVersionSupported(3,0) && PresentationSource.FromVisual(v) is System.Windows.Interop.HwndSource source &&
            source.CompositionTarget!=null && source.CompositionTarget.RenderMode!=System.Windows.Interop.RenderMode.SoftwareOnly; }
        internal void Device(Visual v){
            var next=Hardware3(v)?(projection?projectedProgram:program):(projection?projected2:plain2);
            if(next!=null && PixelShader!=next)PixelShader=next;
        }
        static readonly Brush profile = MakeProfile();
        internal static bool Available { get { return program != null; } }
        internal static bool CanProject { get { return projectedProgram!=null; } }

        static DependencyProperty Constant(string name, object initial, int slot)
        {
            return DependencyProperty.Register(name, initial.GetType(), typeof(GlassEffect), new UIPropertyMetadata(initial, PixelShaderConstantCallback(slot)));
        }

        internal static PixelShader LoadProgram(string name)
        {
            using (var bytes = typeof(GlassEffect).Assembly.GetManifestResourceStream("AstralSplit."+name+".ps"))
            {
                if (bytes == null) return null;
                var shader = new PixelShader();
                shader.SetStreamSource(bytes);
                shader.Freeze();
                return shader;
            }
        }

        static Brush MakeProfile()
        {
            // tan(incident - transmitted) for a convex glass-to-air interface viewed from air.
            var pixels = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                double angle = Math.Asin(i / 255.0);
                double deviation = Math.Tan(angle - Math.Asin(Math.Sin(angle) / 1.48));
                byte value = (byte)Math.Round(255 * deviation / 1.10);
                pixels[i*4] = pixels[i*4+1] = pixels[i*4+2] = value;
                pixels[i*4+3] = 255;
            }
            var map = BitmapSource.Create(256,1,96,96,PixelFormats.Bgra32,null,pixels,1024);
            map.Freeze();
            var brush = new ImageBrush(map) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        internal GlassEffect(bool projected=false)
        {
            projection=projected;PixelShader = projected?(projected2??projectedProgram):(plain2??program);
            SetValue(ProfileProperty, profile);
            foreach (var p in new[] { InputProperty, ProfileProperty, ShapeProperty, LightProperty, FinishProperty, OpticsProperty, ViewportProperty, BevelShapeProperty }) UpdateShaderValue(p);
            if(projected){UpdateShaderValue(AtlasProperty);UpdateShaderValue(ApertureProperty);}
        }
        internal void Sample(ImageBrush atlas,Rect region,Size size)
        {
            if(GetValue(AtlasProperty)!=atlas)SetValue(AtlasProperty,atlas);
            SetValue(ApertureProperty,new Point4D(region.X/size.Width,region.Y/size.Height,region.Width/size.Width,region.Height/size.Height));
        }

        internal void Size(double w, double h, double r, double bevel=0, Rect? region=null)
        {
            if(bevel<=0) bevel=Math.Max(2.2,Math.Min(8,h*0.29));
            SetValue(ShapeProperty,new Point3D(w,h,r));
            double thickness=1.9*bevel;
            var area=region ?? new Rect(0,0,w,h);
            SetValue(ViewportProperty,new Point4D(area.X/w,area.Y/h,area.Width/w,area.Height/h));
            float fw=(float)w,fh=(float)h,fr=(float)r;
            float extentX=projection?fw:fw*(float)(area.Width/w),extentY=projection?fh:fh*(float)(area.Height/h);
            SetValue(BevelShapeProperty,new Point4D(fw*.5f-fr,fh*.5f-fr,1f/Math.Max(1f,extentX),1f/Math.Max(1f,extentY)));
            SetValue(OpticsProperty,new Point3D(thickness/area.Width,thickness/area.Height,1/bevel));
        }
        internal void Finish(double strength, bool pressed, bool perimeter=false) { SetValue(FinishProperty,new Point3D(strength,pressed ? 1 : 0,perimeter ? 1 : 0)); }
    }

    // Only the backdrop is sampled. Controls and text are siblings of the material.
    static class GlassSampling
    {
        // One backdrop texture feeds every aperture. Cropping a DrawingBrush does not rerecord the sky or list.
        sealed class Shared
        {
            internal readonly GeometryDrawing Drawing;
            internal Shared(FrameworkElement source)
            {
                var cache=new BitmapCacheBrush(source) { AutoLayoutContent=false };
                if (source.CacheMode is BitmapCache) cache.BitmapCache=null;
                else cache.BitmapCache=new BitmapCache(ArtworkResolution.Ratio);
                var bounds=new RectangleGeometry(new Rect(source.RenderSize));
                Drawing=new GeometryDrawing(cache,null,bounds);
                source.SizeChanged+=(s,e)=>bounds.Rect=new Rect(e.NewSize);
            }
        }
        static readonly ConditionalWeakTable<FrameworkElement,Shared> textures=new ConditionalWeakTable<FrameworkElement,Shared>();
        internal static DrawingBrush Sampler()
        {
            return new DrawingBrush { ViewboxUnits=BrushMappingMode.Absolute, Stretch=Stretch.Fill,
                AlignmentX=AlignmentX.Left, AlignmentY=AlignmentY.Top };
        }
        internal static Drawing Texture(FrameworkElement source)
        {
            var sky=source as StarfieldLayer;
            if(sky!=null)return sky.FrostSky;
            if(source is FrostedPane pane && pane.Backdrop is StarfieldLayer)return pane.TransmittedMaterial;
            return textures.GetValue(source, s=>new Shared(s)).Drawing;
        }
        internal static bool Map(FrameworkElement destination, FrameworkElement source, DrawingBrush brush, Rect area,StarfieldLayer projection=null,bool preserveCenter=false,bool followTransform=false)
        {
            if(projection!=null)source=projection;
            if (source == null || source.RenderSize.Width < 1 || source.RenderSize.Height < 1) return false;
            Point origin;
            Size extent=area.Size;
            if (destination.FindCommonVisualAncestor(source) != null)
            {
                if(followTransform){
                    var visible=destination.TransformToVisual(source).TransformBounds(area);
                    origin=visible.TopLeft;
                    extent=visible.Size;
                }
                else origin = destination.TranslatePoint(area.TopLeft,source);
            }
            else if (PresentationSource.FromVisual(source) is System.Windows.Interop.HwndSource &&
                     PresentationSource.FromVisual(destination) is System.Windows.Interop.HwndSource)
                origin = source.PointFromScreen(destination.PointToScreen(area.TopLeft));
            else return false;
            if(projection!=null){
                var shift=projection.SamplingOffset;
                origin=new Point(origin.X-shift.X,origin.Y-shift.Y);
                if(!preserveCenter)origin=new Point(Math.Max(0,Math.Min(source.ActualWidth-extent.Width,origin.X)),
                    Math.Max(0,Math.Min(source.ActualHeight-extent.Height,origin.Y)));
            }
            var rect = new Rect(origin,extent);
            if (brush.Viewbox != rect) brush.Viewbox = rect;
            var drawing=projection!=null?projection.ControlSky:Texture(source);
            if (brush.Drawing != drawing) brush.Drawing=drawing;
            return true;
        }
    }

    public class LiquidLens : FrameworkElement
    {
        public static readonly DependencyProperty SceneProperty = DependencyProperty.RegisterAttached("Scene", typeof(FrameworkElement),typeof(LiquidLens),
            new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.Inherits,Changed));
        public static void SetScene(DependencyObject target, FrameworkElement value) { target.SetValue(SceneProperty,value); }
        public static FrameworkElement GetScene(DependencyObject target) { return (FrameworkElement)target.GetValue(SceneProperty); }
        public static readonly DependencyProperty SceneCoatProperty = DependencyProperty.RegisterAttached("SceneCoat", typeof(Brush), typeof(LiquidLens),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, Changed));
        public static Brush GetSceneCoat(DependencyObject target) { return (Brush)target.GetValue(SceneCoatProperty); }
        public static void SetSceneCoat(DependencyObject target, Brush value) { target.SetValue(SceneCoatProperty,value); }
        public static readonly DependencyProperty GlassEnabledProperty = DependencyProperty.RegisterAttached("GlassEnabled",typeof(bool),typeof(LiquidLens),
            new FrameworkPropertyMetadata(true,FrameworkPropertyMetadataOptions.Inherits,Changed));
        public static void SetGlassEnabled(DependencyObject target,bool value) { target.SetValue(GlassEnabledProperty,value); }
        public static bool GetGlassEnabled(DependencyObject target) { return (bool)target.GetValue(GlassEnabledProperty); }
        public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint),typeof(Brush),typeof(LiquidLens),new PropertyMetadata(null,Changed));
        public static readonly DependencyProperty RestFillProperty = DependencyProperty.Register(nameof(RestFill),typeof(Brush),typeof(LiquidLens),new PropertyMetadata(Brushes.White,Changed));
        public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(nameof(TintOpacity),typeof(double),typeof(LiquidLens),new PropertyMetadata(1.0,Changed));
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius),typeof(double),typeof(LiquidLens),new PropertyMetadata(100.0,Changed));
        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength),typeof(double),typeof(LiquidLens),new PropertyMetadata(1.0,Changed));
        public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed),typeof(bool),typeof(LiquidLens),new PropertyMetadata(false,LightChanged));
        public Brush Tint { get { return (Brush)GetValue(TintProperty); } set { SetValue(TintProperty,value); } }
        public Brush RestFill { get { return (Brush)GetValue(RestFillProperty); } set { SetValue(RestFillProperty,value); } }
        public double TintOpacity { get { return (double)GetValue(TintOpacityProperty); } set { SetValue(TintOpacityProperty,value); } }
        public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty,value); } }
        public double Strength { get { return (double)GetValue(StrengthProperty); } set { SetValue(StrengthProperty,value); } }
        public bool Pressed { get { return (bool)GetValue(PressedProperty); } set { SetValue(PressedProperty,value); } }
        public bool SampleScene { get; set; }
        static readonly LinearGradientBrush reflection = Reflection();
        static readonly Pen knobEdge = (Pen)new Pen(new SolidColorBrush(Color.FromArgb(66,0,0,0)),0.65).GetAsFrozen();
        static readonly RadialGradientBrush knobShade = KnobShade();
        static readonly LinearGradientBrush knobContact = KnobContact();
        readonly ContainerVisual clipped = new ContainerVisual();
        readonly DrawingVisual input = new DrawingVisual();
        readonly DrawingBrush sample = GlassSampling.Sampler();
        readonly DrawingBrush incidentStars=GlassSampling.Sampler();
        readonly GlassEffect effect = GlassEffect.Available ? new GlassEffect() : null;
        GlassEffect projectedEffect;
        readonly ContainerVisual coating=new ContainerVisual();
        readonly DrawingVisual captionImage=new DrawingVisual();
        CaptionRefraction caption;
        CaptionGlassShader captionShader;
        Size gpuFace;
        GlassLayout layoutGroup;
        bool directTexture, tiny, drawing;
        GlassEffect ActiveEffect {get{return knobTrack!=null && knobClarity==0 ? null : directTexture&&!tiny?projectedEffect:effect;}}
        FrameworkElement owner, knobTrack;
        DrawingBrush knobSample;
        Rect knobField;
        GeometryDrawing knobRail;
        DrawingVisual knobSurface;
        Size knobTrackSize;
        double knobClarity;
        protected double KnobClarity { get { return knobClarity; } }
        StarfieldLayer projection;
        readonly SolidColorBrush projectedFilm=new SolidColorBrush(Colors.Transparent);
        Size faceSize;
        bool mapAttached, followsTransform;
        protected virtual bool ManualSize { get { return false; } }

        public LiquidLens()
        {
            IsHitTestVisible = false;
            AddVisualChild(clipped);
            clipped.CacheMode=new BitmapCache(ArtworkResolution.Ratio);
            clipped.Children.Add(captionImage);clipped.Children.Add(coating);coating.Children.Add(input);
            Loaded += (s,e) => { Connect(); Draw(); };
            Unloaded += (s,e) => { Disconnect(); sample.Drawing=null; };
        }

        static LinearGradientBrush Reflection()
        {
            var b = new LinearGradientBrush { StartPoint=new Point(0.24,0),EndPoint=new Point(0.70,1) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(72,255,255,255),0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(6,238,247,255),0.39));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(5,181,206,248),0.68));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(41,214,239,255),1));
            b.Freeze(); return b;
        }

        static RadialGradientBrush KnobShade()
        {
            var b = new RadialGradientBrush { RadiusX=0.55, RadiusY=0.66 };
            b.GradientStops.Add(new GradientStop(Colors.Transparent,0.56));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(12,0,0,0),0.82));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(46,0,0,0),1));
            b.Freeze();return b;
        }

        static LinearGradientBrush KnobContact()
        {
            var b=new LinearGradientBrush { StartPoint=new Point(0,0.5), EndPoint=new Point(1,0.5) };
            foreach(double t in new[]{0.0,0.08,0.16,0.25,0.75,0.84,0.92,1.0}){
                double edge=Math.Min(t,1-t);
                byte a=(byte)Math.Round(66*(1-UiMotion.Unit((edge-0.06)/0.19)));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(a,0,0,0),t));
            }
            b.Freeze();return b;
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { if (index != 0) throw new ArgumentOutOfRangeException(nameof(index)); return clipped; }
        protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); if (!ManualSize) SizeFace(info.NewSize); }
        protected void SizeFace(Size size)
        {
            if (faceSize==size) return;
            faceSize=size; Draw();
        }
        static void Changed(DependencyObject o,DependencyPropertyChangedEventArgs e)
        {
            var lens=o as LiquidLens;
            if (lens!=null && !ReferenceEquals(e.OldValue,e.NewValue)) lens.Draw();
        }
        static void LightChanged(DependencyObject o,DependencyPropertyChangedEventArgs e)
        {
            var lens=(LiquidLens)o;
            lens.ActiveEffect?.Finish(lens.SurfaceStrength,lens.Pressed);
        }
        double SurfaceStrength { get { return knobTrack==null ? Strength : Strength*0.38; } }

        void Draw()
        {
            if (drawing || input==null || faceSize.Width < 1 || faceSize.Height < 1) return;
            drawing=true;
            try{
                var rect = new Rect(faceSize);
                double r = Math.Min(Radius,Math.Min(rect.Width,rect.Height)/2);
                var shape = new RectangleGeometry(rect,r,r); shape.Freeze(); clipped.Clip=shape;
                bool glass=GetGlassEnabled(this);
                if(knobTrack!=null && (knobClarity==0 || !glass)){
                    input.Effect=null;
                    captionImage.Opacity=0;
                    using(var dc=input.RenderOpen())dc.DrawRectangle(glass ? RestFill : Tint ?? SystemColors.WindowTextBrush,null,rect);
                    if(knobSurface!=null)using(knobSurface.RenderOpen()){}
                    return;
                }
                if(glass && (SampleScene || knobTrack!=null))MapScene(null,EventArgs.Empty);
                captionImage.Opacity=glass&&tiny?1:0;
                var optical=ActiveEffect;
                input.Effect=glass ? optical : null;
                if (glass && optical!=null) { optical.Device(this);optical.Size(rect.Width,rect.Height,r); optical.Finish(SurfaceStrength,Pressed); }
                using (var dc=input.RenderOpen())
                {
                    if (glass && (SampleScene || knobTrack!=null))
                    {
                        if(!directTexture)dc.DrawRectangle(sample,null,rect);
                        dc.DrawRectangle(projectedFilm,null,rect);
                        var coat = GetSceneCoat(this);
                        if(coat != null) dc.DrawRectangle(coat,null,rect);
                    }
                    if (glass && knobSample!=null)
                    {
                        dc.PushOpacity(0.985);
                        dc.DrawRectangle(knobSample,null,rect);
                        dc.Pop();
                    }
                    if(glass && this is CapsuleThumb && !tiny && (knobClarity>0 || Pressed)){
                        dc.PushOpacity(.67);dc.DrawRectangle(incidentStars,null,rect);dc.Pop();
                    }
                    double film=TintOpacity*(knobTrack!=null ? 0.14-0.05*knobClarity : 1);
                    if (glass && film<1) dc.PushOpacity(film);
                    dc.DrawRectangle(Tint ?? Brushes.Transparent,null,rect);
                    if (glass && film<1) dc.Pop();
                    if (glass) {
                        if(knobTrack!=null) {
                            dc.DrawRectangle(knobShade,null,rect);
                            dc.PushOpacity(0.45);
                        }
                        dc.DrawRectangle(reflection,null,rect);
                        if(knobTrack!=null){
                            dc.Pop();
                        }
                    }
                }
                if(knobSurface!=null){
                    using(var dc=knobSurface.RenderOpen())if(glass){
                        dc.DrawRectangle(knobContact,null,rect);
                        var rim=rect;rim.Inflate(-0.4,-0.4);
                        if(!rim.IsEmpty)dc.DrawRoundedRectangle(null,knobEdge,rim,r,r);
                        if(knobClarity<1){
                            dc.PushOpacity(1-knobClarity);
                            dc.DrawRectangle(RestFill,null,rect);
                            dc.Pop();
                        }
                    }
                }
            }finally{drawing=false;}
        }

        internal void SampleTrack(Border track, Rect aperture, double clarity)
        {
            bool trackChanged=knobTrack!=track || (knobRail!=null)!=(track.Background!=null);
            bool redraw=trackChanged || knobClarity!=clarity || faceSize!=aperture.Size;
            if (trackChanged)
            {
                knobTrack=track;
                if(knobSurface==null){knobSurface=new DrawingVisual();clipped.Children.Add(knobSurface);}
                // A scroll thumb refracts the list background alone. A switch also has a painted rail.
                knobRail=track.Background==null ? null : new GeometryDrawing();
                knobSample=knobRail==null ? null : GlassSampling.Sampler();
                if(knobSample!=null)knobSample.Drawing=knobRail;
            }
            if(knobRail!=null && knobRail.Brush!=track.Background)knobRail.Brush=track.Background;
            if(knobRail!=null && clarity>0 && (redraw || knobTrackSize!=track.RenderSize || knobSample.Viewbox!=aperture)){
                knobTrackSize=track.RenderSize;
                knobRail.Geometry=RefractedRail(knobTrackSize,aperture,clarity);
                knobSample.Viewbox=aperture;
            }
            knobField=aperture;
            double extra=(0.70+0.40*clarity)/2;
            double sampleHeight=knobRail==null ? Math.Min(aperture.Width,aperture.Height) : aperture.Height;
            knobField.Inflate(aperture.Width*extra,sampleHeight*extra);
            faceSize=aperture.Size;
            knobClarity=clarity;
            if(redraw)Draw();
            else if(IsLoaded)MapScene(null,EventArgs.Empty);
        }

        // The near rail contracts vertically through the middle of the lens and fans back out
        // where it enters the curved rim. Its rounded end remains attached to the physical rail.
        static Geometry RefractedRail(Size track, Rect lens,double lift)
        {
            double inset=track.Height*(0.395-0.625*lift);
            double left=inset,right=track.Width-inset,mid=track.Height/2;
            double radius=Math.Min(lens.Height*(0.295-0.0125*lift), (right-left)/2);
            if(radius<=0 || lens.Width<=0)return Geometry.Empty;
            var outline=new StreamGeometry();
            using(var path=outline.Open()){
                Point Surface(double x,double dy){
                    double u=(x-lens.Left)/lens.Width;
                    double edge=UiMotion.Unit((Math.Min(u,1-u)-0.145)/0.075);
                    double flare=1-Math.Sqrt(edge*(2-edge));
                    return new Point(x,mid+dy*(1+0.72*flare));
                }
                path.BeginFigure(new Point(left,mid),true,true);
                for(int side=0;side<2;side++){
                    double from=side==0?left:right,to=side==0?right:left;
                    int direction=side==0?1:-1;
                    for(int i=1;i<=12;i++){
                        double angle=i*Math.PI/24;
                        path.LineTo(Surface(from+direction*radius*(1-Math.Cos(angle)),-direction*radius*Math.Sin(angle)),true,false);
                    }
                    for(int i=1;i<=36;i++){
                        double length=right-left-2*radius;
                        path.LineTo(Surface(from+direction*(radius+length*i/36),-direction*radius),true,false);
                    }
                    for(int i=1;i<=12;i++){
                        double angle=i*Math.PI/24;
                        path.LineTo(Surface(to-direction*radius*(1-Math.Sin(angle)),-direction*radius*Math.Cos(angle)),true,false);
                    }
                }
            }
            outline.Freeze();return outline;
        }
        internal void FollowTransform(Transform transform)
        {
            followsTransform=true;
            // Render transforms move the lens without a layout pass, even over a stationary sky.
            transform.Changed+=(s,e)=>{if(IsLoaded && IsVisible)MapScene(null,EventArgs.Empty);};
        }

        static readonly bool releaseMovingCache=Environment.GetEnvironmentVariable("ASTRALSPLIT_DYNAMIC_CACHE")=="1";
        static readonly bool gpuCaption=Environment.GetEnvironmentVariable("ASTRALSPLIT_LEGACY_CAPTION")!="1";
        Rect cacheArea=Rect.Empty;
        System.Windows.Threading.DispatcherTimer cacheIdle;
        System.Windows.Media.CacheMode savedCache;
        int lastMotion;
        void TrackCache(Rect area){
            if(!releaseMovingCache || tiny || area==cacheArea || !IsLoaded)return;
            bool moved=!cacheArea.IsEmpty;cacheArea=area;if(!moved)return;
            if(savedCache==null){
                if(clipped.CacheMode==null)return;
                savedCache=clipped.CacheMode;clipped.CacheMode=null;
                if(cacheIdle==null){cacheIdle=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background,Dispatcher);
                    cacheIdle.Interval=TimeSpan.FromMilliseconds(100);cacheIdle.Tick+=(s,e)=>{if(unchecked((uint)(Environment.TickCount-lastMotion))>=100)RestoreMotionCache();};}
                cacheIdle.Start();
            }
            lastMotion=Environment.TickCount;
        }
        void RestoreMotionCache(){
            cacheIdle?.Stop();
            if(savedCache!=null){if(!tiny)clipped.CacheMode=savedCache;savedCache=null;}
        }
        void MapScene(object sender,EventArgs e)
        {
            if ((knobTrack==null ? !SampleScene : knobClarity==0) || !GetGlassEnabled(this) || faceSize.IsEmpty || faceSize.Width<1) return;
            var source=GetScene(this);
            var pane=source as FrostedPane;
            var next=source as StarfieldLayer ?? pane?.Backdrop as StarfieldLayer;
            if(next!=projection)
            {
                if(projection!=null)projection.SamplingChanged-=MapScene;
                projection=next;
                if(projection!=null && IsLoaded)projection.SamplingChanged+=MapScene;
            }
            FrameworkElement anchor=knobTrack ?? (FrameworkElement)this;
            Rect field=knobTrack!=null ? knobField : new Rect(faceSize);
            if(this is CapsuleThumb && projection!=null && anchor.FindCommonVisualAncestor(projection)!=null){
                var bounds=anchor.TransformToVisual(projection).TransformBounds(field);
                if(incidentStars.Viewbox!=bounds)incidentStars.Viewbox=bounds;
                if(incidentStars.Drawing!=projection.OpticalEmission)incidentStars.Drawing=projection.OpticalEmission;
            }
            if (!GlassSampling.Map(anchor,source,sample,field,projection,knobTrack!=null,followsTransform)) sample.Drawing=null;
            bool wasDirect=directTexture,wasTiny=tiny;
            var atlas=projection?.ControlMap;
            directTexture=atlas!=null && GlassEffect.CanProject && projection.ActualWidth>0 && projection.ActualHeight>0 && sample.Drawing!=null;
            tiny=directTexture && faceSize.Width<=16 && faceSize.Width==faceSize.Height;
            if(pane==null)projectedFilm.Color=Colors.Transparent;
            if(sender!=projection && pane!=null && projection!=null && FindCommonVisualAncestor(pane)!=null){
                var coat=pane.Film as LinearGradientBrush;
                if(coat!=null && pane.ActualHeight>0){
                    double y=anchor.TranslatePoint(new Point(field.X+field.Width/2,field.Y+field.Height/2),pane).Y/pane.ActualHeight;
                    var stops=coat.GradientStops;int i=1;while(i<stops.Count-1 && stops[i].Offset<y)i++;
                    var a=stops[i-1];var b=stops[i];var t=UiMotion.Unit((y-a.Offset)/(b.Offset-a.Offset));
                    projectedFilm.Color=Color.FromArgb((byte)Math.Round(a.Color.A+(b.Color.A-a.Color.A)*t),255,255,255);
                }
            }
            if(directTexture){
                var area=sample.Viewbox;
                TrackCache(area);
                if(tiny && gpuCaption && GlassEffect.Hardware3(this) && CaptionGlassShader.Available){
                    if(captionShader==null)captionShader=new CaptionGlassShader();
                    if(captionImage.Effect!=captionShader || gpuFace!=faceSize){
                        using(var dc=captionImage.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(faceSize));
                        captionImage.Effect=captionShader;gpuFace=faceSize;
                    }
                    captionShader.Update(atlas.Brush,area,projection.RenderSize,faceSize);
                } else if(tiny){
                    if(captionImage.Effect!=null){captionImage.Effect=null;caption=null;}
                    var dpi=VisualTreeHelper.GetDpi(this);
                    int w=Math.Max(1,(int)Math.Ceiling(faceSize.Width*dpi.DpiScaleX*ArtworkResolution.Ratio)),h=Math.Max(1,(int)Math.Ceiling(faceSize.Height*dpi.DpiScaleY*ArtworkResolution.Ratio));
                    if(caption==null || caption.Image.PixelWidth!=w || caption.Image.PixelHeight!=h){
                        caption=new CaptionRefraction(faceSize,w,h);
                        using(var dc=captionImage.RenderOpen())dc.DrawImage(caption.Image,new Rect(faceSize));
                    }
                    var tint=Tint as SolidColorBrush;
                    double transmission=(tint==null?1:1-tint.Color.A/255.0*TintOpacity)*(1-projectedFilm.Color.A/255.0);
                    caption.Update(atlas,area,projection.RenderSize,transmission);
                } else {
                    if(projectedEffect==null)projectedEffect=new GlassEffect(true);
                    projectedEffect.Sample(atlas.Brush,area,projection.RenderSize);
                }
            }
            if(tiny!=wasTiny){
                captionImage.Opacity=tiny?1:0;
                clipped.CacheMode=tiny?null:new BitmapCache(ArtworkResolution.Ratio);
                coating.CacheMode=tiny?new BitmapCache(ArtworkResolution.Ratio):null;
            }
            if((wasDirect!=directTexture || wasTiny!=tiny) && !drawing)Draw();
        }
        void Connect()
        {
            Disconnect();
            if (SampleScene || ManualSize) { layoutGroup=GlassLayout.Connect(this,MapScene); mapAttached=true; }
            for (DependencyObject v=VisualTreeHelper.GetParent(this); v!=null; v=VisualTreeHelper.GetParent(v))
                if (v is Control) { owner=(FrameworkElement)v; break; }
            if (owner!=null) { owner.MouseMove+=Pointer; owner.MouseLeave+=Leave; }
        }
        void Disconnect()
        {
            RestoreMotionCache();cacheArea=Rect.Empty;
            if(projection!=null){projection.SamplingChanged-=MapScene;projection=null;}
            incidentStars.Drawing=null;
            if (mapAttached) { layoutGroup?.Disconnect(MapScene);layoutGroup=null;mapAttached=false; }
            if (owner!=null) { owner.MouseMove-=Pointer; owner.MouseLeave-=Leave; owner=null; }
            ResetLight(false);
        }
        void Pointer(object sender,MouseEventArgs e)
        {
            var optical=ActiveEffect;
            if (optical==null || !GetGlassEnabled(this) || !UiMotion.CanAnimate(this) || owner.ActualWidth<1 || owner.ActualHeight<1) return;
            var p=e.GetPosition(owner);
            var direction=new Vector(-0.54+0.84*(2*p.X/owner.ActualWidth-1),-1+0.64*(2*p.Y/owner.ActualHeight-1));
            direction.Normalize();
            optical.BeginAnimation(GlassEffect.LightProperty,null);
            optical.SetValue(GlassEffect.LightProperty,new Point(direction.X,direction.Y));
        }
        void Leave(object sender,MouseEventArgs e) { ResetLight(true); }
        void ResetLight(bool animate)
        {
            var optical=ActiveEffect;if(optical==null)return;
            var current=(Point)optical.GetValue(GlassEffect.LightProperty);
            var rest=new Point(-0.54,-0.84);
            optical.SetValue(GlassEffect.LightProperty,rest);
            optical.BeginAnimation(GlassEffect.LightProperty,animate && current!=rest && UiMotion.CanAnimate(this) ?
                UiMotion.AtRefresh(new PointAnimation(current,rest,TimeSpan.FromMilliseconds(150)) { FillBehavior=FillBehavior.Stop },DisplayRefresh.For(this)) : null);
        }
    }

    public sealed class FrostedEdge : Border
    {
        readonly DrawingBrush sample=GlassSampling.Sampler();
        GlassLayout layoutGroup;
        public FrostedEdge()
        {
            Background=sample;
            Loaded+=(s,e)=>{ layoutGroup=GlassLayout.Connect(this,Align); Align(null,EventArgs.Empty); };
            Unloaded+=(s,e)=>{ layoutGroup?.Disconnect(Align);layoutGroup=null; sample.Drawing=null; };
        }
        void Align(object s,EventArgs e)
        {
            if (ActualWidth<=0 || ActualHeight<=0 || !GlassSampling.Map(this,LiquidLens.GetScene(this),sample,new Rect(RenderSize))) sample.Drawing=null;
        }
    }

    public class FrostedPane : FrameworkElement
    {
        public static readonly DependencyProperty FilmProperty=DependencyProperty.Register(nameof(Film),typeof(Brush),typeof(FrostedPane),new PropertyMetadata(null,Paint));
        public static readonly DependencyProperty BackdropProperty=DependencyProperty.Register(nameof(Backdrop),typeof(FrameworkElement),typeof(FrostedPane),new PropertyMetadata(null,Paint));
        public Brush Film { get { return (Brush)GetValue(FilmProperty); } set { SetValue(FilmProperty,value); } }
        public FrameworkElement Backdrop { get { return (FrameworkElement)GetValue(BackdropProperty); } set { SetValue(BackdropProperty,value); } }
        internal const double BlurRadius=6;
        protected virtual double SurfaceRadius {get{return 14;}}
        const double Padding=20;
        readonly ContainerVisual aperture=new ContainerVisual();
        readonly ContainerVisual mount=new ContainerVisual();
        protected readonly ContainerVisual Coating=new ContainerVisual();
        protected readonly ContainerVisual LensSurface=new ContainerVisual();
        protected ContainerVisual Material { get { return aperture; } }
        readonly DrawingVisual frost=new DrawingVisual(), film=new DrawingVisual();
        readonly DrawingGroup transmitted=new DrawingGroup();
        protected readonly DrawingGroup RimMaterial=new DrawingGroup();
        readonly DrawingVisual sharedRimBase=new DrawingVisual();
        readonly BitmapCacheBrush sharedRimBrush;
        static readonly bool stageRim=Environment.GetEnvironmentVariable("ASTRALSPLIT_STATIC_RIM")=="1";
        internal Drawing TransmittedMaterial { get { return transmitted; } }
        readonly ContainerVisual movingLight=new ContainerVisual();
        StarfieldLayer projectedSky;
        readonly DrawingBrush sample=GlassSampling.Sampler();
        readonly BlurEffect blur;
        readonly TranslateTransform skyOrigin=new TranslateTransform();
        Rect expanded;
        GlassLayout layoutGroup;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Visual,FrostedPane> nativeFrost=new System.Runtime.CompilerServices.ConditionalWeakTable<Visual,FrostedPane>();
        internal static bool FindFrost(Visual visual,out FrostedPane owner){return nativeFrost.TryGetValue(visual,out owner);}
        public FrostedPane()
        {
            nativeFrost.Add(frost,this);
            if(stageRim){
                sharedRimBase.CacheMode=new BitmapCache(ArtworkResolution.Ratio);
                sharedRimBrush=new BitmapCacheBrush(sharedRimBase){AutoLayoutContent=false,BitmapCache=null};
            }
            IsHitTestVisible=false; AddVisualChild(mount);mount.Children.Add(LensSurface);mount.Children.Add(movingLight);mount.Children.Add(Coating);LensSurface.Children.Add(aperture);
            LensSurface.CacheMode=new BitmapCache(ArtworkResolution.Ratio);
            aperture.Children.Add(frost); aperture.Children.Add(film);
            blur=new BlurEffect { Radius=BlurRadius, KernelType=KernelType.Gaussian, RenderingBias=RenderingBias.Quality };
            blur.Freeze(); frost.Effect=blur;
            Loaded+=(s,e)=>{ layoutGroup=GlassLayout.Connect(this,Align); Draw(); };
            Unloaded+=(s,e)=>{
                layoutGroup?.Disconnect(Align);layoutGroup=null;sample.Drawing=null;
                if(this is LiquidSheet)projectedSky?.CoverWithGlass(this,Rect.Empty);
                projectedSky?.DetachLight(movingLight);projectedSky=null;
            };
            IsVisibleChanged+=(s,e)=>{if(this is LiquidSheet && Backdrop is StarfieldLayer sky){if(IsVisible)Align(null,EventArgs.Empty);else sky.CoverWithGlass(this,Rect.Empty);}};
        }
        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { if(index!=0) throw new ArgumentOutOfRangeException(nameof(index)); return mount; }
        protected override void OnRenderSizeChanged(SizeChangedInfo i) { base.OnRenderSizeChanged(i); Draw(); }
        static void Paint(DependencyObject o,DependencyPropertyChangedEventArgs e) { ((FrostedPane)o).Draw(); }
        void Align(object s,EventArgs e)
        {
            if(Backdrop is StarfieldLayer sky)
            {
                Point origin;
                if(FindCommonVisualAncestor(sky)!=null)origin=TranslatePoint(new Point(),sky);
                else if(LiquidLens.GetGlassEnabled(this) && GlassSampling.Map(this,sky,sample,new Rect(RenderSize)))
                    origin=sample.Viewbox.TopLeft;
                else return;
                if(skyOrigin.X!=-origin.X) skyOrigin.X=-origin.X;
                if(skyOrigin.Y!=-origin.Y) skyOrigin.Y=-origin.Y;
                if(this is LiquidSheet)sky.CoverWithGlass(this,IsVisible && LiquidLens.GetGlassEnabled(this)?new Rect(origin,RenderSize):Rect.Empty);
                return;
            }
            if (expanded.Width<=0 || !LiquidLens.GetGlassEnabled(this) || !GlassSampling.Map(this,Backdrop,sample,expanded)) sample.Drawing=null;
        }
        protected void Draw()
        {
            if (frost==null || ActualWidth<1 || ActualHeight<1) return;
            var sky=Backdrop as StarfieldLayer;
            frost.Effect=sky==null ? blur : null; frost.Transform=sky==null ? null : skyOrigin;
            var rect=new Rect(RenderSize);
            var box=new RectangleGeometry(rect);box.Freeze();aperture.Clip=box;
            var clip=new RectangleGeometry(rect,SurfaceRadius,SurfaceRadius); clip.Freeze();mount.Clip=clip;
            expanded=rect; expanded.Inflate(Padding,Padding); Align(null,EventArgs.Empty);
            using(var dc=transmitted.Open())
            {
                if(sky!=null && LiquidLens.GetGlassEnabled(this))
                {
                    dc.PushTransform(skyOrigin);dc.DrawDrawing(sky.FrostSky);dc.Pop();
                }
                dc.DrawRectangle(Film,null,rect);
            }
            if(stageRim)using(var cached=sharedRimBase.RenderOpen()){
                cached.DrawRectangle(Brushes.Transparent,null,rect);
                if(sky!=null && LiquidLens.GetGlassEnabled(this)){
                    cached.PushClip(new RectangleGeometry(rect));cached.PushTransform(skyOrigin);
                    cached.DrawDrawing(sky.FrostSky);cached.DrawDrawing(sky.RimStarlight);cached.Pop();cached.Pop();
                }
            }
            using(var dc=RimMaterial.Open())
            {
                if(sky!=null && LiquidLens.GetGlassEnabled(this))
                {
                    if(stageRim)dc.DrawRectangle(sharedRimBrush,null,rect);
                    dc.PushTransform(skyOrigin);if(!stageRim){dc.DrawDrawing(sky.FrostSky);dc.DrawDrawing(sky.RimStarlight);}
                    dc.DrawDrawing(sky.OpticalEmission);dc.Pop();
                }
                dc.DrawRectangle(Film,null,rect);
            }
            var lightSky=LiquidLens.GetGlassEnabled(this) && IsLoaded ? sky : null;
            if(lightSky!=projectedSky)
            {
                projectedSky?.DetachLight(movingLight); projectedSky=lightSky;
                projectedSky?.AttachLight(movingLight);
            }
            movingLight.Transform=skyOrigin;
            using (var dc=frost.RenderOpen())
            {
                if(sky!=null) {if(LiquidLens.GetGlassEnabled(this))dc.DrawDrawing(sky.FrostSky);}
                else dc.DrawRectangle(sample,null,expanded);
            }
            using (var dc=film.RenderOpen()) dc.DrawRectangle(Film,null,rect);
        }
    }
}
