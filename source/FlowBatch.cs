using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    // Each D3DImage stays bound to its own shared surface. Pin the producer until
    // its GPU fence signals, leaving WPF free to display the last completed frame.
    internal sealed class FlowBatch : IDisposable
    {
        const string Library="AstralSplit.Graphics.dll";
        const int Capacity=1024;
        readonly bool dust,magnetic;
        bool ribbon;
        const int BufferCount=3;
        [ThreadStatic] static System.Collections.Generic.List<WeakReference<FlowBatch>> siblings;
        IntPtr FindSibling(){
            if(siblings==null)siblings=new System.Collections.Generic.List<WeakReference<FlowBatch>>();
            IntPtr found=IntPtr.Zero;
            for(int i=siblings.Count-1;i>=0;i--){
                FlowBatch b;
                if(!siblings[i].TryGetTarget(out b) || b==this || b.device==IntPtr.Zero){siblings.RemoveAt(i);continue;}
                if(!b.failed && b.owner==owner && b.monitor==monitor)found=b.device;
            }
            return found;
        }
        static readonly bool cacheField=Environment.GetEnvironmentVariable("ASTRALSPLIT_POLAR_MAP")=="1";
        static readonly Lazy<byte[]> fieldWriter=new Lazy<byte[]>(()=>Shader("MagneticMap")),fieldReader=new Lazy<byte[]>(()=>Shader("MagneticLookup"));
        static readonly bool clipField=cacheField || Environment.GetEnvironmentVariable("ASTRALSPLIT_MAGNETIC_CLIP")=="1";
        static readonly Lazy<byte[]> clippedField=new Lazy<byte[]>(()=>Shader("MagneticOptimizedPixel"));
        static readonly Lazy<byte[]> magneticVS=new Lazy<byte[]>(()=>Shader("MagneticVertex"));
        static readonly Lazy<byte[]> magneticPS=new Lazy<byte[]>(()=>Shader("MagneticPixel"));
        [StructLayout(LayoutKind.Sequential)]
        struct Sprite {internal float X,Y,Ax,Ay,Bx,By,R,G,B,A,Front;}
        readonly Sprite[] sprites=new Sprite[Capacity];
        readonly D3DImage[] frames={new D3DImage(),new D3DImage(),new D3DImage()};
        readonly IntPtr[] surfaces=new IntPtr[BufferCount];
        readonly bool[] pinned=new bool[BufferCount];
        readonly RectangleGeometry aperture=new RectangleGeometry(Rect.Empty);
        readonly GeometryDrawing back,front;
        internal Drawing Back {get{return back;}}
        internal Drawing Front {get{return front;}}
        D3DImage image;
        IntPtr device,owner,monitor;
        int count,width,height,lastMonitorCheck,lastFailure;
        int shown=-1,writer=-1,recording=-1;
        internal bool AcceptsSprites {get{return recording>=0 || describing;}}
        internal bool HasImage {get{return shown>=0;}}
        bool failed;
        // Each retained D3DImage has its own previous contents.
        readonly Int32Rect[,] previousMarks=new Int32Rect[BufferCount,2];
        readonly bool[] historyValid=new bool[BufferCount];
        readonly Int32Rect[] nextMarks=new Int32Rect[2], notified=new Int32Rect[2];
        struct Bounds {
            internal bool used;
            internal double left,top,right,bottom;
            internal void Add(Sprite sprite){
                double x=sprite.X,y=sprite.Y;
                double rx=Math.Abs((double)sprite.Ax)+Math.Abs((double)sprite.Bx);
                double ry=Math.Abs((double)sprite.Ay)+Math.Abs((double)sprite.By);
                if(!used){left=x-rx;right=x+rx;top=y-ry;bottom=y+ry;used=true;}
                else {left=Math.Min(left,x-rx);right=Math.Max(right,x+rx);top=Math.Min(top,y-ry);bottom=Math.Max(bottom,y+ry);}
            }
            internal Int32Rect Pixels(Rect area,int w,int h,int plane){
                if(!used)return Int32Rect.Empty;
                double x=(float)area.X,y=(float)area.Y,sx=w*(double)(float)(1/area.Width),sy=h*(double)(float)(1/area.Height);
                // A float ULP at these working coordinates must fit inside our two-texel guard.
                if(!(Math.Max(Math.Abs(left),Math.Max(Math.Abs(right),Math.Abs(x)))*sx<1000000 &&
                     Math.Max(Math.Abs(top),Math.Max(Math.Abs(bottom),Math.Abs(y)))*sy<1000000))
                    return new Int32Rect(0,plane*h,w,h);
                int l=(int)Math.Max(0,Math.Min(w,Math.Floor((left-x)*sx)-2));
                int t=(int)Math.Max(0,Math.Min(h,Math.Floor((top-y)*sy)-2));
                int r=(int)Math.Max(0,Math.Min(w,Math.Ceiling((right-x)*sx)+2));
                int b=(int)Math.Max(0,Math.Min(h,Math.Ceiling((bottom-y)*sy)+2));
                if(r<=l || b<=t)return Int32Rect.Empty;
                return new Int32Rect(l,t+plane*h,r-l,b-t);
            }
        }
        bool LocalMarks {get{return !dust && !magnetic && !ribbon;}}
        void CaptureMarks(Rect area){
            Bounds rear=default(Bounds),front=default(Bounds);
            for(int i=0;i<count;i++){if(sprites[i].Front>.5f)front.Add(sprites[i]);else rear.Add(sprites[i]);}
            nextMarks[0]=rear.Pixels(area,width,height,0);nextMarks[1]=front.Pixels(area,width,height,1);
        }
        static Int32Rect Cover(Int32Rect a,Int32Rect b){
            if(a.IsEmpty)return b;if(b.IsEmpty)return a;
            int l=Math.Min(a.X,b.X),t=Math.Min(a.Y,b.Y);
            return new Int32Rect(l,t,Math.Max(a.X+a.Width,b.X+b.Width)-l,Math.Max(a.Y+a.Height,b.Y+b.Height)-t);
        }
        void PublishChanges(D3DImage target,bool rebound){
            if(!LocalMarks || rebound || !historyValid[writer]){
                notified[0]=new Int32Rect(0,0,width,height*2);notified[1]=Int32Rect.Empty;
            }else {
                for(int i=0;i<2;i++)notified[i]=Cover(previousMarks[writer,i],nextMarks[i]);
            }
            for(int i=0;i<2;i++){
                if(!notified[i].IsEmpty)target.AddDirtyRect(notified[i]);
                previousMarks[writer,i]=nextMarks[i];
            }
            historyValid[writer]=true;
        }
        Rect submittedBounds,nextBounds;
        static readonly Duration NoWait=new Duration(TimeSpan.Zero);
        static readonly Lazy<byte[]> vs=new Lazy<byte[]>(()=>Shader("MoteVertex")),ps=new Lazy<byte[]>(()=>Shader("MotePixel"));
        static readonly Lazy<byte[]> dustVS=new Lazy<byte[]>(()=>Shader("DustVertex")),dustPS=new Lazy<byte[]>(()=>Shader("DustPixel"));
        static byte[] Shader(string name){
            using(var s=typeof(FlowBatch).Assembly.GetManifestResourceStream("AstralSplit."+name+".ps"))
            using(var dst=new MemoryStream()){s.CopyTo(dst);return dst.ToArray();}
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource,FlowBatch> owners=new System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource,FlowBatch>();
        StarfieldLayer sceneOwner;
        BitmapSource descriptionSource;
        bool describing;
        Rect descriptionRegion;
        int descriptionWidth,descriptionHeight;
        SceneRibbonData descriptionLines;
        SceneStreamData descriptionSprites;
        long publishedFrame;
        static int nativeReaders;
        readonly SceneStreamData[] spriteFrames=new SceneStreamData[BufferCount];
        readonly SceneRibbonData[] lineFrames=new SceneRibbonData[BufferCount];
        internal SceneRibbonData SceneLines{get{return sceneOwner!=null?descriptionLines:shown>=0?lineFrames[shown]:null;}}
        internal static void TrackNative(bool add){nativeReaders+=add?1:-1;}
        internal static FlowBatch SceneOwner(ImageSource image){
            if(image==null || !owners.TryGetValue(image,out var owner))return null;
            if(owner.sceneOwner!=null && owner.descriptionSource==image)return owner;
            return owner.shown>=0 && owner.frames[owner.shown]==image?owner:null;
        }
        internal bool SceneRegion(out Rect area,out int w,out int h){area=aperture.Rect;w=sceneOwner!=null?descriptionWidth:width;h=sceneOwner!=null?descriptionHeight:height;return shown>=0 && !area.IsEmpty && w>0 && h>0;}
        internal SceneStreamData SceneSprites(){return sceneOwner!=null?descriptionSprites:shown>=0?spriteFrames[shown]:null;}
        SceneStreamData CaptureSprites(Rect b,int w,int h){
            var saved=new SceneStreamData{Kind=magnetic?1:2,W=w,H=h,Region=b};
            for(int i=0;i<count;i++){var a=sprites[i];
                saved.Row(a.X,a.Y,a.Ax,a.Ay);saved.Row(a.Bx,a.By,a.R,a.G);saved.Row(a.B,a.A,a.Front,0);
            }
            return saved;
        }
        internal static bool Locate(ImageSource image,out IntPtr handle,out int slot,out long revision){
            handle=IntPtr.Zero;slot=-1;revision=0;
            if(!(image is D3DImage surface) || !owners.TryGetValue(surface,out var batch))return false;
            handle=batch.device;slot=Array.IndexOf(batch.frames,surface);revision=batch.publishedFrame;
            return handle!=IntPtr.Zero && slot>=0 && slot==batch.shown;
        }
        internal FlowBatch():this(false){}
        internal FlowBatch(bool dust,bool magnetic=false){
            this.dust=dust;this.magnetic=magnetic;
            foreach(var frame in frames)frame.IsFrontBufferAvailableChanged+=(sender,e)=>Array.Clear(historyValid,0,historyValid.Length);
            foreach(var frame in frames)owners.Add(frame,this);
            image=frames[0];back=Plane(0);front=Plane(.5);
        }
        GeometryDrawing Plane(double y){
            var brush=new ImageBrush(image){ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,Viewbox=new Rect(0,y,1,.5),
                Stretch=Stretch.Fill,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top};
            return new GeometryDrawing(brush,null,aperture);
        }
        bool Pin(int index,bool wait){
            var frame=frames[index];
            if(wait)frame.Lock();
            else if(!frame.TryLock(NoWait)){
                // .NET Framework increments D3DImage's lock count even on timeout.
                frame.Unlock();return false;
            }
            pinned[index]=true;return true;
        }
        void Unpin(int i){if(i>=0 && pinned[i]){pinned[i]=false;frames[i].Unlock();}}
        bool Detach(bool wait){
            int acquired=0;
            for(int i=0;i<frames.Length;i++){
                if(pinned[i])continue;
                if(!Pin(i,wait)){
                    for(int j=0;j<i;j++)if((acquired&(1<<j))!=0)Unpin(j);
                    return false;
                }
                acquired|=1<<i;
            }
            try{
                for(int i=0;i<frames.Length;i++)if(surfaces[i]!=IntPtr.Zero){
                    frames[i].SetBackBuffer(D3DResourceType.IDirect3DSurface9,IntPtr.Zero);
                    surfaces[i]=IntPtr.Zero;
                }
                Array.Clear(historyValid,0,historyValid.Length);writer=recording=shown=-1;aperture.Rect=Rect.Empty;width=height=0;
            }finally{for(int i=0;i<frames.Length;i++)Unpin(i);}
            return true;
        }
        internal bool ResetImages(){RestoreCompatibility();return device!=IntPtr.Zero && !failed && Detach(false);}
        bool Present(){
            if(writer<0)return true;
            int ready=ParticleReady(device,out var surface);Check(ready);
            if(ready!=0)return false;
            var completed=frames[writer];
            bool rebound=surfaces[writer]!=surface;
            if(rebound){
                completed.SetBackBuffer(D3DResourceType.IDirect3DSurface9,surface,true);
                surfaces[writer]=surface;
            }
            PublishChanges(completed,rebound);
            Unpin(writer);shown=writer;writer=-1;
            image=completed;publishedFrame++;
            ((ImageBrush)back.Brush).ImageSource=image;((ImageBrush)front.Brush).ImageSource=image;
            aperture.Rect=submittedBounds;return true;
        }
        internal bool Begin(Visual visual,Rect area){return BeginAt(visual,area,1);}
        internal bool BeginAt(Visual visual,Rect area,double displayScale){return BeginFrame(visual,area,displayScale,0,0,null);}
        internal bool BeginRibbonFrame(Visual visual,Rect area,int w,int h,FlowBatch sibling){return BeginFrame(visual,area,1,w,h,sibling) && AcceptsSprites;}
        internal void CancelRibbonFrame(){describing=false;Unpin(recording);recording=-1;}
        void StartDescription(StarfieldLayer field,Rect area,int w,int h){
            if(sceneOwner==null){
                if(descriptionSource==null){
                    descriptionSource=BitmapSource.Create(1,2,96,96,PixelFormats.Pbgra32,null,new byte[8],4);
                    descriptionSource.Freeze();owners.Add(descriptionSource,this);
                }
                sceneOwner=field;field.DescriptionBatches.Add(this);
            }
            describing=true;descriptionRegion=area;descriptionWidth=w;descriptionHeight=h;
        }
        void PublishDescription(){
            describing=false;aperture.Rect=descriptionRegion;
            if(((ImageBrush)back.Brush).ImageSource!=descriptionSource){
                ((ImageBrush)back.Brush).ImageSource=descriptionSource;((ImageBrush)front.Brush).ImageSource=descriptionSource;
            }
        }
        internal void RestoreCompatibility(){
            if(sceneOwner==null)return;
            sceneOwner.DescriptionBatches.Remove(this);sceneOwner=null;describing=false;
            descriptionLines=null;descriptionSprites=null;
            // A completed D3DImage remains ready throughout native ownership.
            ((ImageBrush)back.Brush).ImageSource=image;((ImageBrush)front.Brush).ImageSource=image;
            aperture.Rect=submittedBounds;
        }
        void Dimensions(Visual visual,Rect area,double displayScale,ref int w,ref int h){
            if(w!=0)return;
            double requested=displayScale*ArtworkResolution.PixelsPerDip(visual,area.Width*displayScale,area.Height*displayScale);
            double density=Math.Min(requested,Math.Min(4096/area.Width,
                Math.Min((dust?2560:2048)/area.Height,Math.Sqrt((dust?5000000:2200000)/(area.Width*area.Height)))));
            w=Math.Max(1,(int)Math.Ceiling(area.Width*density));h=Math.Max(1,(int)Math.Ceiling(area.Height*density));
        }
        bool BeginFrame(Visual visual,Rect area,double displayScale,int exactWidth,int exactHeight,FlowBatch sibling){
            count=0;
            if(area.IsEmpty || area.Width<1 || area.Height<1 ||
               (RenderCapability.Tier>>16)<2 || RenderOptions.ProcessRenderMode==RenderMode.SoftwareOnly)return false;
            var source=PresentationSource.FromVisual(visual) as HwndSource;
            if(source==null)return false;
            try{
                int now=Environment.TickCount;var currentMonitor=monitor;
                if(owner!=source.Handle || unchecked((uint)(now-lastMonitorCheck))>=400){
                    currentMonitor=MonitorFromWindow(source.Handle,2);lastMonitorCheck=now;
                }
                bool moved=owner!=source.Handle || currentMonitor!=monitor;
                // Only bypass the compatibility renderer once this particular window has a
                // visible native frame and this particular batch has a fallback surface.
                var field=visual as StarfieldLayer;
                if(!moved && !failed && field!=null && field.NativeFlow && shown>=0 && Present()){
                    int nw=exactWidth,nh=exactHeight;Dimensions(visual,area,displayScale,ref nw,ref nh);
                    int oldW=sceneOwner!=null?descriptionWidth:width,oldH=sceneOwner!=null?descriptionHeight:height;
                    if(exactWidth==0 && Math.Abs(nw-oldW)<=Math.Max(32,nw/6) && Math.Abs(nh-oldH)<=Math.Max(20,nh/6)){nw=oldW;nh=oldH;}
                    StartDescription(field,area,nw,nh);return true;
                }
                RestoreCompatibility();
                if(failed && !moved && unchecked((uint)(now-lastFailure))<5000)return false;
                if(device==IntPtr.Zero || moved || failed){
                    if(device!=IntPtr.Zero){
                        if(!Detach(false))return !aperture.Rect.IsEmpty;
                        ParticleClose(device);device=IntPtr.Zero;
                    }
                    owner=source.Handle;monitor=currentMonitor;orbitUnavailable=false;
                    ribbon=exactWidth>0;
                    IntPtr available=FindSibling();
                    if(ribbon){
                        IntPtr shared=sibling!=null && !sibling.failed && sibling.owner==owner && sibling.monitor==monitor?sibling.device:available;
                        Check(ParticleOpenRibbon(owner,shared,out device));
                    }else{
                        var v=(dust?dustVS:magnetic && clipField?magneticVS:vs).Value;var p=(dust?dustPS:magnetic?(clipField?clippedField:magneticPS):ps).Value;
                        Check(ParticleOpenShared(owner,available,v,(uint)v.Length,p,(uint)p.Length,out device));
                    }
                    if(magnetic && cacheField){
                        var writerCode=fieldWriter.Value;var readerCode=fieldReader.Value;
                        ParticleEnablePolar(device,writerCode,(uint)writerCode.Length,readerCode,(uint)readerCode.Length);
                    }
                    Check(ParticleSlots(device,BufferCount));
                    siblings.Add(new WeakReference<FlowBatch>(this));
                    failed=false;
                }
                int w=exactWidth,h=exactHeight;
                Dimensions(visual,area,displayScale,ref w,ref h);
                if(exactWidth>0 ? (w!=width || h!=height) : (width==0 || Math.Abs(w-width)>Math.Max(32,w/6) || Math.Abs(h-height)>Math.Max(20,h/6))){
                    if(width!=0 && !Detach(false))return !aperture.Rect.IsEmpty;
                    Check(ParticleSize(device,(uint)w,(uint)h));width=w;height=h;
                }
                if(!Present())return true;
                int next=-1;
                for(int step=1;step<=frames.Length;step++){
                    int candidate=(shown+step)%frames.Length;
                    if(candidate!=shown && frames[candidate].IsFrontBufferAvailable && Pin(candidate,false)){next=candidate;break;}
                }
                if(next<0)return shown>=0;
                recording=next;
                Check(ParticleSelectSlot(device,next));
                recording=next;nextBounds=area;return true;
            }catch(Exception e) when(e is COMException || e is DllNotFoundException || e is EntryPointNotFoundException ||
                e is BadImageFormatException || e is ArgumentException || e is InvalidOperationException){
                Fail();return false;
            }
        }
        internal void Add(Point p,Vector direction,double rx,double ry,Color color,double opacity,bool behind){
            if(recording<0 || count>=sprites.Length)return;
            sprites[count++]=new Sprite {X=(float)p.X,Y=(float)p.Y,Ax=(float)(direction.X*rx),Ay=(float)(direction.Y*rx),
                Bx=(float)(-direction.Y*ry),By=(float)(direction.X*ry),R=color.R/255f,G=color.G/255f,B=color.B/255f,
                A=(float)(opacity*(color.A/255.0)),Front=behind?0:1};
        }
        internal void MagneticPlate(Point center,Vector major,Vector minor,double phase,double load,int imageKind,double gain,bool behind){
            if(!AcceptsSprites || count>=sprites.Length)return;
            sprites[count++]=new Sprite{X=(float)center.X,Y=(float)center.Y,Ax=(float)major.X,Ay=(float)major.Y,
                Bx=(float)minor.X,By=(float)minor.Y,R=(float)phase,G=(float)load,B=imageKind,A=(float)gain,Front=behind?0:1};
        }
        internal void Grain(Point p,Vector along,double radius,double tail,int pigment,bool behind){
            if(!AcceptsSprites || count>=sprites.Length || radius<.0001)return;
            float x=(float)(along.X*radius),y=(float)(along.Y*radius);
            sprites[count++]=new Sprite{X=(float)p.X,Y=(float)p.Y,Ax=x,Ay=y,Bx=-y,By=x,
                R=(float)(tail/radius),G=pigment,Front=behind?0:1};
        }
        internal void End(){
            if(describing){
                descriptionSprites=(dust || magnetic)?CaptureSprites(descriptionRegion,descriptionWidth,descriptionHeight):null;
                PublishDescription();return;
            }
            if(recording<0)return;
            try{
                var b=nextBounds;
                int hr=ParticleDraw(device,sprites,(uint)count,(float)b.X,(float)b.Y,(float)(1/b.Width),(float)(1/b.Height));
                Check(hr);
                if(hr==0){
                    if((dust || magnetic) && (nativeReaders>0 || ScenePresenter.Requested))spriteFrames[recording]=CaptureSprites(b,width,height);
                    if(LocalMarks)CaptureMarks(b);writer=recording;submittedBounds=b;
                }else Unpin(recording);
                recording=-1;
            }catch(COMException){Fail();}
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RibbonVertex {internal float X,Y;internal int Slot;}
        sealed class OrbitProgram {
            internal readonly int[] Sizes=new int[5];
            internal readonly byte[] Bytes;
            internal OrbitProgram(){
                using(var joined=new MemoryStream()){
                    int i=0;
                    foreach(var name in new[]{"orbitVS","orbitGS","orbitMask","orbitQuad","orbitInk"}){
                        var shader=Shader(name);Sizes[i++]=shader.Length;joined.Write(shader,0,shader.Length);
                    }
                    Bytes=joined.ToArray();
                }
            }
        }
        static readonly Lazy<OrbitProgram> orbitProgram=new Lazy<OrbitProgram>(()=>new OrbitProgram());
        bool orbitUnavailable;
        internal bool FinishOrbitMesh(float[] table,float[] spins,int stars,float[] view,uint[] colors,float[] widths,Point blue,Point red){
            if(describing){
                var bounds=descriptionRegion;
                view[8]=(float)bounds.X;view[9]=(float)bounds.Y;view[10]=(float)(descriptionWidth/bounds.Width);view[11]=(float)(descriptionHeight/bounds.Height);
                view[12]=(float)blue.X;view[13]=(float)blue.Y;view[14]=(float)red.X;view[15]=(float)red.Y;
                descriptionLines=SceneRibbonData.Orbit(table,spins,stars,view,colors,widths,descriptionWidth,descriptionHeight);
                PublishDescription();return true;
            }
            if(recording<0 || orbitUnavailable)return false;
            try{
                var code=orbitProgram.Value;
                int hr=ParticleOrbitProgram(device,code.Bytes,code.Sizes,table);
                if(hr<0){orbitUnavailable=true;return false;}
                var b=nextBounds;
                view[8]=(float)b.X;view[9]=(float)b.Y;view[10]=(float)(width/b.Width);view[11]=(float)(height/b.Height);
                view[12]=(float)blue.X;view[13]=(float)blue.Y;view[14]=(float)red.X;view[15]=(float)red.Y;
                hr=ParticleOrbitDraw(device,spins,(uint)stars,view,colors,widths);Check(hr);
                if(hr!=0)return false;
                lineFrames[recording]=nativeReaders>0 || ScenePresenter.Requested?SceneRibbonData.Orbit(table,spins,stars,view,colors,widths,width,height):null;
                writer=recording;recording=-1;submittedBounds=b;
                hr=ParticleRibbonFence(device);Check(hr);
                return hr==0 && Present();
            }catch(Exception ex) when(ex is COMException || ex is EntryPointNotFoundException || ex is InvalidOperationException){
                orbitUnavailable=true;return false;
            }
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleOrbitProgram(IntPtr device,byte[] program,[In] int[] sizes,[In] float[] parameters);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleOrbitDraw(IntPtr device,[In] float[] angles,uint stars,[In] float[] view,[In] uint[] colors,[In] float[] widths);
        internal bool FinishRibbons(RibbonVertex[] points,int count,uint[] colors,float[] widths,Point blue,Point red){
            if(describing){
                descriptionLines=SceneRibbonData.Curves(points,count,colors,widths,descriptionRegion,descriptionWidth,descriptionHeight,blue,red);
                PublishDescription();return true;
            }
            if(recording<0)return false;
            try{
                var b=nextBounds;
                int hr=ParticleRibbons(device,points,(uint)count,colors,widths,(float)b.X,(float)b.Y,
                    (float)(width/b.Width),(float)(height/b.Height),(float)blue.X,(float)blue.Y,(float)red.X,(float)red.Y);
                Check(hr);
                if(hr!=0){Unpin(recording);recording=-1;return false;}
                lineFrames[recording]=nativeReaders>0 || ScenePresenter.Requested?SceneRibbonData.Curves(points,count,colors,widths,b,width,height,blue,red):null;
                writer=recording;recording=-1;submittedBounds=b;
                hr=ParticleRibbonFence(device);Check(hr);
                return hr==0 && Present();
            }catch(Exception ex) when(ex is COMException || ex is EntryPointNotFoundException || ex is ArgumentException || ex is InvalidOperationException){
                Fail();return false;
            }
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleRibbons(IntPtr device,[In] RibbonVertex[] points,uint count,[In] uint[] colors,[In] float[] widths,
            float x,float y,float sx,float sy,float bx,float by,float rx,float ry);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleRibbonFence(IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleOpenRibbon(IntPtr owner,IntPtr sibling,out IntPtr device);
        static void Check(int hr){if(hr<0)throw new COMException("Particle renderer",hr);}
        void Fail(){Unpin(writer);Unpin(recording);writer=recording=-1;failed=true;lastFailure=Environment.TickCount;aperture.Rect=Rect.Empty;}
        public void Dispose(){
            RestoreCompatibility();
            if(device==IntPtr.Zero)return;
            try{Detach(true);}finally{ParticleClose(device);device=IntPtr.Zero;orbitUnavailable=false;}
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("user32.dll",ExactSpelling=true)]
        static extern IntPtr MonitorFromWindow(IntPtr window,uint fallback);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleEnablePolar(IntPtr device,byte[] writer,uint length,byte[] reader,uint readerLength);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleSlots(IntPtr device,int count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleSelectSlot(IntPtr device,int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleOpenShared(IntPtr window,IntPtr sibling,byte[] vs,uint vlen,byte[] ps,uint plen,out IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleSize(IntPtr device,uint width,uint height);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleReady(IntPtr device,out IntPtr surface);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern int ParticleDraw(IntPtr device,[In] Sprite[] sprites,uint count,float x,float y,float invW,float invH);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern void ParticleClose(IntPtr device);
    }
}
