using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        // Only the unwarped sibling is clipped. The sampler sees the complete live source,
        // including the solar limb, the volume, stars and milestones outside the aperture.
        const double LensRadius=90;
        readonly ContainerVisual transmittedSky=new ContainerVisual();
        int waveConsumers;
        CacheMode oldWaveCache;
        internal void ShareWaveSource(bool subscribe){
            if(subscribe){
                if(waveConsumers++==0){
                    oldWaveCache=transmittedSky.CacheMode;
                    if(oldWaveCache==null)transmittedSky.CacheMode=new BitmapCache(1);
                }
            }else if(waveConsumers>0 && --waveConsumers==0){transmittedSky.CacheMode=oldWaveCache;oldWaveCache=null;}
        }
        internal Visual TransmittedSky {get{return transmittedSky;}}
        readonly ContainerVisual naturalSky=new ContainerVisual(),straightSky=new ContainerVisual();
        readonly RectangleGeometry lensWindow=new RectangleGeometry(),lensBox=new RectangleGeometry();
        readonly VisualBrush skySample=new VisualBrush{AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        readonly DrawingBrush flyerSample=new DrawingBrush{ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        CombinedGeometry outsideLens;
        readonly DrawingVisual straightTiles=new DrawingVisual();
        readonly ContainerVisual localStraight=new ContainerVisual();
        readonly RectangleGeometry perimeter=new RectangleGeometry();
        Rect integerPatch;
        // Snap outside the lens to a coarse physical grid. Pointer parallax can then move the
        // fine lens mask without constructing new tile brushes on every tracking frame.
        Rect previouslyDrawn;
        DpiScale tileDpi;
        bool TryGetPerimeter(Rect window,Rect box,out Rect result){
            result=Rect.Empty;
            if(!observing || observationCameraMoving || !window.Contains(box) || box.IsEmpty)return false;
            var source=PresentationSource.FromVisual(this);
            if(source==null || source.CompositionTarget==null || !(source.RootVisual is Visual root))return false;
            // Window expansion applies a transformed camera to the live scene. Keep its original
            // mask until each tile edge can be aligned to a physical destination pixel.
            var toRoot=TransformToAncestor(root);
            var device=source.CompositionTarget.TransformToDevice;
            Point origin=device.Transform(toRoot.Transform(new Point()));
            Point ux=device.Transform(toRoot.Transform(new Point(1,0)));
            Point vy=device.Transform(toRoot.Transform(new Point(0,1)));
            var dx=ux-origin;var dy=vy-origin;
            var dpi=VisualTreeHelper.GetDpi(this);
            if(System.Math.Abs(dx.X-dpi.DpiScaleX)>1e-6 || System.Math.Abs(dy.Y-dpi.DpiScaleY)>1e-6 ||
                System.Math.Abs(dx.Y)>1e-7 || System.Math.Abs(dy.X)>1e-7 ||
                System.Math.Abs(origin.X-System.Math.Round(origin.X))>1e-6 ||
                System.Math.Abs(origin.Y-System.Math.Round(origin.Y))>1e-6)return false;
            double l=(256*System.Math.Floor((box.Left*dx.X+origin.X-2)/256)-origin.X)/dx.X;
            double t=(256*System.Math.Floor((box.Top*dy.Y+origin.Y-2)/256)-origin.Y)/dy.Y;
            double r=(256*System.Math.Ceiling((box.Right*dx.X+origin.X+2)/256)-origin.X)/dx.X;
            double b=(256*System.Math.Ceiling((box.Bottom*dy.Y+origin.Y+2)/256)-origin.Y)/dy.Y;
            result=Rect.Intersect(window,new Rect(new Point(l,t),new Point(r,b)));return !result.IsEmpty;
        }
        void SetGutters(Rect window,Rect h){
            var dpi=VisualTreeHelper.GetDpi(this);
            if(perimeter.Rect==h && previouslyDrawn==window && tileDpi.DpiScaleX==dpi.DpiScaleX && tileDpi.DpiScaleY==dpi.DpiScaleY)return;
            perimeter.Rect=h;previouslyDrawn=window;tileDpi=dpi;
            using(var dc=straightTiles.RenderOpen()){
                DrawZone(dc,new Rect(window.Left,window.Top,window.Width,h.Top-window.Top),dpi);
                DrawZone(dc,new Rect(window.Left,h.Bottom,window.Width,window.Bottom-h.Bottom),dpi);
                DrawZone(dc,new Rect(window.Left,h.Top,h.Left-window.Left,h.Height),dpi);
                DrawZone(dc,new Rect(h.Right,h.Top,window.Right-h.Right,h.Height),dpi);
            }
        }
        void DrawZone(DrawingContext dc,Rect zone,DpiScale dpi){
            if(zone.Width<=0 || zone.Height<=0)return;
            // Keeping the sampled window local prevents WPF from minifying a wide brush
            // intermediate. Extra source pixels preserve bilinear taps at shared edges.
            double right=zone.Right,bottom=zone.Bottom;
            for(double y=zone.Top;y<bottom;){
                double nextY=System.Math.Min(bottom,y+1024/dpi.DpiScaleY);
                for(double x=zone.Left;x<right;){
                    double nextX=System.Math.Min(right,x+1024/dpi.DpiScaleX);
                    var tile=new Rect(x,y,nextX-x,nextY-y);
                    var sample=tile;sample.Inflate(4/dpi.DpiScaleX,4/dpi.DpiScaleY);
                    var brush=new VisualBrush(naturalSky){AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,
                        ViewportUnits=BrushMappingMode.Absolute,Viewport=sample,Viewbox=sample,Stretch=Stretch.Fill,TileMode=TileMode.None};
                    var clip=new RectangleGeometry(tile);clip.Freeze();
                    dc.PushClip(clip);dc.DrawRectangle(brush,null,sample);dc.Pop();x=nextX;
                }
                y=nextY;
            }
        }
        static readonly bool testFullLens=System.Environment.GetEnvironmentVariable("ASTRALSPLIT_FULL_LENS")=="1";
        readonly ContainerVisual windowEffect=new ContainerVisual(),windowInput=new ContainerVisual();
        readonly DrawingVisual windowFlyers=new DrawingVisual();
        bool windowLensActive;
        bool SwitchWindowLens(Rect window,Rect box){
            bool use=testFullLens && !CompositionObservatory.Requested && observing && !observationCameraMoving &&
                !box.IsEmpty && window.Contains(box) && GlassEffect.Hardware3(this);
            if(use){
                starOptics.PrepareWindow(new Rect(box.X/window.Width,box.Y/window.Height,box.Width/window.Width,box.Height/window.Height));
                use=starOptics.FullScene!=null;
            }
            if(windowLensActive!=use){
                windowLensActive=use;
                if(use){
                    straightSky.Children.Remove(naturalSky);windowInput.Children.Insert(0,naturalSky);
                    windowEffect.Effect=starOptics.FullScene;transmittedSky.Children.Add(windowEffect);
                }else{
                    transmittedSky.Children.Remove(windowEffect);windowInput.Children.Remove(naturalSky);
                    straightSky.Children.Add(naturalSky);windowEffect.Effect=null;
                }
            }
            if(use){
                windowInput.Clip=new RectangleGeometry(window);
                using(var dc=windowFlyers.RenderOpen()){
                    dc.DrawRectangle(Brushes.Transparent,null,window);
                    dc.DrawGeometry(flyerSample,null,lensBox);
                }
                localStraight.Clip=null;straightSky.Clip=null;straightSky.Opacity=straightTiles.Opacity=lensPatch.Opacity=0;
            }
            return use;
        }
        void InitializeLensPatch(){
            windowInput.Children.Add(windowFlyers);windowEffect.Children.Add(windowInput);
            outsideLens=new CombinedGeometry(GeometryCombineMode.Exclude,lensWindow,lensBox);
            skySample.Visual=naturalSky;
            var flyers=new DrawingGroup{Transform=mapShift};flyers.Children.Add(lensedFlyers);flyerSample.Drawing=flyers;
            using(var dc=lensPatch.RenderOpen()){
                dc.DrawGeometry(skySample,null,lensBox);
                dc.DrawGeometry(flyerSample,null,lensBox);
            }
        }
    }
}
