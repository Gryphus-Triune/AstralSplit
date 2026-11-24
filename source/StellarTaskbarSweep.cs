using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed class StellarTaskbarSweep
    {
        const int FrameMilliseconds = 125;

        // The foreground lip runs left to right. The far side bends around the top of the shadow.
        // Coordinates follow the bright ridges in the 48 px face of AstralSplit.ico.
        static readonly Point[] Orbit = {
            new Point(.12, .54), new Point(.23, .56), new Point(.34, .57), new Point(.45, .58),
            new Point(.56, .58), new Point(.67, .57), new Point(.78, .55), new Point(.88, .52),
            new Point(.79, .50), new Point(.71, .45), new Point(.65, .37), new Point(.56, .32),
            new Point(.46, .31), new Point(.38, .36), new Point(.31, .44), new Point(.24, .50)
        };

        readonly Window window;
        readonly DispatcherTimer timer;
        readonly Stopwatch clock = new Stopwatch();
        BitmapSource[] frames;
        ImageSource restingIcon;
        bool shownForConnection;
        int displayed = -1;

        internal StellarTaskbarSweep(Window window)
        {
            this.window = window;
            timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher);
            timer.Tick += (s, e) => Advance();
            window.IsVisibleChanged += (s, e) => { if (!window.IsVisible) Stop(); };
            window.Closed += (s, e) => Stop();
        }

        internal void Update(bool connected, bool stateUnknown)
        {
            if (!connected)
            {
                // A failed status read followed by the same active session is not a reconnection.
                if (!stateUnknown) shownForConnection = false;
                Stop();
                return;
            }
            if (shownForConnection) return;
            shownForConnection = true;
            if (!window.IsVisible || !window.ShowInTaskbar ||
                !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;

            if (frames == null) frames = MakeFrames();
            restingIcon = window.Icon;
            clock.Restart();
            Advance();
            if (clock.IsRunning) timer.Start();
        }

        void Advance()
        {
            if (!clock.IsRunning) return;
            int frame = (int)(clock.ElapsedMilliseconds / FrameMilliseconds);
            if (frame >= Orbit.Length) { Stop(); return; }
            if (frame != displayed)
            {
                displayed = frame;
                window.SetCurrentValue(Window.IconProperty, frames[frame]);
            }
            // Align ticks to elapsed time so WPF or Explorer delays do not queue stale frames.
            long untilNext = (frame + 1) * FrameMilliseconds - clock.ElapsedMilliseconds;
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, untilNext));
        }

        internal void Stop()
        {
            if (displayed < 0) return;
            timer.Stop();
            clock.Reset();
            displayed = -1;
            window.SetCurrentValue(Window.IconProperty, restingIcon);
            restingIcon = null;
        }

        static BitmapSource[] MakeFrames()
        {
            var source = new FormatConvertedBitmap(StellarSkyArt.ReadImage("tray-horizon.png"), PixelFormats.Bgra32, null, 0);
            int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
            var original = new byte[stride * height];
            source.CopyPixels(original, stride, 0);
            var result = new BitmapSource[Orbit.Length];
            for (int frame = 0; frame < result.Length; frame++)
            {
                var head = Orbit[frame];
                var tail = Orbit[(frame + Orbit.Length - 1) % Orbit.Length];
                var segment = head - tail;
                double fade = frame == 0 || frame == result.Length - 1 ? .65 : 1;
                var pixels = (byte[])original.Clone();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * stride + x * 4;
                    if (original[i + 2] <= 30 || original[i + 3] == 0) continue;
                    var point = new Point((x + .5) / width, (y + .5) / height);
                    double u = Math.Max(0, Math.Min(1, Vector.Multiply(point - tail, segment) / segment.LengthSquared));
                    var distance = point - (tail + u * segment);
                    double mask = Math.Min(1, (original[i + 2] - 30) / 120.0);
                    double light = fade * mask * (.18 + .82 * u) * Math.Exp(-distance.LengthSquared / .008);
                    // A warm highlight changes only lit material. Alpha and the black horizon stay intact.
                    pixels[i] = Lift(original[i], 198, light);
                    pixels[i + 1] = Lift(original[i + 1], 249, light);
                    pixels[i + 2] = Lift(original[i + 2], 255, light);
                }
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                result[frame] = image;
            }
            return result;
        }

        static byte Lift(byte value, int highlight, double intensity)
        {
            return (byte)(value + Math.Round(Math.Max(0, highlight - value) * intensity));
        }
    }
}
