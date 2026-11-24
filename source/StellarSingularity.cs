using System;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    // All geometry is in the overlay's local physical pixels, including monitors with a negative desktop origin.
    internal sealed class StellarSingularity
    {
        internal const int OpenMilliseconds = 1250, ExitMilliseconds = 1080, TrayExitMilliseconds = 1840;
        internal const double BurstStart = .43;
        const double WindowBirth = .555, TrayArrived = .55;
        const int PerSide = 180;
        enum FieldPhase { Gather, Shed, Burst }
        readonly Rect monitor;
        readonly Point center;
        readonly double scale;
        readonly bool hasWindow;
        readonly Grain[] grains = new Grain[4 * PerSide];
        readonly Ink[,] inks = new Ink[3, 3];
        readonly Pen wave, echo;
        readonly double waveRadius;
        Point? lastTrayOrigin;

        struct Grain
        {
            public Vector ToEdge, FromWindow, TrayControlA, TrayControlB;
            public double GatherCurl, BurstCurl, Start, End, Kick, Escape;
            public double Shed, Accreted;
            public double TrayEmission, TrayArrival, BendA, BendB, LeadA, LeadB;
            public double Radius, Tail, Pace;
            public int Color, Depth;
        }

        sealed class Ink
        {
            internal readonly Brush Light, Glow;
            internal readonly Pen Tail, Streak;
            internal Ink(Color color, int depth, double scale)
            {
                double strength = depth == 0 ? .46 : depth == 1 ? .72 : .97;
                Light = Solid(Blend(color, Colors.White, .27), strength);
                Glow = Halo(color, strength * .58);
                Tail = Stroke(color, strength * .39, scale * (.50 + .27 * depth));
                Streak = Stroke(color, strength * .77, scale * (.80 + .30 * depth));
            }
        }

        static readonly Color amber = Color.FromRgb(255, 177, 102);
        static readonly Color pearl = Color.FromRgb(255, 237, 200);
        static readonly Color ice = Color.FromRgb(163, 210, 246);
        static readonly Brush corona = Halo(amber, .80), bloom = Halo(pearl, .98), filament = Halo(ice, .5);
        static readonly Brush horizon = Horizon();

        internal StellarSingularity(Rect monitor, Point center, double pixelScale, Rect window = default(Rect))
        {
            this.monitor = monitor;
            this.center = center;
            scale = Math.Max(.5, Math.Min(5, pixelScale));
            hasWindow = !window.IsEmpty && window.Width > 0 && window.Height > 0;
            wave = Stroke(pearl, .62, .95 * scale);
            echo = Stroke(ice, .49, .7 * scale);
            waveRadius = Math.Min(210 * scale, Math.Min(monitor.Width,monitor.Height) * .17);
            var colors = new[] { amber, pearl, ice };
            for (int c = 0; c < colors.Length; c++)
                for (int depth = 0; depth < 3; depth++) inks[c, depth] = new Ink(colors[c], depth, scale);

            // The randomness belongs to this playback. No trajectory or color is rerolled while drawing.
            var random = new Random(Guid.NewGuid().GetHashCode());
            double phase = random.NextDouble() * 2 * Math.PI;
            int firstColor = random.Next(4);
            for (int i = 0; i < grains.Length; i++)
            {
                // Two loose streams on each edge. Keep samples across the whole edge while gathering more around each stream's spine.
                int side = i % 4, lane = i / 4, half = lane / (PerSide / 2);
                int stream = side * 2 + half;
                double along = (lane % (PerSide / 2) + .1 + .8 * random.NextDouble()) / (PerSide / 2);
                double f = (half + along + .60 * Math.Sin(2 * Math.PI * along) / (2 * Math.PI)) * .5;
                double bend = Math.Sin(phase + stream * 2.399963);
                double delay = .5 + .5 * Math.Cos(phase + stream * 2.399963);
                double beyond = scale * (44 + 100 * random.NextDouble());
                double x = monitor.Left + f * monitor.Width, y = monitor.Top + f * monitor.Height;
                Point edge;
                switch (side)
                {
                    case 0: edge = new Point(x, monitor.Top - beyond); break;
                    case 1: edge = new Point(monitor.Right + beyond, y); break;
                    case 2: edge = new Point(x, monitor.Bottom + beyond); break;
                    default: edge = new Point(monitor.Left - beyond, y); break;
                }
                double depth = random.NextDouble();
                int layer = depth < .50 ? 0 : depth < .85 ? 1 : 2;
                int streamColor = (stream + firstColor) % 4;
                int color = random.NextDouble() < .57 ? (streamColor == 3 ? 2 : streamColor % 2) : (random.Next(5) == 0 ? 2 : random.Next(2));
                var g = new Grain {
                    ToEdge = edge - center, Color = color, Depth = layer,
                    GatherCurl = 1.74 + .76 * bend + (random.NextDouble() - .5) * .55,
                    BurstCurl = .32 * bend + (random.NextDouble() - .5) * 1.04,
                    Start = .002 + .082 * random.NextDouble() + .047 * delay, End = .384 + .112 * random.NextDouble() + .042 * delay,
                    Kick = .461 + .061 * random.NextDouble() + .032 * delay, Escape = .776 + .150 * random.NextDouble() + .053 * delay,
                    Radius = scale * (.62 + .43 * layer + .37 * random.NextDouble()),
                    Tail = .007 + .012 * layer + .008 * random.NextDouble(),
                    Pace = (layer - 1) * .40 + (random.NextDouble() - .5) * .82,
                    TrayEmission = .003 + .12 * random.NextDouble() + .065 * delay, TrayArrival = .387 + .114 * random.NextDouble() + .036 * delay,
                    LeadA = .12 + .28 * random.NextDouble(), LeadB = .64 + .23 * random.NextDouble(),
                    BendA = .32 * bend + (random.NextDouble() - .5) * .55, BendB = .19 * bend + (random.NextDouble() - .5) * .52
                };
                if (hasWindow && i % 3 == 0)
                {
                    g.Shed = .025 + .138 * random.NextDouble() + .036 * delay;
                    g.Accreted = .332 + .065 * random.NextDouble() + .027 * delay;
                    // Each spark starts on the live window's edge at its own emission time.
                    double edgeRatio = Math.Max(2 * Math.Abs(g.ToEdge.X) / window.Width, 2 * Math.Abs(g.ToEdge.Y) / window.Height);
                    if (edgeRatio > 0) g.FromWindow = g.ToEdge * (WindowScale(g.Shed,true) / edgeRatio);
                }
                grains[i] = g;
            }
        }

        // The GPU launch and the compatibility renderer use the same seeds and flight paths.
        internal float[] OpeningGrains()
        {
            var data = new float[grains.Length * 12];
            for (int i = 0; i < grains.Length; i++)
            {
                var g = grains[i]; int at = i * 12;
                data[at] = (float)g.ToEdge.X; data[at+1] = (float)g.ToEdge.Y;
                data[at+2] = (float)g.GatherCurl; data[at+3] = (float)g.Pace;
                data[at+4] = (float)g.Start; data[at+5] = (float)g.End;
                data[at+6] = (float)g.Tail; data[at+7] = (float)g.Radius;
                data[at+8] = g.Color; data[at+9] = g.Depth;
            }
            return data;
        }

        internal static double WindowScale(double t, bool exiting)
        {
            if (exiting) return 1 - Smooth(0, BurstStart, t);
            double u = Unit((t - WindowBirth) / (1 - WindowBirth)), left = 1 - u;
            return 1 - (1 + 4 * u) * left * left * left * left;
        }
        internal static double WindowOpacity(double t, bool exiting)
        {
            return exiting ? 1 - Smooth(.29, BurstStart, t) : Smooth(WindowBirth, .735, t);
        }

        internal void Draw(DrawingContext dc, double t, bool exiting)
        {
            if (t <= 0 || t >= 1) return;
            if (exiting && t < BurstStart)
            {
                if (hasWindow)
                {
                    dc.PushOpacity(.78 * Smooth(.015,.08,t) * (1 - Smooth(.38,BurstStart,t)));
                    DrawField(dc,t,FieldPhase.Shed);
                    dc.Pop();
                }
            }
            else if (exiting || t < WindowBirth) DrawField(dc,t,exiting ? FieldPhase.Burst : FieldPhase.Gather);
            DrawCore(dc, t, exiting);
        }

        void DrawField(DrawingContext dc, double t, FieldPhase phase)
        {
            bool outward = phase == FieldPhase.Burst, shedding = phase == FieldPhase.Shed;
            int stride = shedding ? 3 : 1;
            for (int i = 0; i < grains.Length; i += stride)
            {
                ref readonly var g = ref grains[i];
                double start = shedding ? g.Shed : outward ? g.Kick : g.Start;
                double end = shedding ? g.Accreted : outward ? g.Escape : g.End;
                if (t <= start || t >= end) continue;
                double u = (t - start) / (end - start);
                double tailLength = g.Tail * (1 - Smooth(.90, 1, u));
                Vector extent = shedding ? g.FromWindow : g.ToEdge;
                double curl = outward ? g.BurstCurl : g.GatherCurl;
                Point head = FieldPoint(extent,curl,Travel(u,g.Pace),outward);
                Point tail = FieldPoint(extent,curl,Travel(Math.Max(0,u-tailLength),g.Pace),outward);
                Point neck = g.Depth == 2 ? FieldPoint(extent,curl,Travel(Math.Max(0,u-tailLength*.38),g.Pace),outward) : head;
                DrawGrain(dc, g, head, neck, tail);
            }
        }

        Point FieldPoint(Vector extent, double curl, double u, bool outward)
        {
            double radius = outward ? u * (1.45 - .45 * u) : 1 - u * (.72 + u * (.65 - .37 * u));
            double near = 1 - radius;
            double angle = outward ? curl * near : curl * near * (.32 + .68 * near);
            double c = radius * Math.Cos(angle), s = radius * Math.Sin(angle);
            return new Point(center.X + extent.X * c - extent.Y * s, center.Y + extent.X * s + extent.Y * c);
        }

        internal void DrawTrayExit(DrawingContext dc, double t, Point trayOrigin)
        {
            if (t <= 0 || t >= 1) return;
            if (t < TrayArrived)
            {
                if (lastTrayOrigin != trayOrigin) PrepareTrayPaths(trayOrigin);
                double emission = Smooth(0, .029, t) * (1 - Smooth(.16, .27, t));
                dc.PushOpacity(emission);
                dc.DrawEllipse(corona, null, trayOrigin, 15 * scale, 15 * scale);
                dc.DrawEllipse(bloom, null, trayOrigin, 3.5 * scale, 3.5 * scale);
                dc.Pop();
                for (int i = 0; i < grains.Length; i++)
                {
                    ref readonly var g = ref grains[i];
                    if (t <= g.TrayEmission || t >= g.TrayArrival) continue;
                    double u = (t - g.TrayEmission) / (g.TrayArrival - g.TrayEmission);
                    double tailLength = g.Tail * (1 - Smooth(.90, 1, u));
                    Point head = TrayPoint(g,trayOrigin,u);
                    Point neck = g.Depth == 2 ? TrayPoint(g,trayOrigin,Math.Max(0,u-.42*tailLength)) : head;
                    DrawGrain(dc,g,head,neck,TrayPoint(g,trayOrigin,Math.Max(0,u-tailLength)));
                }
                DrawCore(dc, BurstStart * Smooth(.05, TrayArrived, t), true);
                return;
            }
            double burst = BurstStart + (1 - BurstStart) * (t - TrayArrived) / (1 - TrayArrived);
            Draw(dc, burst, true);
        }

        void PrepareTrayPaths(Point origin)
        {
            lastTrayOrigin = origin;
            Vector span = center - origin, normal = new Vector(-span.Y, span.X);
            for (int i = 0; i < grains.Length; i++)
            {
                var g = grains[i];
                // Control points inside the screen keep curves visible even when the icon is in a corner.
                g.TrayControlA = OnScreen(origin + span * g.LeadA + normal * g.BendA) - origin;
                g.TrayControlB = OnScreen(origin + span * g.LeadB + normal * g.BendB) - origin;
                grains[i] = g;
            }
        }
        Point OnScreen(Point p)
        {
            return new Point(Math.Max(monitor.Left, Math.Min(monitor.Right, p.X)),
                Math.Max(monitor.Top, Math.Min(monitor.Bottom, p.Y)));
        }
        Point TrayPoint(in Grain g, Point origin, double u)
        {
            u = Travel(u,g.Pace);
            double q = 1 - u;
            return origin + 3 * q * q * u * g.TrayControlA + 3 * q * u * u * g.TrayControlB + u * u * u * (center - origin);
        }
        void DrawGrain(DrawingContext dc, in Grain g, Point head, Point neck, Point tail)
        {
            var ink = inks[g.Color, g.Depth];
            if (g.Depth == 0)
            {
                dc.DrawLine(ink.Streak,tail,head);
                return;
            }
            dc.DrawLine(ink.Tail, tail, neck);
            if (g.Depth == 2)
            {
                dc.DrawLine(ink.Streak, neck, head);
                dc.DrawEllipse(ink.Glow, null, head, 4 * g.Radius, 4 * g.Radius);
            }
            dc.DrawEllipse(ink.Light, null, head, g.Radius, g.Radius);
        }

        void DrawCore(DrawingContext dc, double t, bool exiting)
        {
            double charge = exiting ? Smooth(.165, BurstStart, t) : Smooth(.105, .527, t);
            double discharge = exiting ? Smooth(.485, .665, t) : Smooth(.56, .735, t);
            double energy = charge * (1 - discharge);
            if (energy > .001)
            {
                double size = scale * (2 + 5.6 * charge);
                double spark = exiting ? Smooth(.425, .492, t) : Smooth(.505, .57, t);
                dc.PushOpacity(energy);
                dc.DrawEllipse(corona, null, center, 6.8 * size, 6.8 * size);
                dc.DrawEllipse(filament, null, center, 4.3 * size, .52 * size);
                dc.DrawEllipse(horizon, null, center, size, size);
                dc.PushOpacity(spark);
                dc.DrawEllipse(bloom, null, center, 9 * size, .44 * size);
                dc.DrawEllipse(bloom, null, center, 1.5 * size, 1.5 * size);
                dc.Pop();
                dc.Pop();
            }
            double ignition = exiting ? .465 : WindowBirth;
            DrawWave(dc,wave,(t-ignition)/.24,waveRadius,1);
            DrawWave(dc,echo,(t-ignition-.048)/.28,waveRadius*.80,.92);
        }

        void DrawWave(DrawingContext dc, Pen pen, double phase, double reach, double aspect)
        {
            if (phase <= 0 || phase >= 1) return;
            double radius = 7 * scale + reach * (1 - (1 - phase) * (1 - phase));
            dc.PushOpacity((1 - phase) * (1 - phase) * Smooth(0, .1, phase));
            dc.DrawEllipse(null,pen,center,radius,radius*aspect);
            dc.Pop();
        }

        static double Unit(double x) { return Math.Max(0, Math.Min(1, x)); }
        // The fastest bright heads and the slower dust share both endpoints, with positive speed throughout.
        static double Travel(double u,double pace) { return u + pace * u * (1 - u); }
        static double Smooth(double a, double b, double x) { double u = Unit((x - a) / (b - a)); return u * u * (3 - 2 * u); }
        static Color Blend(Color a, Color b, double u)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * u), (byte)(a.G + (b.G - a.G) * u), (byte)(a.B + (b.B - a.B) * u));
        }
        static Color Alpha(Color color, double strength) { return Color.FromArgb((byte)(255 * strength), color.R, color.G, color.B); }
        static Brush Solid(Color c, double opacity)
        {
            var brush = new SolidColorBrush(Alpha(c, opacity)); brush.Freeze(); return brush;
        }
        static Pen Stroke(Color c, double opacity, double width)
        {
            var pen = new Pen(Solid(c, opacity), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze(); return pen;
        }
        static Brush Halo(Color c, double opacity)
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Alpha(c, opacity), 0));
            brush.GradientStops.Add(new GradientStop(Alpha(c, opacity * .7), .17));
            brush.GradientStops.Add(new GradientStop(Alpha(c, opacity * .14), .48));
            brush.GradientStops.Add(new GradientStop(Alpha(c, 0), 1));
            brush.Freeze(); return brush;
        }
        static Brush Horizon()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(36, 28, 43), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(46, 33, 40), .64));
            brush.GradientStops.Add(new GradientStop(amber, .79));
            brush.GradientStops.Add(new GradientStop(Colors.FloralWhite, .88));
            brush.GradientStops.Add(new GradientStop(Alpha(pearl, 0), 1));
            brush.Freeze(); return brush;
        }
    }
}
