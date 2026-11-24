using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    // Only atlas workers call the CUDA driver. The shipping program contains both
    // a native Blackwell module and forward-compatible PTX, and requires no toolkit.
    internal sealed unsafe class GalaxyGpu
    {
        static readonly Lazy<GalaxyGpu> backend = new Lazy<GalaxyGpu>(Open);
        static int preparing;
        readonly IntPtr context, ridge, paint, tone;

        GalaxyGpu(IntPtr context, IntPtr module)
        {
            this.context=context;
            Check(cuModuleGetFunction(out ridge,module,"Ridge"));
            Check(cuModuleGetFunction(out paint,module,"Base"));
            Check(cuModuleGetFunction(out tone,module,"Tone"));
        }

        internal static void Prepare()
        {
            if(Interlocked.Exchange(ref preparing,1)==0) Task.Run(() => backend.Value);
        }

        sealed class DriverError : Exception { }
        static void Check(int result) { if(result!=0) throw new DriverError(); }

        static GalaxyGpu Open()
        {
            try {
                Check(cuInit(0));
                Check(cuDeviceGetCount(out int count));
                for(int i=0;i<count;i++) {
                    if(cuDeviceGet(out int device,i)!=0 || cuDevicePrimaryCtxRetain(out IntPtr ctx,device)!=0)continue;
                    bool keep=false;
                    try {
                        Check(cuCtxPushCurrent_v2(ctx));
                        try {
                            cuDeviceGetAttribute(out int major,75,device);
                            cuDeviceGetAttribute(out int minor,76,device);
                            var module=major==12 && minor==0 ? Load("Galaxy.cubin") : IntPtr.Zero;
                            if(module==IntPtr.Zero) module=Load("Galaxy.ptx");
                            if(module==IntPtr.Zero)continue;
                            try {
                                var gpu=new GalaxyGpu(ctx,module); keep=true; return gpu;
                            } finally { if(!keep)cuModuleUnload(module); }
                        } finally {cuCtxPopCurrent_v2(out _);}
                    } catch(DriverError) { }
                    finally {if(!keep)cuDevicePrimaryCtxRelease_v2(device);}
                }
            } catch(Exception e) when(e is DriverError || e is DllNotFoundException ||
                                       e is EntryPointNotFoundException || e is BadImageFormatException) { }
            return null;
        }

        static IntPtr Load(string name)
        {
            using(var s=typeof(GalaxyGpu).Assembly.GetManifestResourceStream("AstralSplit."+name)) {
                if(s==null)return IntPtr.Zero;
                using(var bytes=new MemoryStream()) {
                    s.CopyTo(bytes);
                    return cuModuleLoadData(out IntPtr module,bytes.ToArray())==0 ? module : IntPtr.Zero;
                }
            }
        }

        internal static BitmapSource TryBake(int w,int h,int day,double scale,BitmapSource[] tints)
        {
            var gpu=backend.Value;
            if(gpu==null)return null;
            try {return gpu.Bake(w,h,day,scale,tints);}
            catch(DriverError) {return null;}
        }

        BitmapSource Bake(int w,int h,int day,double scale,BitmapSource[] tints)
        {
            int size=checked(w*h*4);
            var bytes=new byte[size];
            ulong columns=0, original=0, tinted=0;
            Check(cuCtxPushCurrent_v2(context));
            try {
                Check(cuMemAlloc_v2(out columns,(UIntPtr)(uint)(w*8)));
                Check(cuMemAlloc_v2(out original,(UIntPtr)(uint)size));
                Check(cuMemAlloc_v2(out tinted,(UIntPtr)(uint)size));
                IntPtr* args=stackalloc IntPtr[5];
                args[0]=(IntPtr)(&columns);args[1]=(IntPtr)(&w);args[2]=(IntPtr)(&day);
                Check(cuLaunchKernel(ridge,(uint)((w+127)/128),1,1,128,1,1,0,IntPtr.Zero,args,IntPtr.Zero));
                args[1]=(IntPtr)(&original);args[2]=(IntPtr)(&w);args[3]=(IntPtr)(&h);args[4]=(IntPtr)(&day);
                uint gx=(uint)((w+31)/32),gy=(uint)((h+7)/8);
                Check(cuLaunchKernel(paint,gx,gy,1,32,8,1,0,IntPtr.Zero,args,IntPtr.Zero));
                fixed(byte* buffer=bytes) {
                    Check(cuMemcpyDtoH_v2((IntPtr)buffer,original,(UIntPtr)(uint)size));
                    var image=SkyAtlas.Pixels(bytes,w,h,scale);
                    args[0]=(IntPtr)(&original);args[1]=(IntPtr)(&tinted);
                    for(int i=0;i<tints.Length;i++){
                        var color=SkyAtlas.TintColors[i];int rgb=(color.R<<16)|(color.G<<8)|color.B;
                        args[4]=(IntPtr)(&rgb);
                        Check(cuLaunchKernel(tone,gx,gy,1,32,8,1,0,IntPtr.Zero,args,IntPtr.Zero));
                        Check(cuMemcpyDtoH_v2((IntPtr)buffer,tinted,(UIntPtr)(uint)size));
                        tints[i]=SkyAtlas.Pixels(bytes,w,h,scale);
                    }
                    return image;
                }
            } finally {
                if(tinted!=0)cuMemFree_v2(tinted);
                if(original!=0)cuMemFree_v2(original);
                if(columns!=0)cuMemFree_v2(columns);
                cuCtxPopCurrent_v2(out _);
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuInit(uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuDeviceGetCount(out int count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuDeviceGet(out int device,int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuDeviceGetAttribute(out int value,int attribute,int device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuDevicePrimaryCtxRetain(out IntPtr context,int device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuDevicePrimaryCtxRelease_v2(int device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuCtxPushCurrent_v2(IntPtr ctx);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuCtxPopCurrent_v2(out IntPtr ctx);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuModuleLoadData(out IntPtr module,byte[] image);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuModuleUnload(IntPtr module);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuModuleGetFunction(out IntPtr function,IntPtr module,string name);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuMemAlloc_v2(out ulong address,UIntPtr bytes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuMemFree_v2(ulong address);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuMemcpyDtoH_v2(IntPtr destination,ulong source,UIntPtr bytes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]
        static extern int cuLaunchKernel(IntPtr function,uint gx,uint gy,uint gz,uint bx,uint by,uint bz,uint shared,IntPtr stream,IntPtr* args,IntPtr extra);
    }
}
