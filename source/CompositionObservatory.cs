using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        readonly ContainerVisual compositionBase=new ContainerVisual(),compositionUpper=new ContainerVisual(),nativeForeground=new ContainerVisual();
        readonly DrawingVisual lensForegroundMirror=new DrawingVisual();
        internal Visual CompositionBase {get{return compositionBase;}}
        internal NebulaSky CompositionVolume {get{return volume;}}
        internal static VisualBrush LiveView(Visual source,Rect area){
            return new VisualBrush(source){AutoLayoutContent=false,ViewboxUnits=BrushMappingMode.Absolute,
                Viewbox=area,Stretch=Stretch.Fill,TileMode=TileMode.None,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top};
        }
        internal void SplitComposition(bool enable){
            if(enable==(VisualTreeHelper.GetParent(nativeForeground)==scene))return;
            if(enable){
                var area=new Rect(RenderSize);
                using(var d=lensForegroundMirror.RenderOpen())d.DrawRectangle(LiveView(compositionUpper,area),null,area);
                scene.Children.Remove(transmittedSky);naturalSky.Children.Remove(compositionUpper);
                naturalSky.Children.Add(lensForegroundMirror);transmittedSky.Children.Remove(lensPatch);
                nativeForeground.Children.Add(compositionUpper);nativeForeground.Children.Add(lensPatch);
                scene.Children.Insert(0,nativeForeground);
            }else{
                scene.Children.Remove(nativeForeground);nativeForeground.Children.Remove(lensPatch);nativeForeground.Children.Remove(compositionUpper);
                naturalSky.Children.Remove(lensForegroundMirror);using(lensForegroundMirror.RenderOpen()){}
                naturalSky.Children.Add(compositionUpper);transmittedSky.Children.Add(lensPatch);scene.Children.Insert(0,transmittedSky);
            }
        }
    }

    internal sealed class CompositionObservatory : IDisposable
    {
        const string Dll="AstralSplit.Graphics.dll";
        static readonly Lazy<byte[]> vertex=new Lazy<byte[]>(()=>Code("CompositionVertex")),pixel=new Lazy<byte[]>(()=>Code("CompositionVolume"));
        readonly Window window;readonly Grid surface;readonly StarfieldLayer sky;
        readonly UIElement parent,fill;readonly double parentOpacity;readonly Visibility originallyVisible;
        HwndSource rear,front;
        IntPtr device;
        bool split,disposed;
        Rect lastArea,client;
        DpiScale dpi;
        internal bool Failed {get;private set;}
        internal int Frames{get{return device==IntPtr.Zero?0:CompositionFrames(device);}}
        internal static bool Requested{get{return Environment.GetEnvironmentVariable("ASTRALSPLIT_COMPOSITION")=="1";}}
        static byte[] Code(string name){
            using(var stream=typeof(FlowBatch).Assembly.GetManifestResourceStream("AstralSplit."+name+".ps"))
            using(var dest=new MemoryStream()){stream.CopyTo(dest);return dest.ToArray();}
        }
        internal CompositionObservatory(Window host,StarfieldLayer field){
            window=host;sky=field;surface=window.Content as Grid;
            if(surface==null || surface.Children.Count<2 || !GlassEffect.Hardware3(sky))throw new InvalidOperationException("A composited WPF surface is required.");
            parent=VisualTreeHelper.GetParent(surface) as UIElement;
            if(parent==null || parent==host)throw new InvalidOperationException("Window presenter is unavailable.");
            parentOpacity=parent.Opacity;fill=surface.Children[0];originallyVisible=fill.Visibility;
            try{
                dpi=VisualTreeHelper.GetDpi(sky);client=new Rect(surface.RenderSize);
                IntPtr hwnd=new WindowInteropHelper(window).Handle;
                var origin=surface.PointToScreen(new Point());
                rear=Proxy(hwnd,"AstralSplit sky sources",sky.CompositionBase,origin);
                front=Proxy(hwnd,"AstralSplit controls and sky",surface,origin);
                var data=NebulaSky.Density;
                if(data.Format!=PixelFormats.Pbgra32)data=new FormatConvertedBitmap(data,PixelFormats.Pbgra32,null,0);
                int stride=checked(data.PixelWidth*4);var bytes=new byte[checked(stride*data.PixelHeight)];data.CopyPixels(bytes,stride,0);
                Check(CompositionOpen(hwnd,rear.Handle,front.Handle,(uint)Math.Ceiling(384*dpi.DpiScaleX),(uint)Math.Ceiling(144*dpi.DpiScaleY),
                    bytes,(uint)data.PixelWidth,(uint)data.PixelHeight,vertex.Value,(uint)vertex.Value.Length,pixel.Value,(uint)pixel.Value.Length,out device));
                Update(true);
                UiFrames.Rendering+=Tick;
                window.IsVisibleChanged+=VisibilityChange;window.StateChanged+=StateChange;window.Closed+=CloseWindow;
            }catch{Dispose();throw;}
        }
        HwndSource Proxy(IntPtr owner,string title,Visual visual,Point origin){
            var options=new HwndSourceParameters(title){
                ParentWindow=owner,WindowStyle=unchecked((int)0x88000000),ExtendedWindowStyle=0x080800A0,
                PositionX=(int)Math.Round(origin.X),PositionY=(int)Math.Round(origin.Y),
                Width=(int)Math.Ceiling(client.Width*dpi.DpiScaleX),Height=(int)Math.Ceiling(client.Height*dpi.DpiScaleY),
                UsesPerPixelOpacity=true,AdjustSizingForNonClientArea=false
            };
            var proxy=new HwndSource(options);
            try{
                int cloak=1;Check(DwmSetWindowAttribute(proxy.Handle,13,ref cloak,4));
                proxy.CompositionTarget.BackgroundColor=Colors.Transparent;
                var view=new DrawingVisual();
                using(var dc=view.RenderOpen())dc.DrawRectangle(StarfieldLayer.LiveView(visual,client),null,client);
                proxy.RootVisual=view;ShowWindow(proxy.Handle,8);return proxy;
            }catch{proxy.Dispose();throw;}
        }
        void VisibilityChange(object sender,DependencyPropertyChangedEventArgs e){if(!window.IsVisible)Dispose();}
        void CloseWindow(object sender,EventArgs e){Dispose();}
        void StateChange(object sender,EventArgs e){if(!window.IsVisible || window.WindowState==WindowState.Minimized || sender==null)Dispose();}
        void Tick(object sender,EventArgs args){
            if(disposed)return;
            if(!window.IsVisible || window.WindowState==WindowState.Minimized || client.Size!=surface.RenderSize ||
               !dpi.Equals(VisualTreeHelper.GetDpi(sky))){Dispose();return;}
            try{Update(false);}catch(COMException){Failed=true;Dispose();}
        }
        void Update(bool initial){
            if(!sky.CompositionVolume.Sample(out var area,out var eye,out var energy,out var drift,out var swell,out var move)){
                if(initial)throw new InvalidOperationException("The native volume is unavailable.");Dispose();return;
            }
            Check(CompositionEye(device,(float)eye.X,(float)eye.Y,(float)energy,drift,swell,move?1:0));
            if(initial || area!=lastArea){
                // Both proxies are at the same physical origin as the client and retain per-monitor DPI.
                Check(CompositionPlace(device,(float)(area.X*dpi.DpiScaleX),(float)(area.Y*dpi.DpiScaleY),
                    (float)(area.Width*dpi.DpiScaleX),(float)(area.Height*dpi.DpiScaleY)));lastArea=area;
            }
            if(!split && !initial && Frames>0){
                sky.SplitComposition(true);split=true;fill.SetCurrentValue(UIElement.VisibilityProperty,Visibility.Hidden);
                parent.SetCurrentValue(UIElement.OpacityProperty,0d);
            }
        }
        internal bool Closed{get{return disposed;}}
        public void Dispose(){
            if(disposed)return;disposed=true;
            UiFrames.Rendering-=Tick;window.IsVisibleChanged-=VisibilityChange;window.StateChanged-=StateChange;window.Closed-=CloseWindow;
            if(split){sky.SplitComposition(false);fill.SetCurrentValue(UIElement.VisibilityProperty,originallyVisible);parent.SetCurrentValue(UIElement.OpacityProperty,parentOpacity);split=false;}
            if(device!=IntPtr.Zero){CompositionClose(device);device=IntPtr.Zero;}
            front?.Dispose();rear?.Dispose();
        }
        static void Check(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
        [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int len);
        [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Dll,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]static extern int CompositionOpen(IntPtr window,IntPtr below,IntPtr above,uint w,uint h,
            byte[] image,uint imageW,uint imageH,byte[] vertex,uint vl,byte[] pixel,uint pl,out IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Dll,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]static extern int CompositionPlace(IntPtr device,float x,float y,float w,float h);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Dll,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]static extern int CompositionEye(IntPtr device,float x,float y,float energy,double drift,double swell,int moving);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Dll,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]static extern int CompositionFrames(IntPtr device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),DllImport(Dll,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]static extern void CompositionClose(IntPtr device);
    }
}
