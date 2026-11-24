using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace AstralSplit
{
    // Read the selected display mode of the monitor containing this window.
    internal static class DisplayRefresh
    {
        // Only the fixed fields used by EnumDisplaySettingsW are exposed here (DEVMODEW).
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        struct DisplayMode
        {
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(184)] internal uint Frequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        static IntPtr lastMonitor;
        static readonly Stopwatch age = new Stopwatch();
        static int? rate;
        internal static void Invalidate() { age.Reset(); }

        internal static int? For(Visual visual)
        {
            var source = PresentationSource.FromVisual(visual) as HwndSource;
            if (source == null) return null;
            var monitor = MonitorFromWindow(source.Handle, 2);
            if (monitor == lastMonitor && age.IsRunning && age.Elapsed.TotalSeconds < 5) return rate;
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
            bool known = EnumDisplaySettings(Forms.Screen.FromHandle(source.Handle).DeviceName, -1, ref mode);
            rate = known && mode.Frequency > 1 && mode.Frequency <= Int32.MaxValue ? (int?)mode.Frequency : null;
            // Windows can report the integer part of a fractional refresh mode.
            switch (rate)
            {
                case 59: rate = 60; break;
                case 119: rate = 120; break;
                case 143: rate = 144; break;
                case 164: rate = 165; break;
                case 179: rate = 180; break;
                case 239: rate = 240; break;
                case 359: rate = 360; break;
            }
            lastMonitor = monitor;
            age.Restart();
            return rate;
        }
    }
}
