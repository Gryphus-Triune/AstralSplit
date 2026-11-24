using System;
using System.Windows.Media;

namespace AstralSplit
{
    // One subscription per WPF dispatcher. Multicast invocation snapshots the active
    // listeners, including when a transition replaces itself from inside a frame.
    internal static class UiFrames
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint value);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]static extern uint timeEndPeriod(uint value);
        static readonly bool fineClock=Environment.GetEnvironmentVariable("ASTRALSPLIT_FINE_TIMER")=="1";
        [ThreadStatic] static bool fine;
        [ThreadStatic] static EventHandler listeners;
        [ThreadStatic] static TimeSpan? last;
        internal static event EventHandler Rendering
        {
            add {
                if (listeners == null) { last = null; if(fineClock)fine=timeBeginPeriod(1)==0; CompositionTarget.Rendering += Tick; }
                listeners += value;
            }
            remove {
                listeners -= value;
                if (listeners == null) { CompositionTarget.Rendering -= Tick; last = null; if(fine){timeEndPeriod(1);fine=false;} }
            }
        }
        static void Tick(object sender, EventArgs args)
        {
            var frame = args as RenderingEventArgs;
            if (frame != null) {
                if (last == frame.RenderingTime) return;
                last = frame.RenderingTime;
            }
            listeners?.Invoke(sender, args);
        }
    }
}
