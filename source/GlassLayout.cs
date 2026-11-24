using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AstralSplit
{
    // LayoutUpdated is broadcast across an entire dispatcher. Subscribe once per
    // presentation root so a sequence of list changes produces one coordinate pass.
    internal sealed class GlassLayout
    {
        static readonly ConditionalWeakTable<FrameworkElement,GlassLayout> roots = new ConditionalWeakTable<FrameworkElement,GlassLayout>();
        readonly FrameworkElement root;
        EventHandler clients;
        DispatcherOperation queued;
        GlassLayout(FrameworkElement root) { this.root=root; }
        internal static GlassLayout Connect(FrameworkElement child,EventHandler client)
        {
            var root=PresentationSource.FromVisual(child)?.RootVisual as FrameworkElement ?? child;
            var group=roots.GetValue(root,r=>new GlassLayout(r));
            if(group.clients==null)root.LayoutUpdated+=group.Invalidate;
            group.clients+=client;
            group.Invalidate(null,EventArgs.Empty);
            return group;
        }
        internal void Disconnect(EventHandler client)
        {
            clients-=client;
            if(clients!=null)return;
            root.LayoutUpdated-=Invalidate;
            queued?.Abort();
            queued=null;
        }
        void Invalidate(object sender,EventArgs args)
        {
            if(queued==null && !root.Dispatcher.HasShutdownStarted)
                queued=root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Flush));
        }
        void Flush() { queued=null; clients?.Invoke(root,EventArgs.Empty); }
    }
}
