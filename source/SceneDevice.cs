using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    internal sealed unsafe class SceneDevice : IDisposable
    {
        const string Dll="AstralSplit.Graphics.dll";
        const DllImportSearchPath Search=DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32;
        internal static readonly bool Profiling=Environment.GetEnvironmentVariable("ASTRALSPLIT_SCENE_PROFILE")=="1";
        [StructLayout(LayoutKind.Sequential)] internal struct Stats {internal long Sequence,Frames;internal double GpuMs;internal int Passes,Draws,Images,Status,ActivePasses,Submissions,Clouds,Paths;}
        [StructLayout(LayoutKind.Sequential)] internal struct PerfStats {
            internal long Packets,SubmitWaitTicks,SubmitTicks,Frames,PrepareTicks,RecordTicks,PresentTicks;
            internal long TargetCreates,TargetReuses,PooledBytes,ConstantWrites,ConstantReuses,PaletteCreates,PaletteReuses;
            internal long InputCreates,InputReuses,InputBytes,PassCreates,PassReuses,PassBytes;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FrameStamp {
            internal long Sequence,Frame,PacketAt,Start,Prepared,Encoded,DisplayCallDone;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct LayerStats {
            internal long Moved,MatchedSize,Idle,SizeMiss,PendingMiss,Trimmed;
            internal long Prepared,VectorTicks,TargetTicks,ConstantTicks;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct GpuStamp {
            internal long Frame;
            internal double Setup,Streams,Sky,Lens,Projection,Layers,Composite;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Command {
            internal int Image,Auxiliary,Atlas,Material;
            internal fixed float Value[40];
            internal fixed int Clip[4];
            internal void Constant(int slot,double x,double y=0,double z=0,double w=0){
                fixed(float* first=Value){float* a=first+12+slot*4;a[0]=(float)x;a[1]=(float)y;a[2]=(float)z;a[3]=(float)w;}
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct PassData {internal int Slot,W,H,First,Count;}
        internal enum Stage { Setup, Streams, Sky, Lens, Projection, Layers, Composite }
        [StructLayout(LayoutKind.Sequential)] struct PassTag {internal ulong Identity;internal Stage Stage;internal int Reserved;}
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneOpen(IntPtr host,int screen,int w,int h,byte[] vs,int vl,byte[] ps,int pl,out IntPtr device);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneImage(IntPtr d,int id,int w,int h,IntPtr pixels,int stride);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void SceneReleaseImage(IntPtr d,int id);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneSubmitTagged(IntPtr d,PassData[] passes,PassTag[] tags,int count,Command[] cmds,int draws,out long sequence);
        [StructLayout(LayoutKind.Sequential)]internal struct ScrollMotion {
            internal long Stamp;internal double Position,Velocity,Target,Frequency;internal int Owner,Drag;
        }
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void SceneScrollMotion(IntPtr d,ref ScrollMotion motion);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneDisplayedOffset(IntPtr d,int owner,out double offset);
        internal void SetScroll(ScrollMotion motion){if(device!=IntPtr.Zero)SceneScrollMotion(device,ref motion);}
        internal bool DisplayedScroll(int owner,out double offset){offset=0;return device!=IntPtr.Zero && SceneDisplayedOffset(device,owner,out offset)!=0;}
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneShow(IntPtr d);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneBeginLaunch(IntPtr d,IntPtr popup,float[] bounds,float[] grains,int count,int duration,byte[] vs,int vl,byte[] ps,int pl);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneLaunchFinished(IntPtr d);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void SceneEndLaunch(IntPtr d);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneReadSized(IntPtr d,[Out] byte[] pixels,int stride,int width,int height);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void SceneStats(IntPtr d,out Stats result);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneProfile(IntPtr d,out PerfStats result,[Out] FrameStamp[] frames,int count);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneProfileDetail(IntPtr d,out LayerStats result,[Out] GpuStamp[] frames,int count);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void SceneClose(IntPtr d);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneHealth(IntPtr d);
        internal string Health{get{return device==IntPtr.Zero?"unopened":"0x"+SceneHealth(device).ToString("X8");}}
        IntPtr device;int nextImage;
        IntPtr uploadScratch;
        int scratchBytes;
        static readonly Lazy<byte[]> vertex=new Lazy<byte[]>(()=>Load("WindowSceneVertex")),pixel=new Lazy<byte[]>(()=>Load("WindowScenePixel"));
        static byte[] Load(string name){
            using(var stream=typeof(SceneDevice).Assembly.GetManifestResourceStream("AstralSplit."+name+".ps"))
            using(var output=new MemoryStream()){stream.CopyTo(output);return output.ToArray();}
        }
        // The render worker applies these dimensions with the first matching packet.
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal void SetSize(int width,int height){Width=width;Height=height;}
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneOwnsMonitor(IntPtr device,IntPtr monitor);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneYieldTarget(IntPtr device);
        internal bool OwnsMonitor(IntPtr monitor){return SceneOwnsMonitor(device,monitor)!=0;}
        internal void YieldTarget(){Check(SceneYieldTarget(device));}
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern void ScenePark(IntPtr d,int park);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneMeteor(IntPtr d,int direction);
        internal void Park(bool park){if(device!=IntPtr.Zero)ScenePark(device,park?1:0);}
        internal int Meteor(int direction=0){return device==IntPtr.Zero?-2:SceneMeteor(device,direction);}
        static readonly Lazy<byte[]> meteorVertex=new Lazy<byte[]>(()=>Load("meteorVS")), meteorPixel=new Lazy<byte[]>(()=>Load("meteorPS"));
        internal void BeginMeteor(IntPtr popup,float[] bounds,float[] grains,int duration){
            var vs=meteorVertex.Value;var ps=meteorPixel.Value;
            Check(SceneBeginLaunch(device,popup,bounds,grains,grains.Length/12,duration,vs,vs.Length,ps,ps.Length));
        }
        internal int Landing => device==IntPtr.Zero?0:SceneLaunchFinished(device);
        internal void BeginLaunch(IntPtr popup,float[] bounds,float[] grains)
        {
            var vs=Load("launchVS");var ps=Load("launchPS");
            Check(SceneBeginLaunch(device,popup,bounds,grains,grains.Length/12,StellarSingularity.OpenMilliseconds,vs,vs.Length,ps,ps.Length));
        }
        internal bool LaunchFinished => device!=IntPtr.Zero && SceneLaunchFinished(device)!=0;
        internal void EndLaunch(){if(device!=IntPtr.Zero)SceneEndLaunch(device);}
        internal SceneDevice(IntPtr hwnd,int w,int h,bool desktop){
            Width=w;Height=h;Check(SceneOpen(hwnd,desktop?1:0,w,h,vertex.Value,vertex.Value.Length,pixel.Value,pixel.Value.Length,out device));
            try{
                string[] vs={"nativeMoteVS","nativeFieldVS","nativeDustVS","solarVS"},ps={"MotePixel","MagneticPixel","DustPixel","solarPS"};
                for(int i=0;i<vs.Length;i++){var a=Load(vs[i]);var b=Load(ps[i]);Check(SceneProgram(device,i,a,a.Length,b,b.Length));}
            }catch{Dispose();throw;}
        }
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneParticles(IntPtr d,int id,IntPtr batch,int slot);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneVectorTile(IntPtr d,int id,int w,int h,ref SceneVectorData.Info info,
            SceneVectorData.Segment[] segments,int count,SceneVectorData.Stop[] stops,int colors);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneProgram(IntPtr d,int kind,byte[] vs,int vl,byte[] ps,int pl);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneStream(IntPtr d,int id,int kind,int w,int h,float[] camera,
            float[] data,int length,uint[] colors,int count);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneRibbon(IntPtr d,int id,int w,int h,
            FlowBatch.RibbonVertex[] points,int count,uint[] colors,float[] widths,float[] view,float[] spins,int stars);
        [DefaultDllImportSearchPaths(Search),DllImport(Dll)]static extern int SceneOrbitCode(IntPtr d,byte[] program,int[] sizes,float[] table);
        readonly float[] streamCamera=new float[4];
        float[] streamValues=Array.Empty<float>();
        uint[] streamColors=Array.Empty<uint>();
        bool hasOrbitCode;
        internal int Ribbons(SceneRibbonData lines,int reuse){
            if(lines.Stars>0 && !hasOrbitCode){
                int[] sizes=new int[5];int at=0;
                using(var stream=new MemoryStream()){
                    foreach(var name in new[]{"orbitVS","orbitGS","orbitMask","orbitQuad","orbitInk"}){
                        var bytes=Load(name);sizes[at++]=bytes.Length;stream.Write(bytes,0,bytes.Length);
                    }
                    Check(SceneOrbitCode(device,stream.ToArray(),sizes,lines.Table));hasOrbitCode=true;
                }
            }
            int id=reuse>0?reuse:++nextImage;
            Check(SceneRibbon(device,id,lines.W,lines.H,lines.Points,lines.Points?.Length??0,lines.Colors,lines.Widths,lines.View,lines.Angles,lines.Stars));return id;
        }
        internal int Stream(SceneStreamData frame,int reuse){
            int id=reuse>0?reuse:++nextImage;var b=frame.Region;
            streamCamera[0]=(float)b.X;streamCamera[1]=(float)b.Y;streamCamera[2]=(float)(1/b.Width);streamCamera[3]=(float)(1/b.Height);
            int values=frame.Values.Count,colors=frame.Colors.Count;
            if(streamValues.Length<values)streamValues=new float[Math.Max(values,streamValues.Length*2)];
            if(streamColors.Length<colors)streamColors=new uint[Math.Max(colors,streamColors.Length*2)];
            frame.Values.CopyTo(streamValues,0);frame.Colors.CopyTo(streamColors,0);
            Check(SceneStream(device,id,frame.Kind,frame.W,frame.H,streamCamera,streamValues,values,streamColors,colors));return id;
        }
        internal int Vector(SceneVectorData path,int w,int h,int reuse){
            int key=reuse>0?reuse:++nextImage;
            Check(SceneVectorTile(device,key,w,h,ref path.Style,path.Path.ToArray(),path.Path.Count,path.Stops.ToArray(),path.Stops.Count));return key;
        }
        internal int Import(IntPtr batch,int slot,int reuse){
            int key=reuse>0?reuse:++nextImage;Check(SceneParticles(device,key,batch,slot));return key;
        }
        internal int Upload(BitmapSource bitmap,int reuse=0){
            if(bitmap==null || bitmap.PixelWidth<=0 || bitmap.PixelHeight<=0)return 0;
            if(bitmap.Format!=PixelFormats.Pbgra32)bitmap=new FormatConvertedBitmap(bitmap,PixelFormats.Pbgra32,null,0);
            int w=bitmap.PixelWidth,h=bitmap.PixelHeight,stride=checked(w*4);
            int bytes=checked(stride*h);
            // WIC copies into native memory. A full-resolution sky no longer leaves
            // a second, short-lived 100+ MB array on the managed large object heap.
            bool large=bytes>1024*1024;
            if(!large && scratchBytes<bytes){
                int capacity=Math.Max(4096,bytes);
                var next=Marshal.AllocHGlobal(capacity);
                Marshal.FreeHGlobal(uploadScratch);uploadScratch=next;scratchBytes=capacity;
            }
            IntPtr pixels=large?Marshal.AllocHGlobal(bytes):uploadScratch;
            try{
                bitmap.CopyPixels(Int32Rect.Empty,pixels,bytes,stride);
                int key=reuse!=0?reuse:++nextImage;
                Check(SceneImage(device,key,w,h,pixels,stride));return key;
            }
            finally{if(large)Marshal.FreeHGlobal(pixels);}
        }
        internal void Drop(int id){if(device!=IntPtr.Zero && id>0)SceneReleaseImage(device,id);}
        internal Stats ReadStats(){SceneStats(device,out var r);Check(r.Status);return r;}
        internal PerfStats ReadPerf(){SceneProfile(device,out var stats,null,0);return stats;}
        internal LayerStats ReadLayerStats(){SceneProfileDetail(device,out var stats,null,0);return stats;}
        internal GpuStamp[] ReadGpuStamps(){
            var rows=new GpuStamp[1024];int count=SceneProfileDetail(device,out _,rows,rows.Length);
            if(count!=rows.Length)Array.Resize(ref rows,count);
            return rows;
        }
        internal FrameStamp[] ReadFrameStamps(){
            var rows=new FrameStamp[1024];int count=SceneProfile(device,out _,rows,rows.Length);
            if(count!=rows.Length)Array.Resize(ref rows,count);
            return rows;
        }
        internal void Show(){Check(SceneShow(device));}
        internal BitmapSource Capture(){
            int stride=checked(Width*4);var data=new byte[checked(stride*Height)];Check(SceneReadSized(device,data,stride,Width,Height));
            var image=BitmapSource.Create(Width,Height,96,96,PixelFormats.Pbgra32,null,data,stride);image.Freeze();return image;
        }
        internal sealed class Pass {
            internal int Slot,W,H;internal Rect Area;internal readonly List<Command> Draws=new List<Command>();
            internal ulong Identity;
            internal Stage Kind;
            internal double Sx{get{return W/Area.Width;}} internal double Sy{get{return H/Area.Height;}}
            internal Pass(int slot,Rect area,int w,int h){Reset(slot,area,w,h);}
            internal void Reset(int slot,Rect area,int w,int h){
                Slot=slot;Area=area;W=w;H=h;Identity=0;Kind=slot==0?Stage.Composite:Stage.Layers;Draws.Clear();
            }
        }
        internal static Command Quad(Pass pass,Rect area,Matrix transform,Rect clip,int image,Rect uv,double alpha,int mode,double spin=0){
            Point at=transform.Transform(area.TopLeft);
            Vector x=transform.Transform(new Vector(area.Width,0)),y=transform.Transform(new Vector(0,area.Height));
            var c=new Command{Image=image,Material=mode};
            c.Value[0]=(float)((at.X-pass.Area.X)/pass.Area.Width*2-1);c.Value[1]=(float)(1-(at.Y-pass.Area.Y)/pass.Area.Height*2);
            c.Value[2]=(float)(x.X/pass.Area.Width*2);c.Value[3]=(float)(-x.Y/pass.Area.Height*2);
            c.Value[4]=(float)(y.X/pass.Area.Width*2);c.Value[5]=(float)(-y.Y/pass.Area.Height*2);
            c.Value[6]=(float)uv.X;c.Value[7]=(float)uv.Y;c.Value[8]=(float)uv.Width;c.Value[9]=(float)uv.Height;
            c.Value[10]=(float)alpha;c.Value[11]=(float)spin;
            clip.Intersect(pass.Area);
            if(!clip.IsEmpty){
                c.Clip[0]=(int)Math.Max(0,Math.Floor((clip.Left-pass.Area.Left)*pass.Sx));
                c.Clip[1]=(int)Math.Max(0,Math.Floor((clip.Top-pass.Area.Top)*pass.Sy));
                c.Clip[2]=(int)Math.Min(pass.W,Math.Ceiling((clip.Right-pass.Area.Left)*pass.Sx));
                c.Clip[3]=(int)Math.Min(pass.H,Math.Ceiling((clip.Bottom-pass.Area.Top)*pass.Sy));
            }
            return c;
        }
        PassData[] submissionPasses=new PassData[0];
        PassTag[] submissionTags=new PassTag[0];
        Command[] submissionDraws=new Command[0];
        internal long Submit(List<Pass> ordered){
            int n=0;foreach(var p in ordered)n=checked(n+p.Draws.Count);
            if(n>65536 || ordered.Count>384)throw new NotSupportedException("Scene command allocation exceeds capacity.");
            if(submissionPasses.Length<ordered.Count){
                int capacity=Math.Min(384,Math.Max(ordered.Count,submissionPasses.Length*2));
                submissionPasses=new PassData[capacity];submissionTags=new PassTag[capacity];
            }
            if(submissionDraws.Length<n)submissionDraws=new Command[Math.Min(65536,Math.Max(n,submissionDraws.Length*2))];
            int at=0;
            for(int i=0;i<ordered.Count;i++){var p=ordered[i];submissionPasses[i]=new PassData{Slot=p.Slot,W=p.W,H=p.H,First=at,Count=p.Draws.Count};
                submissionTags[i]=new PassTag{Identity=p.Identity,Stage=p.Kind};
                p.Draws.CopyTo(submissionDraws,at);at+=p.Draws.Count;}
            // SetPacket copies the populated ranges before returning to the dispatcher.
            Check(SceneSubmitTagged(device,submissionPasses,submissionTags,ordered.Count,submissionDraws,n,out var sequence));return sequence;
        }
        static void Check(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
        internal Task Retire(){
            var closing=device;device=IntPtr.Zero;
            var scratch=uploadScratch;uploadScratch=IntPtr.Zero;scratchBytes=0;
            return Task.Run(()=>{
                if(closing!=IntPtr.Zero)SceneClose(closing);
                Marshal.FreeHGlobal(scratch);
            });
        }
        public void Dispose(){
            if(device!=IntPtr.Zero){SceneClose(device);device=IntPtr.Zero;}
            Marshal.FreeHGlobal(uploadScratch);uploadScratch=IntPtr.Zero;scratchBytes=0;
        }
    }
}
