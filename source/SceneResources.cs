using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    // Resources are local to one window's device. Packets pin the GPU version they used.
    internal sealed class SceneResources : IDisposable
    {
        sealed class Entry {
            internal int Id,Used,W,H;
            internal long Generation;internal SceneRibbonData Lines;
            internal SceneStreamData Sprites;
            internal bool Changed;
            internal List<Freezable> Watched;
            internal void Dirty(object sender,EventArgs args){Changed=true;}
            internal void Watch(object target){
                if(!(target is Freezable f) || f.IsFrozen)return;
                if(Watched==null)Watched=new List<Freezable>();
                if(!Watched.Contains(f)){Watched.Add(f);f.Changed+=Dirty;}
            }
            internal void Release(){if(Watched!=null)foreach(var f in Watched)f.Changed-=Dirty;}
        }
        struct Key : IEquatable<Key>{
            internal object A,B,C;internal int W,H;internal Rect Shape,SourceBounds;internal double RoundX,RoundY;internal int Kind;
            public bool Equals(Key other){return ReferenceEquals(A,other.A)&&ReferenceEquals(B,other.B)&&ReferenceEquals(C,other.C)
                &&W==other.W&&H==other.H&&Shape==other.Shape&&SourceBounds==other.SourceBounds&&RoundX==other.RoundX&&RoundY==other.RoundY&&Kind==other.Kind;}
            public override bool Equals(object obj){return obj is Key k&&Equals(k);}
            public override int GetHashCode(){unchecked{return ((((A==null?0:RuntimeHelpers.GetHashCode(A))*31+(B==null?0:RuntimeHelpers.GetHashCode(B)))*31+
                (C==null?0:RuntimeHelpers.GetHashCode(C)))*31+W*131+H)*31+Shape.GetHashCode()+SourceBounds.GetHashCode()+RoundX.GetHashCode()+37*RoundY.GetHashCode()+Kind;}}
        }
        readonly Dictionary<Key,Entry> cache=new Dictionary<Key,Entry>();
        readonly Dictionary<int,Entry> byId=new Dictionary<int,Entry>();
        readonly SceneDevice device;readonly StarfieldLayer sky;
        readonly List<Key> expired=new List<Key>();
        int age;long bytes;
        internal static readonly bool Recording=Environment.GetEnvironmentVariable("ASTRALSPLIT_GRAPH_PROFILE")=="1";
        readonly Dictionary<string,double> cost=new Dictionary<string,double>();
        internal string Profile(){
            var notes=new List<KeyValuePair<string,double>>(cost);notes.Sort((a,b)=>b.Value.CompareTo(a.Value));
            return String.Join(" | ",notes.GetRange(0,Math.Min(12,notes.Count)).ConvertAll(x=>x.Key+"="+x.Value.ToString("F1")));
        }
        void Add(string label,long tick){if(!Recording)return;double ms=(System.Diagnostics.Stopwatch.GetTimestamp()-tick)*1000d/System.Diagnostics.Stopwatch.Frequency;
            cost.TryGetValue(label,out double old);cost[label]=old+ms;}
        internal int Uploads{get;private set;}
        internal SceneResources(SceneDevice target,StarfieldLayer source){device=target;sky=source;}
        internal void Begin(){age++;Uploads=0;cost.Clear();}
        internal void Collect(){
            if(age%24!=0 && bytes<384L*1024*1024)return;
            expired.Clear();
            foreach(var pair in cache)if(age-pair.Value.Used>(bytes>384L*1024*1024?1:8))expired.Add(pair.Key);
            foreach(var key in expired){var e=cache[key];device.Drop(e.Id);byId.Remove(e.Id);e.Release();bytes-=4L*e.W*e.H;cache.Remove(key);}
        }
        Entry Find(Key key){
            if(!cache.TryGetValue(key,out var entry)){entry=new Entry{Changed=true};cache.Add(key,entry);}
            entry.Used=age;return entry;
        }
        void Uploaded(Entry e,int id,int w,int h){
            bytes+=4L*w*h-4L*e.W*e.H;e.Id=id;e.W=w;e.H=h;e.Changed=false;byId[id]=e;Uploads++;
        }
        internal bool Touch(int id){
            if(id==0)return true;
            if(!byId.TryGetValue(id,out var e) || e.Changed)return false;
            e.Used=age;return true;
        }
        internal int Image(ImageSource source){
            if(source==null)return 0;
            var key=new Key{A=source,Kind=1};var e=Find(key);
            var owner=FlowBatch.SceneOwner(source);
            if(sky!=null && owner!=null && e.Used==age && e.Generation==-age)return e.Id;
            var lines=owner?.SceneLines;
            if(lines!=null){
                if(lines!=e.Lines){int id=device.Ribbons(lines,e.Id);Uploaded(e,id,lines.W,lines.H*2);e.Lines=lines;}
                e.Sprites=null;e.Generation=-age;return e.Id;
            }
            var sprite=owner!=null?sky?.StreamFrame(owner):null;
            if(sprite!=null){
                if(!ReferenceEquals(sprite,e.Sprites)){
                    int id=device.Stream(sprite,e.Id);Uploaded(e,id,sprite.W,sprite.H*2);
                    // FlowBatch snapshots for the dust and magnetic field are
                    // complete on publication. Live app particles carry new ages.
                    e.Sprites=sprite.Kind==0?null:sprite;
                }
                e.Lines=null;e.Generation=-age;
            }else if(FlowBatch.Locate(source,out var batch,out var slot,out var generation)){
                e.Sprites=null;e.Lines=null;
                if(e.Id==0||generation!=e.Generation){long tick=System.Diagnostics.Stopwatch.GetTimestamp();int id=device.Import(batch,slot,e.Id);Add("GPU particle",tick);Uploaded(e,id,((D3DImage)source).PixelWidth,((D3DImage)source).PixelHeight);e.Generation=generation;}
            }else if(source is BitmapSource bitmap && bitmap.PixelWidth>0 && bitmap.PixelHeight>0 && (e.Changed || e.Id==0)){
                long tick=System.Diagnostics.Stopwatch.GetTimestamp();int id=device.Upload(bitmap,e.Id);Add("image "+(bitmap.PixelWidth>>8)+"x"+(bitmap.PixelHeight>>8),tick);if(e.Id==0)e.Watch(bitmap);Uploaded(e,id,bitmap.PixelWidth,bitmap.PixelHeight);
            }
            return e.Id;
        }
        internal static BitmapSource Raster(Drawing drawing,Rect area,int w,int h){
            var content=new DrawingVisual();
            using(var dc=content.RenderOpen()){
                dc.PushTransform(new MatrixTransform(w/area.Width,0,0,h/area.Height,-area.X*w/area.Width,-area.Y*h/area.Height));
                dc.DrawDrawing(drawing);dc.Pop();
            }
            var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(content);bitmap.Freeze();return bitmap;
        }
        internal int Tile(Drawing d,Rect area,int w,int h,object explicitKey=null,bool slice=false){
            var k=new Key{W=w,H=h,Kind=2,A=explicitKey??d,Shape=area};
            if(explicitKey==null && d is GeometryDrawing gd){
                k.B=gd.Brush;k.C=gd.Pen;k.A=gd.Geometry;
                // DrawContext recreates rectangle/ellipse wrappers. Their origins do not
                // affect relative brushes, so identical glyph halos and solids can be shared.
                bool relative=!(gd.Brush is GradientBrush gradient) || gradient.MappingMode==BrushMappingMode.RelativeToBoundingBox;
                if((slice || relative && !(gd.Brush is TileBrush)) && (gd.Geometry.Transform==null||gd.Geometry.Transform.Value.IsIdentity)){
                    if(gd.Geometry is RectangleGeometry rectangle){k.Kind=slice?5:3;k.A=null;k.Shape=slice?area:new Rect(area.Size);
                        k.RoundX=rectangle.RadiusX;k.RoundY=rectangle.RadiusY;}
                    else if(gd.Geometry is EllipseGeometry){k.Kind=slice?6:4;k.A=null;k.Shape=slice?area:new Rect(area.Size);}
                    if(slice)k.SourceBounds=gd.Geometry.Bounds;
                }
            }else if(explicitKey==null && d is GlyphRunDrawing glyph){k.A=glyph.GlyphRun;k.B=glyph.ForegroundBrush;}
            var entry=Find(k);
            if(entry.Id==0 || entry.Changed){
                if(w<1||h<1||w>8192||h>8192||w*(long)h>24L*1024*1024)throw new NotSupportedException("Vector tile exceeds device dimensions.");
                long tick=System.Diagnostics.Stopwatch.GetTimestamp();int id;
                var path=new SceneVectorData();
                if(d is GeometryDrawing vector && path.Encode(vector,area))id=device.Vector(path,w,h,entry.Id);
                else id=device.Upload(Raster(d,area,w,h),entry.Id);
                if(Recording){var primitive=d as GeometryDrawing;Add((primitive==null?d.GetType().Name:(primitive.Brush?.GetType().Name??"stroke")+"/"+primitive.Geometry.GetType().Name)+" "+(w>>7)+"x"+(h>>7),tick);}
                if(entry.Id==0){entry.Watch(k.A);entry.Watch(k.B);entry.Watch(k.C);}
                Uploaded(entry,id,w,h);
            }
            return entry.Id;
        }
        public void Dispose(){foreach(var e in cache.Values){e.Release();device.Drop(e.Id);}cache.Clear();}
    }
}
