using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    // Translate retained WPF source drawings into an ordered GPU graph. A source reused
    // by lenses is drawn once per packet, with its own coordinate space and alpha.
    internal sealed partial class SceneGraph : IDisposable
    {
        internal static readonly Rect Unit=new Rect(0,0,1,1);
        readonly SceneDevice device;readonly SceneResources resources;readonly StarfieldLayer sky;
        readonly List<SceneDevice.Pass> ordered=new List<SceneDevice.Pass>();
        readonly List<SceneDevice.Pass> passPool=new List<SceneDevice.Pass>();
        readonly Dictionary<Tuple<object,Rect,int,int>,int> layers=new Dictionary<Tuple<object,Rect,int,int>,int>();
        readonly HashSet<object> traversing=new HashSet<object>();
        readonly Dictionary<Visual,Rect> boundsThisPacket=new Dictionary<Visual,Rect>();
        readonly Dictionary<Visual,DrawingGroup> drawingsThisPacket=new Dictionary<Visual,DrawingGroup>();
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,RetainedDrawing> retained=new System.Runtime.CompilerServices.ConditionalWeakTable<object,RetainedDrawing>();
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,PassIdentity> passIdentities=new System.Runtime.CompilerServices.ConditionalWeakTable<object,PassIdentity>();
        sealed class PassIdentity {internal ulong Value;}
        enum LayerRole {
            Main, EffectInput, EffectOutput, Masked, ShapeMask, ShadowInput, ShadowWidth, ShadowHeight,
            ScrollStrip, SolarLoop0, SolarLoop1, SolarLoop2, Frost0, Frost1, Frost2, Frost3
        }
        ulong nextPassIdentity;
        ulong PassKey(object owner,LayerRole role=LayerRole.Main){
            if(owner==null)return 0;
            if(!passIdentities.TryGetValue(owner,out var identity))
                passIdentities.Add(owner,identity=new PassIdentity{Value=++nextPassIdentity});
            return identity.Value<<8 | (uint)role;
        }
        sealed class RetainedDrawing {
            public RetainedDrawing(){}
            internal Rect Area,Clip;
            internal int W,H,Count;
            internal Matrix Transform;
            internal double Alpha,Spin;
            internal Brush Ink;
            internal SceneDevice.Command[] Commands;
            internal bool Ready,Isolated;
        }
        internal int ReusedCommands{get;private set;}
        readonly bool liveFrost=Environment.GetEnvironmentVariable("ASTRALSPLIT_SCENE_FROST")=="live";
        readonly bool fuseLens=Environment.GetEnvironmentVariable("ASTRALSPLIT_SCENE_FUSE_LENS")!="0";
        internal long ProfilePackets{get;private set;}
        internal long WalkTicks{get;private set;}
        internal long SubmitTicks{get;private set;}
        internal long CollectTicks{get;private set;}
        internal long LensPassesSaved{get;private set;}
        int slots,frostOutput;bool disposed;
        internal int Fallbacks{get;private set;}
        internal string Profile{get{return resources.Profile();}}
        internal int Uploads{get{return resources.Uploads;}}
        internal SceneGraph(SceneDevice d,StarfieldLayer field){device=d;resources=new SceneResources(d,field);sky=field;FlowBatch.TrackNative(true);}
        internal long Publish(Visual root,Rect client){
            long started=SceneDevice.Profiling?Stopwatch.GetTimestamp():0;
            ordered.Clear();layers.Clear();traversing.Clear();boundsThisPacket.Clear();drawingsThisPacket.Clear();slots=1;frostOutput=0;Fallbacks=0;resources.Begin();
            ReusedCommands=0;var pass=PassAt(0,client,device.Width,device.Height);
            seenScroll=false;
            VisualNode(root,pass,Matrix.Identity,client,1,false);
            FinishScrollPacket();
            ordered.Add(pass);
            long walked=SceneDevice.Profiling?Stopwatch.GetTimestamp():0;
            long seq=device.Submit(ordered);
            long submitted=SceneDevice.Profiling?Stopwatch.GetTimestamp():0;
            resources.Collect();
            if(SceneDevice.Profiling){
                ++ProfilePackets;WalkTicks+=walked-started;SubmitTicks+=submitted-walked;CollectTicks+=Stopwatch.GetTimestamp()-submitted;
            }
            return seq;
        }
        internal long PublishDrawing(Drawing drawing,Rect area){
            ordered.Clear();layers.Clear();traversing.Clear();boundsThisPacket.Clear();drawingsThisPacket.Clear();slots=1;frostOutput=0;Fallbacks=0;resources.Begin();
            ReusedCommands=0;var pass=PassAt(0,area,device.Width,device.Height);
            Draw(drawing,pass,Matrix.Identity,area,1,0);ordered.Add(pass);long seq=device.Submit(ordered);resources.Collect();return seq;
        }
        internal static bool Valid(Rect a){return !a.IsEmpty && a.Width>0 && a.Height>0 && UiMotion.IsFinite(a.Width+a.Height+a.X+a.Y);}
        static Matrix Join(Matrix local,Matrix parent){local.Append(parent);return local;}
        static double Pixels(SceneDevice.Pass p,Matrix m){
            var x=m.Transform(new Vector(1,0));var y=m.Transform(new Vector(0,1));
            return Math.Max(.001,Math.Max(new Vector(x.X*p.Sx,x.Y*p.Sy).Length,new Vector(y.X*p.Sx,y.Y*p.Sy).Length));
        }
        static bool Axis(Matrix m){return Math.Abs(m.M12)+Math.Abs(m.M21)<.0000001;}
        static bool Rectangle(Geometry g,out Rect a){
            if(g is RectangleGeometry r && r.RadiusX==0 && r.RadiusY==0 && (r.Transform==null || r.Transform.Value.IsIdentity)){
                a=r.Rect;return true;
            }
            a=Rect.Empty;return false;
        }
        Rect Bounds(Visual v){
            if(!boundsThisPacket.TryGetValue(v,out var area)){
                area=VisualTreeHelper.GetDescendantBounds(v);area.Union(VisualTreeHelper.GetContentBounds(v));boundsThisPacket.Add(v,area);
            }
            return area;
        }
        DrawingGroup OwnDrawing(Visual visual){
            if(insideScroll && visual is ApplicationIcon icon && icon.Source?.IsFrozen==true)return IconDrawing(icon);
            if(!drawingsThisPacket.TryGetValue(visual,out var drawing)){
                drawing=VisualTreeHelper.GetDrawing(visual);drawingsThisPacket.Add(visual,drawing);
            }
            return drawing;
        }
        static Rect Visible(Rect bounds,Matrix m,Rect clip){
            if(!m.HasInverse || !Valid(bounds))return Rect.Empty;m.Invert();bounds.Intersect(Rect.Transform(clip,m));return bounds;
        }
        static int Size(double size){return Math.Max(1,(int)Math.Min(8192,Math.Ceiling(size)));}
        SceneDevice.Pass PassAt(int slot,Rect area,int w,int h){
            if(slot==passPool.Count)passPool.Add(new SceneDevice.Pass(slot,area,w,h));
            else passPool[slot].Reset(slot,area,w,h);
            return passPool[slot];
        }
        int Layer(object key,Rect area,double density,Action<SceneDevice.Pass> paint,ulong identity=0,SceneDevice.Stage stage=SceneDevice.Stage.Layers){
            if(!Valid(area))return 0;
            int w=Size(area.Width*density),h=Size(area.Height*density);
            var id=key==null?null:Tuple.Create(key,area,w,h);
            if(id!=null && layers.TryGetValue(id,out int prior))return prior;
            if(slots>=384)throw new NotSupportedException("Too many independently composited WPF layers.");
            return LayerSized(id,area,w,h,paint,identity==0?PassKey(key):identity,stage);
        }
        int LayerSized(Tuple<object,Rect,int,int> id,Rect area,int w,int h,Action<SceneDevice.Pass> paint,
                       ulong identity=0,SceneDevice.Stage stage=SceneDevice.Stage.Layers){
            if(id!=null && layers.TryGetValue(id,out int prior))return prior;
            if(slots>=384)throw new NotSupportedException("Too many independently composited WPF layers.");
            var pass=PassAt(slots++,area,w,h);pass.Identity=identity;pass.Kind=stage;paint(pass);ordered.Add(pass);
            int result=-1-pass.Slot;if(id!=null)layers.Add(id,result);return result;
        }
        void VisualNode(Visual v,SceneDevice.Pass p,Matrix m,Rect clip,double alpha,bool placement=true){
            if(v==null || !Valid(clip) || (v is UIElement el && el.Visibility!=Visibility.Visible))return;
            if(placement){Matrix local=VisualTreeHelper.GetTransform(v)?.Value??Matrix.Identity;
                var offset=VisualTreeHelper.GetOffset(v);local.Translate(offset.X,offset.Y);m=Join(local,m);}
            if(insideScroll && v is AppRowSurface row && row.ClipToBounds &&
                !clip.IntersectsWith(Rect.Transform(new Rect(row.RenderSize),m)))return;
            if(liveFrost && sky!=null && FrostedPane.FindFrost(v,out var pane) && pane.Backdrop==sky && LiquidLens.GetGlassEnabled(pane)){
                var full=new Rect(sky.RenderSize);if(!Valid(full))return;
                if(frostOutput==0)frostOutput=FrostGraph(full);
                p.Draws.Add(SceneDevice.Quad(p,full,m,clip,frostOutput,Unit,alpha,0));return;
            }
            var opacity=VisualTreeHelper.GetOpacity(v);if(opacity<=0 || alpha<=0)return;
            var mask=VisualTreeHelper.GetOpacityMask(v);var g=VisualTreeHelper.GetClip(v);var fx=VisualTreeHelper.GetEffect(v);
            if(g!=null && Rectangle(g,out var rect) && Axis(m)){clip.Intersect(Rect.Transform(rect,m));g=null;}
            if(!Valid(clip))return;
            if(NativeScrolling && !insideScroll && ScrollSurface(v,p,m,clip,alpha,opacity,fx,mask,g))return;
            if(v is MilestoneFilm film && film.Procedural){
                device.EnsureMilestoneProgram();
                var quad=SceneDevice.Quad(p,film.Area,m,clip,0,Unit,alpha*opacity,24);
                var basis=film.Basis;var frame=film.Frame;
                quad.Constant(0,basis.X,basis.Y,basis.Z,basis.W);
                quad.Constant(1,frame.X,frame.Y,frame.Z,frame.W);
                quad.Constant(2,(int)film.Kind,film.Playing?1:0);
                p.Draws.Add(quad);
                return;
            }
            if(v==sky?.NativeTransmission){
                var area=new Rect(sky.RenderSize);
                double density=Pixels(p,m);
                bool lens=Valid(sky.NativePatch) && sky.NativeOptics!=null;
                bool standby=sky.NativeOptics?.StandbyOnly==true;
                // A 1:1 copy of the lens pass samples the same pixel centers as
                // drawing the lens directly. Scaled flights keep the separate pass.
                bool fused=lens && (standby || fuseLens && m.IsIdentity && p.Area==area &&
                    Size(area.Width*density)==p.W && Size(area.Height*density)==p.H);
                var id=NativeSky(area,density,!fused);
                if(id!=0){
                    if(fused){
                        p.Draws.Add(SkyLensQuad(p,area,m,clip,id,alpha*opacity));
                        if(SceneDevice.Profiling && !standby)++LensPassesSaved;
                    }
                    else p.Draws.Add(SceneDevice.Quad(p,area,m,clip,id,Unit,alpha*opacity,0));
                }
                return;
            }
            if(fx==null && mask==null && g==null && (opacity>=.999999 || VisualTreeHelper.GetChildrenCount(v)==0 || VisualTreeHelper.GetChildrenCount(v)==1 && OwnDrawing(v)==null)){
                VisualContent(v,p,m,clip,alpha*opacity);return;
            }
            var bounds=Bounds(v);
            if(!Valid(bounds))return;
            if(fx is DropShadowEffect || fx is BlurEffect){
                Shadow(v,p,m,clip,alpha*opacity,bounds,fx);return;
            }
            if(fx!=null && !(fx is ShaderEffect))throw new NotSupportedException("Unsupported visual effect "+fx.GetType().Name);
            Isolate(v,p,m,clip,alpha*opacity,bounds,mask,g,fx,child=>VisualContent(v,child,Matrix.Identity,child.Area,1));
        }
        void Shadow(Visual owner,SceneDevice.Pass output,Matrix m,Rect clip,double opacity,Rect bounds,Effect effect){
            var shadow=effect as DropShadowEffect;var blur=effect as BlurEffect;
            double radius=shadow?.BlurRadius??blur.Radius;
            double angle=(shadow?.Direction??0)*Math.PI/180;
            var shift=new Vector(Math.Cos(angle)*(shadow?.ShadowDepth??0),-Math.Sin(angle)*(shadow?.ShadowDepth??0));
            var area=bounds;area.Inflate(radius+Math.Abs(shift.X)+1,radius+Math.Abs(shift.Y)+1);
            if(!Valid(area) || !clip.IntersectsWith(Rect.Transform(area,m)))return;
            double density=Pixels(output,m);
            int content=Layer(null,area,density,t=>VisualContent(owner,t,Matrix.Identity,area,1),PassKey(owner,LayerRole.ShadowInput));
            int id=content;
            for(int axis=0;axis<2;axis++){
                int source=id,coordinate=axis;
                id=Layer(null,area,density,t=>{
                    var q=SceneDevice.Quad(t,area,Matrix.Identity,area,source,Unit,1,18);
                    q.Constant(0,coordinate==0?radius/area.Width:0,coordinate==1?radius/area.Height:0);t.Draws.Add(q);
                },PassKey(owner,axis==0?LayerRole.ShadowWidth:LayerRole.ShadowHeight));
            }
            if(shadow!=null){
                var moved=m;var shiftMatrix=new Matrix(1,0,0,1,shift.X,shift.Y);moved=Join(shiftMatrix,m);
                var color=shadow.Color;var q=SceneDevice.Quad(output,area,moved,clip,id,Unit,opacity*shadow.Opacity,19);
                q.Constant(0,color.R/255d,color.G/255d,color.B/255d,color.A/255d);output.Draws.Add(q);
                output.Draws.Add(SceneDevice.Quad(output,area,m,clip,content,Unit,opacity,0));
            }else output.Draws.Add(SceneDevice.Quad(output,area,m,clip,id,Unit,opacity,0));
        }
        void VisualContent(Visual v,SceneDevice.Pass p,Matrix m,Rect clip,double a){
            var d=OwnDrawing(v);if(d!=null)Draw(d,p,m,clip,a,0);
            int n=VisualTreeHelper.GetChildrenCount(v);
            for(int i=0;i<n;i++)if(VisualTreeHelper.GetChild(v,i) is Visual child)VisualNode(child,p,m,clip,a);
        }
        void Draw(Drawing d,SceneDevice.Pass p,Matrix m,Rect clip,double alpha,double spin){
            if(d==null || alpha<=0 || !Valid(clip))return;
            if(insideScroll && d is GlyphRunDrawing rowGlyph && DrawRowText(rowGlyph,p,m,clip,alpha))return;
            // Frozen multi-drawing artwork and WPF glyph runs have stable retained identities.
            // Mutable visual trees and packet-local offscreen surfaces still follow the normal walk.
            object key=null;Brush ink=null;
            if(d is GlyphRunDrawing text && text.GlyphRun!=null){key=text.GlyphRun;ink=text.ForegroundBrush;}
            else if(d.IsFrozen && (d is DrawingGroup frozen && frozen.Children.Count>1 ||
                d is GeometryDrawing shape && (shape.Brush==null || shape.Brush is SolidColorBrush || shape.Brush is GradientBrush) ||
                d is ImageDrawing image && image.ImageSource is BitmapSource bitmap && FlowBatch.SceneOwner(bitmap)==null))key=d;
            if(key==null){DrawContent(d,p,m,clip,alpha,spin);return;}
            var record=retained.GetOrCreateValue(key);
            if(record.Isolated){DrawContent(d,p,m,clip,alpha,spin);return;}
            bool same=record.W==p.W && record.H==p.H && record.Area==p.Area && record.Transform==m && record.Clip==clip &&
                      record.Alpha==alpha && record.Spin==spin && ReferenceEquals(record.Ink,ink);
            if(same && record.Ready){
                int i=0;
                for(;i<record.Count;i++){
                    var q=record.Commands[i];
                    if(!resources.Touch(q.Image) || !resources.Touch(q.Auxiliary) || !resources.Touch(q.Atlas))break;
                }
                if(i==record.Count){
                    for(i=0;i<record.Count;i++)p.Draws.Add(record.Commands[i]);
                    ReusedCommands+=record.Count;return;
                }
            }
            int first=p.Draws.Count,startSlot=slots;
            DrawContent(d,p,m,clip,alpha,spin);
            record.Ready=false;
            if(slots!=startSlot){record.Isolated=true;return;}
            if(same){
                int count=p.Draws.Count-first;
                for(int i=first;i<p.Draws.Count;i++){
                    var q=p.Draws[i];if(q.Image<0 || q.Auxiliary<0 || q.Atlas<0){record.Isolated=true;return;}
                }
                if(record.Commands==null || record.Commands.Length<count)record.Commands=new SceneDevice.Command[count];
                p.Draws.CopyTo(first,record.Commands,0,count);record.Count=count;record.Ready=true;
            }else{
                record.W=p.W;record.H=p.H;record.Area=p.Area;record.Clip=clip;record.Transform=m;
                record.Alpha=alpha;record.Spin=spin;record.Ink=ink;
            }
        }
        void DrawContent(Drawing d,SceneDevice.Pass p,Matrix m,Rect clip,double alpha,double spin){
            if(d is DrawingGroup group){
                if(group.Opacity<=0 || group.Children.Count==0)return;
                if(sky!=null && group==sky.ProminenceDrawing){
                    Solar(p,m,clip,alpha*group.Opacity);return;
                }
                if(sky!=null && group==sky.CompositionVolume.Art && Volume(p,m,clip,alpha))return;
                if(group.Transform!=null){
                    spin+=sky?.NativeRotation(group.Transform)??0;m=Join(group.Transform.Value,m);
                }
                var g=group.ClipGeometry;
                if(g!=null && Rectangle(g,out var rect) && Axis(m)){clip.Intersect(Rect.Transform(rect,m));g=null;}
                if(!Valid(clip))return;
                if(group.OpacityMask!=null || g!=null || (group.Opacity<.999999 && group.Children.Count>1)){
                    var bounds=Rect.Empty;foreach(var child in group.Children)bounds.Union(child.Bounds);
                    if(!Valid(bounds))return;
                    Isolate(group,p,m,clip,alpha*group.Opacity,bounds,group.OpacityMask,g,null,
                        pass=>{foreach(var child in group.Children)Draw(child,pass,Matrix.Identity,pass.Area,1,spin);});
                    return;
                }
                foreach(var child in group.Children)Draw(child,p,m,clip,alpha*group.Opacity,spin);return;
            }
            if(d is ImageDrawing image){
                if(image.ImageSource is DrawingImage di){
                    var a=di.Drawing?.Bounds??Rect.Empty;
                    if(Valid(a) && Valid(image.Rect))Draw(di.Drawing,p,Join(Map(a,image.Rect),m),clip,alpha,spin);
                }else if(Valid(image.Rect) && clip.IntersectsWith(Rect.Transform(image.Rect,m))){
                    int id=resources.Image(image.ImageSource);if(id>0)p.Draws.Add(SceneDevice.Quad(p,image.Rect,m,clip,id,Unit,alpha,0,spin));}
                return;
            }
            if(d is GeometryDrawing geometry){
                if(geometry.Geometry==null || geometry.Brush==null && geometry.Pen==null)return;
                if(geometry.Brush is SolidColorBrush empty && empty.Color.A==0 && geometry.Pen==null)return;
                if(Rectangle(geometry.Geometry,out var rect) && geometry.Pen==null && geometry.Brush!=null){
                    if(Valid(rect) && clip.IntersectsWith(Rect.Transform(rect,m)))BrushRect(geometry.Brush,rect,p,m,clip,alpha,spin);
                    return;
                }
                if(geometry.Pen==null && (geometry.Brush is VisualBrush || geometry.Brush is DrawingBrush || geometry.Brush is BitmapCacheBrush ||
                     geometry.Brush is ImageBrush ib && !(ib.ImageSource is BitmapSource))){
                    var a=geometry.Geometry.Bounds;if(!Valid(a))return;
                    Isolate(geometry,p,m,clip,alpha,a,null,geometry.Geometry,null,pass=>BrushRect(geometry.Brush,a,pass,Matrix.Identity,pass.Area,1,spin));return;
                }
            }
            Bitmap(d,p,m,clip,alpha,d.Bounds,null);
        }
        void Bitmap(Drawing d,SceneDevice.Pass p,Matrix m,Rect clip,double a,Rect area,object key){
            if(!Valid(area)||!Rect.Transform(area,m).IntersectsWith(clip))return;
            double density=Pixels(p,m);
            // Keep at least a one-physical-pixel fringe for subpixel glyphs and strokes.
            var original=area;
            area.Inflate(1/density,1/density);
            if(LargeTile(area,density)){
                if(DrawThinGlow(d,p,m,clip,a,original,key))return;
                VisibleTiles(d,p,m,clip,a,area,key,density);return;
            }
            int w=Size(area.Width*density),h=Size(area.Height*density);
            int id=resources.Tile(d,area,w,h,key);p.Draws.Add(SceneDevice.Quad(p,area,m,clip,id,Unit,a,0));
        }
        static bool LargeTile(Rect area,double density){
            double w=Math.Ceiling(area.Width*density),h=Math.Ceiling(area.Height*density);
            return w>8192 || h>8192 || w*h>24*1024*1024;
        }
        bool DrawThinGlow(Drawing drawing,SceneDevice.Pass pass,Matrix transform,Rect clip,double alpha,Rect bounds,object key){
            bool horizontal=pass.H<=2 && pass.W>=256,vertical=pass.W<=2 && pass.H>=256;
            if(!(horizontal || vertical) || transform.M12!=0 || transform.M21!=0 || !(drawing is GeometryDrawing g) ||
               g.Pen!=null || !(g.Geometry is EllipseGeometry ellipse) || !(g.Brush is RadialGradientBrush brush))return false;
            var stops=brush.GradientStops;
            if(brush.MappingMode!=BrushMappingMode.RelativeToBoundingBox || brush.SpreadMethod!=GradientSpreadMethod.Pad ||
               brush.RadiusX<.25 || brush.RadiusY<.25 || brush.Center!=new Point(.5,.5) || brush.GradientOrigin!=brush.Center ||
               ellipse.Transform!=null && !ellipse.Transform.Value.IsIdentity || brush.Transform!=null && !brush.Transform.Value.IsIdentity ||
               brush.RelativeTransform!=null && !brush.RelativeTransform.Value.IsIdentity ||
               stops.Count<2 || stops[0].Offset!=0 || stops[stops.Count-1].Offset!=1 || stops[stops.Count-1].Color.A!=0)return false;
            double last=double.NegativeInfinity;
            foreach(var stop in stops){if(stop.Offset-last<.04)return false;last=stop.Offset;}
            // Isolating an almost fully clipped glow rounds the thin dimension up to one pixel.
            // Its scale may be hundreds of times the horizontal scale. Sample each device axis
            // at its actual density, with a one-texel fringe for this smooth, axis-aligned glow.
            double sx=Math.Abs(transform.M11)*pass.Sx,sy=Math.Abs(transform.M22)*pass.Sy;
            if(!(sx>0 && sy>0) || !(horizontal && sy>sx*2 || vertical && sx>sy*2))return false;
            bounds.Inflate(1/sx,1/sy);
            var visible=Visible(bounds,transform,clip);if(!Valid(visible))return true;
            double fullW=Math.Ceiling(bounds.Width*sx),fullH=Math.Ceiling(bounds.Height*sy);
            sx=fullW/bounds.Width;sy=fullH/bounds.Height;
            double x=Math.Max(0,Math.Floor((visible.X-bounds.X)*sx)-1),y=Math.Max(0,Math.Floor((visible.Y-bounds.Y)*sy)-1);
            double w=Math.Min(fullW,Math.Ceiling((visible.Right-bounds.X)*sx)+1)-x;
            double h=Math.Min(fullH,Math.Ceiling((visible.Bottom-bounds.Y)*sy)+1)-y;
            if(w<1 || h<1)return true;
            if(w>8192 || h>8192)return false;
            var sample=new Rect(bounds.X+x/sx,bounds.Y+y/sy,w/sx,h/sy);
            int id=resources.Tile(drawing,sample,(int)w,(int)h,key,true);
            pass.Draws.Add(SceneDevice.Quad(pass,sample,transform,clip,id,Unit,alpha,0));
            return true;
        }
        void VisibleTiles(Drawing d,SceneDevice.Pass p,Matrix m,Rect clip,double a,Rect area,object key,double density){
            var visible=Visible(area,m,clip);
            if(!Valid(visible))return;
            double w=Math.Ceiling(area.Width*density),h=Math.Ceiling(area.Height*density);
            double sx=w/area.Width,sy=h/area.Height;
            const int chunk=1024;
            double left=Math.Max(0,Math.Floor((visible.Left-area.Left)*sx/chunk)*chunk);
            double top=Math.Max(0,Math.Floor((visible.Top-area.Top)*sy/chunk)*chunk);
            double right=Math.Min(w,(visible.Right-area.Left)*sx),bottom=Math.Min(h,(visible.Bottom-area.Top)*sy);
            for(double y=top;y<bottom;y+=chunk){
                for(double x=left;x<right;x+=chunk){
                    int cw=(int)Math.Min(chunk,w-x),ch=(int)Math.Min(chunk,h-y);
                    var face=new Rect(area.X+x/sx,area.Y+y/sy,cw/sx,ch/sy);
                    if(!Rect.Transform(face,m).IntersectsWith(clip))continue;
                    // Adjacent quads meet exactly on the original pixel grid. Only the sampled
                    // texture overlaps by one texel, so filtering across the seam sees its neighbor.
                    int l=x>0?1:0,t=y>0?1:0,tw=cw+l+(x+cw<w?1:0),th=ch+t+(y+ch<h?1:0);
                    var sample=new Rect(area.X+(x-l)/sx,area.Y+(y-t)/sy,tw/sx,th/sy);
                    int id=resources.Tile(d,sample,tw,th,key,true);
                    var uv=new Rect((double)l/tw,(double)t/th,(double)cw/tw,(double)ch/th);
                    p.Draws.Add(SceneDevice.Quad(p,face,m,clip,id,uv,a,0));
                }
            }
        }
        static Matrix Map(Rect from,Rect to){double sx=to.Width/from.Width,sy=to.Height/from.Height;
            return new Matrix(sx,0,0,sy,to.X-sx*from.X,to.Y-sy*from.Y);}
        static Rect Fraction(Rect a,Rect parent){return new Rect(parent.X+a.X*parent.Width,parent.Y+a.Y*parent.Height,a.Width*parent.Width,a.Height*parent.Height);}
        void Solar(SceneDevice.Pass output,Matrix matrix,Rect clip,double alpha){
            var area=new Rect(-80,-80,160,160);
            if(!Rect.Transform(area,matrix).IntersectsWith(clip))return;
            double phase=sky.SolarPhase,rate=sky.SolarRate,density=Pixels(output,matrix);
            for(int section=-1;section<=3;section++){
                int index=section;
                Action<SceneDevice.Pass,Matrix,Rect,double> field=(pass,transform,visible,opacity)=>{
                    var q=SceneDevice.Quad(pass,area,transform,visible,0,Unit,opacity,20);
                    q.Constant(0,phase,rate,index);q.Constant(1,1/Pixels(pass,transform));pass.Draws.Add(q);
                };
                if(index<0 || index==3){field(output,matrix,clip,alpha);continue;}
                // A single envelope covers each loop, including its overlapping strands and moving tip.
                int stream=Layer(null,area,density,pass=>field(pass,Matrix.Identity,area,1),
                    PassKey(sky.ProminenceDrawing,LayerRole.SolarLoop0+index));
                var layer=SceneDevice.Quad(output,area,matrix,clip,stream,Unit,alpha,21);
                layer.Constant(0,phase,rate,index);output.Draws.Add(layer);
            }
        }
        bool Volume(SceneDevice.Pass p,Matrix m,Rect clip,double a){
            if(!sky.CompositionVolume.Sample(out var area,out var eye,out var energy,out var drift,out var swell,out var moving))return false;
            var dpi=VisualTreeHelper.GetDpi(sky);
            double sample=Environment.GetEnvironmentVariable("ASTRALSPLIT_HALF_NEBULA")=="1"?.5:1;
            int w=Size(384*dpi.DpiScaleX*sample),h=Size(144*dpi.DpiScaleY*sample);
            var key=Tuple.Create((object)sky.CompositionVolume,area,w,h);
            int material=LayerSized(key,area,w,h,t=>{
                var q=SceneDevice.Quad(t,area,Matrix.Identity,area,resources.Image(NebulaSky.Density),Unit,1,7);
                q.Constant(0,eye.X,eye.Y,drift,swell);q.Constant(1,energy,moving?1:0);t.Draws.Add(q);
            },PassKey(sky.CompositionVolume),SceneDevice.Stage.Sky);
            p.Draws.Add(SceneDevice.Quad(p,area,m,clip,material,Unit,a,14));return true;
        }
        static void SetLensParameters(ref SceneDevice.Command quad,GateOptics optics){
            quad.Constant(0,optics.StandbyOnly?0:optics.LensStrength);
            quad.Constant(1,optics.NativeMass);
            var weak=optics.StandbyParameters;
            quad.Constant(2,weak.X,weak.Y);
        }
        SceneDevice.Command SkyLensQuad(SceneDevice.Pass target,Rect area,Matrix m,Rect clip,int image,double alpha){
            var patch=sky.NativePatch;
            var q=SceneDevice.Quad(target,area,m,clip,image,Unit,alpha,9);
            SetLensParameters(ref q,sky.NativeOptics);
            q.Constant(4,(patch.X-area.X)/area.Width,(patch.Y-area.Y)/area.Height,patch.Width/area.Width,patch.Height/area.Height);
            return q;
        }
        int NativeSky(Rect area,double density,bool composeLens=true){
            var patch=sky.NativePatch;bool lens=Valid(patch),standby=sky.NativeOptics?.StandbyOnly==true;
            var baseId=Layer(sky.NativeSky,area,density,pass=>{
                VisualNode(sky.NativeSky,pass,Matrix.Identity,area,1,false);
                if(lens && !standby && sky.NativeFlyers!=null)Draw(sky.NativeFlyers,pass,Matrix.Identity,Rect.Intersect(area,patch),1,0);
            },stage:SceneDevice.Stage.Sky);
            if(!lens || !composeLens)return baseId;
            return Layer(sky.NativeTransmission,area,density,output=>{
                output.Draws.Add(SkyLensQuad(output,area,Matrix.Identity,area,baseId,1));
            },stage:SceneDevice.Stage.Lens);
        }
        int FrostGraph(Rect area){
            int fullW=device.Width,fullH=device.Height,id=NativeSky(area,fullW/area.Width);
            int w=fullW,h=fullH;
            for(int i=0;i<4;i++){
                int source=id,oldW=w,oldH=h;bool down=i<2;
                w=Math.Max(1,fullW/(i==0||i==2?2:i==1?4:1));h=Math.Max(1,fullH/(i==0||i==2?2:i==1?4:1));
                id=LayerSized(null,area,w,h,t=>{
                    var quad=SceneDevice.Quad(t,area,Matrix.Identity,area,source,Unit,1,down?15:16);
                    quad.Constant(0,.62/oldW,.62/oldH);t.Draws.Add(quad);
                },PassKey(sky,LayerRole.Frost0+i));
            }
            return id;
        }
        public void Dispose(){if(disposed)return;disposed=true;scroller?.UnbindScroll(device);resources.Dispose();FlowBatch.TrackNative(false);}
    }
    public sealed partial class StarfieldLayer
    {
        internal Drawing ProminenceDrawing{get{return prominence?.Art;}}
        internal double SolarPhase{get{return standbyClock.Elapsed.TotalSeconds%6/6;}}
        internal double SolarRate{get{return standbyClock.IsRunning?1.0/6:0;}}
        bool nativeProminence;
        internal bool NativeFlow {get{return nativeProminence;}}
        internal readonly HashSet<FlowBatch> DescriptionBatches=new HashSet<FlowBatch>();
        internal void NativeSolar(bool installed){
            nativeProminence=installed;
            if(!installed && DescriptionBatches.Count!=0){
                foreach(var batch in new List<FlowBatch>(DescriptionBatches))batch.RestoreCompatibility();
            }
            if(!installed && standbyClock.IsRunning)PaintStandby(SolarPhase);
        }
        internal Visual NativeSky{get{return naturalSky;}}
        internal Visual NativeTransmission{get{return transmittedSky;}}
        internal Drawing NativeFlyers{get{return lensedFlyers.Children.Count!=0?flyerSample.Drawing:null;}}
        internal Rect NativePatch{get{return patchBounds;}}
        internal GateOptics NativeOptics{get{return starOptics;}}
        internal double NativeRotation(Transform t){
            if(!orbitRunning)return 0;
            double period=t==outerOrbit?7.8:t==innerOrbit?3.65:t==photonOrbit?5.15:t==coronaOrbit?22:0;
            return period>0?2*Math.PI/period:0;
        }
    }
}
