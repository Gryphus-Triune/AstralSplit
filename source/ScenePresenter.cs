using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AstralSplit
{
    // WPF continues to own focus, layout, hit testing and the network session. The
    // compositor takes over only after a complete frame for this exact client exists.
    internal sealed class ScenePresenter : IDisposable
    {
        readonly Window window;readonly FrameworkElement surface;readonly StarfieldLayer sky;readonly Func<bool> eligible;
        SceneDevice device;SceneGraph graph;UIElement presenter;
        Task<SceneDevice> opening;
        Task retirement=Task.CompletedTask;
        Task<SceneDevice> nextAdapter;
        SceneDevice pendingDevice;SceneGraph pendingGraph;
        long pendingPacket,resizePacket;
        IntPtr retryMonitor;long retryAdapterAfter;
        bool geometryDirty=true,notifyGeometry;
        bool disposed,installed,blocked;int losses;long retryAfter,stableSince;double originalOpacity;Size size;DpiScale dpi;IntPtr monitor;
        internal long Sequence{get;private set;}
        internal string Failure{get;private set;}
        internal bool Active{get{return installed;}}
        bool launchReady,parked;
        internal bool Flying{get;set;}
        internal bool Retained => parked && installed;
        bool Hidden => !window.IsVisible || window.WindowState==WindowState.Minimized;
        bool KeepHidden(){
            if(!Hidden || !installed || disposed || blocked || SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation)return false;
            if(!parked){CancelAdapterChange();geometryDirty=true;device.Park(true);parked=true;}
            return true;
        }
        internal void Resume(){
            if(parked){device.Park(false);parked=false;}
        }
        internal void WpfFlight(){Flying=false;Release();}
        internal SceneDevice ReadyDevice => installed && launchReady && resizePacket==0 && pendingDevice==null && nextAdapter==null &&
            (Flying || !geometryDirty || surface.RenderSize==size && VisualTreeHelper.GetDpi(surface).Equals(dpi) && Screen()==monitor) ? device : null;
        internal bool FitsFlight(Rect physical){
            return ReadyDevice!=null && Screen()==monitor && VisualTreeHelper.GetDpi(surface).Equals(dpi) &&
                Math.Abs(physical.Width-device.Width)<=1 && Math.Abs(physical.Height-device.Height)<=1;
        }
        internal event EventHandler FirstFrameReady;
        internal static bool Requested{get{return Environment.GetEnvironmentVariable("ASTRALSPLIT_WINDOW_SCENE")=="1";}}
        internal ScenePresenter(Window w,StarfieldLayer field,Func<bool> ready){
            window=w;sky=field;surface=(FrameworkElement)w.Content;eligible=ready;
            surface.SizeChanged+=Changed;window.DpiChanged+=DpiChanged;window.LocationChanged+=Moved;
            window.IsVisibleChanged+=Visible;window.StateChanged+=Recheck;window.Closed+=Closed;
            UiFrames.Rendering+=Tick;
        }
        bool CanDraw(){return !disposed && !blocked && window.IsVisible && window.WindowState!=WindowState.Minimized &&
            !SystemParameters.HighContrast && SystemParameters.ClientAreaAnimation && eligible() && GlassEffect.Hardware3(sky);}
        void Changed(object s,SizeChangedEventArgs e){Schedule();}
        void DpiChanged(object s,DpiChangedEventArgs e){Schedule();}
        void Moved(object s,EventArgs e){if((device!=null || opening!=null) && Screen()!=monitor)Schedule();}
        void Recheck(object s,EventArgs e){if(KeepHidden())return;if(!CanDraw())Release();else Schedule();}
        void Visible(object s,DependencyPropertyChangedEventArgs e){Recheck(s,EventArgs.Empty);}
        void Closed(object s,EventArgs e){Dispose();}
        void Schedule(){geometryDirty=true;if(launchReady && ReadyDevice==null)notifyGeometry=true;}
        IntPtr Screen(){return MonitorFromWindow(new WindowInteropHelper(window).Handle,2);}
        void Abandon(Task<SceneDevice> task){
            if(task==null)return;
            var cleanup=task.ContinueWith(t=>{
                if(t.IsFaulted){var observed=t.Exception;}
                else if(!t.IsCanceled)t.Result.Dispose();
            },TaskScheduler.Default);
            TrackRetirement(cleanup);
        }
        void TrackRetirement(Task cleanup){
            retirement=retirement.IsCompleted?cleanup:Task.WhenAll(retirement,cleanup);
        }
        void CancelAdapterChange(){
            Abandon(nextAdapter);nextAdapter=null;
            pendingGraph?.Dispose();pendingGraph=null;
            if(pendingDevice!=null){TrackRetirement(pendingDevice.Retire());pendingDevice=null;}
            pendingPacket=0;
        }
        bool PrepareAdapter(IntPtr target,Size current,int width,int height){
                if(nextAdapter==null && pendingDevice==null){
                    var hwnd=new WindowInteropHelper(window).Handle;
                    nextAdapter=Task.Run(()=>new SceneDevice(hwnd,width,height,true));
                }
                if(nextAdapter!=null){
                    if(!nextAdapter.IsCompleted)return false;
                    var ready=nextAdapter;nextAdapter=null;pendingDevice=ready.GetAwaiter().GetResult();
                    if(!pendingDevice.OwnsMonitor(target)){CancelAdapterChange();return false;}
                    pendingGraph=new SceneGraph(pendingDevice,sky);
                }
                if(!pendingDevice.OwnsMonitor(target)){CancelAdapterChange();return false;}
                if(pendingPacket==0 || pendingDevice.Width!=width || pendingDevice.Height!=height){
                    pendingDevice.SetSize(width,height);pendingPacket=pendingGraph.Publish(surface,new Rect(current));return false;
                }
                if(pendingDevice.ReadStats().Sequence<pendingPacket)return false;
                // Only one topmost composition target may own this HWND. The replacement
                // swapchain already contains its first frame before transferring ownership.
                if(installed){device.YieldTarget();pendingDevice.Show();}
                graph.Dispose();TrackRetirement(device.Retire());
                device=pendingDevice;graph=pendingGraph;Sequence=pendingPacket;
                graph.NativeScrolling=installed;
                pendingGraph=null;pendingDevice=null;pendingPacket=0;
                resizePacket=0;return true;
        }
        static bool GraphicsError(Exception ex){
            return ex is ExternalException || ex is InvalidOperationException || ex is NotSupportedException ||
                ex is OverflowException || ex is ArgumentException || ex is OutOfMemoryException ||
                ex is DllNotFoundException || ex is EntryPointNotFoundException;
        }
        bool SyncGeometry(){
            var target=Screen();var current=surface.RenderSize;var scale=VisualTreeHelper.GetDpi(surface);
            int width=Math.Max(1,(int)Math.Round(current.Width*scale.DpiScaleX));
            int height=Math.Max(1,(int)Math.Round(current.Height*scale.DpiScaleY));
            bool checkAdapter=target!=monitor || target==retryMonitor && Stopwatch.GetTimestamp()>=retryAdapterAfter;
            if(checkAdapter && !device.OwnsMonitor(target)){
                if(launchReady)notifyGeometry=true;
                try{
                    if(!PrepareAdapter(target,current,width,height))return false;
                    retryMonitor=IntPtr.Zero;
                }catch(Exception ex) when(GraphicsError(ex)){
                    CancelAdapterChange();
                    if(installed)device.Show();
                    retryMonitor=target;retryAdapterAfter=Stopwatch.GetTimestamp()+5*Stopwatch.Frequency;
                    Trace.WriteLine("Retaining prior display adapter: "+ex);
                }
            }else{
                if(checkAdapter || target!=retryMonitor)retryMonitor=IntPtr.Zero;
                if(nextAdapter!=null || pendingDevice!=null)CancelAdapterChange();
            }
            bool changed=device.Width!=width || device.Height!=height;
            size=current;dpi=scale;monitor=target;geometryDirty=false;
            if(changed){
                if(launchReady)notifyGeometry=true;
                device.SetSize(width,height);Sequence=graph.Publish(surface,new Rect(size));resizePacket=Sequence;return false;
            }
            return true;
        }
        void Tick(object s,EventArgs e){
            if(KeepHidden())return;
            if(!CanDraw()){if(device!=null || opening!=null)Release();return;}
            Resume();
            if(Stopwatch.GetTimestamp()<retryAfter || surface.ActualWidth<1 || surface.ActualHeight<1)return;
            try{
                if(device==null){
                    if(opening==null){
                        if(!retirement.IsCompleted)return;
                        size=surface.RenderSize;dpi=VisualTreeHelper.GetDpi(surface);monitor=Screen();
                        presenter=VisualTreeHelper.GetParent(surface) as UIElement;
                        if(presenter==null || presenter==window || presenter.Opacity!=1)return;
                        originalOpacity=presenter.Opacity;
                        var handle=new WindowInteropHelper(window).Handle;
                        int width=(int)Math.Round(size.Width*dpi.DpiScaleX),height=(int)Math.Round(size.Height*dpi.DpiScaleY);
                        opening=Task.Run(()=>new SceneDevice(handle,width,height,true));
                    }
                    if(!opening.IsCompleted)return;
                    var ready=opening;opening=null;device=ready.GetAwaiter().GetResult();graph=new SceneGraph(device,sky);
                }
                if(!sky.Ready)return;
                if(!Flying && (geometryDirty || surface.RenderSize!=size || !VisualTreeHelper.GetDpi(surface).Equals(dpi) ||
                    retryMonitor!=IntPtr.Zero && Stopwatch.GetTimestamp()>=retryAdapterAfter) && !SyncGeometry())return;
                if(resizePacket!=0){
                    if(device.ReadStats().Sequence<resizePacket)return;
                    resizePacket=0;
                }
                if(notifyGeometry && ReadyDevice!=null){
                    notifyGeometry=false;FirstFrameReady?.Invoke(this,EventArgs.Empty);return;
                }
                if(!installed && Sequence!=0){
                    // Leave the cold packet with the GPU until it has really drawn it.
                    if(device.ReadStats().Sequence==0)return;
                    device.Show();presenter.SetCurrentValue(UIElement.OpacityProperty,0d);installed=true;graph.NativeScrolling=true;sky.NativeSolar(true);stableSince=Stopwatch.GetTimestamp();Failure=null;
                    return;
                }
                if(installed && !launchReady && Sequence>=2 && device.ReadStats().Sequence>=2){
                    // Switching the solar renderer and realizing the first visible rows
                    // produce another packet. Let that packet reach the GPU before ignition.
                    launchReady=true;
                    FirstFrameReady?.Invoke(this,EventArgs.Empty);
                    // Opening a compatibility HWND can pump messages and retire this
                    // graph from inside the callback. The next tick owns the next packet.
                    return;
                }
                Sequence=graph.Publish(surface,new Rect(size));
                if(installed){device.ReadStats();if(losses>0 && Stopwatch.GetTimestamp()-stableSince>=4*Stopwatch.Frequency)losses=0;}
            }catch(Exception ex) when(GraphicsError(ex)){
                DeviceFailure(ex);
            }
        }
        void DeviceFailure(Exception ex){
            Failure=ex.ToString()+" (native device "+device?.Health+", seq "+Sequence+")";
            Trace.WriteLine("AstralSplit native scene: "+Failure);
            uint hr=unchecked((uint)ex.HResult);
            bool transient=hr==0x887A0005 || hr==0x887A0006 || hr==0x887A0007 || hr==0x8899000C;
            if(transient && losses<3){
                losses++;
                retryAfter=Stopwatch.GetTimestamp()+Stopwatch.Frequency*(1L<<(losses-1))/2;
            }else blocked=true;
            Release();
        }
        void Release(){
            CancelAdapterChange();resizePacket=0;retryMonitor=IntPtr.Zero;geometryDirty=true;notifyGeometry=false;
            if(opening!=null){
                // An obsolete monitor or a closed HWND can finish opening after this presenter.
                var abandoned=opening;opening=null;
                Abandon(abandoned);
            }
            var ending=device;device=null;
            if(installed){
                sky.NativeSolar(false);
                if(presenter!=null)presenter.SetCurrentValue(UIElement.OpacityProperty,originalOpacity);
                // Remove the native layer before returning to WPF. Waiting for
                // the render worker or the driver can happen after this handoff.
                try{ending?.YieldTarget();}
                catch(Exception ex) when(GraphicsError(ex)){Trace.WriteLine("Removing the native scene: "+ex);}
            }
            parked=false;installed=false;launchReady=false;Sequence=0;graph?.Dispose();graph=null;presenter=null;
            if(ending!=null)TrackRetirement(ending.Retire());
        }
        public void Dispose(){
            if(disposed)return;disposed=true;UiFrames.Rendering-=Tick;
            surface.SizeChanged-=Changed;window.DpiChanged-=DpiChanged;window.LocationChanged-=Moved;window.StateChanged-=Recheck;
            window.IsVisibleChanged-=Visible;window.Closed-=Closed;Release();
        }
        [DllImport("user32.dll")]static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    }
}
