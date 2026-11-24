using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed class CardShadow : FrameworkElement
    {
        const int Radius = 14, Padding = 30, Corner = Radius + Padding, Middle = 16, Texture = 2 * Corner + Middle;
        static readonly BitmapSource[] tiles = CreateTiles();

        protected override void OnRender(DrawingContext drawing)
        {
            if (ActualWidth < 2 * Radius || ActualHeight < 2 * Radius) return;
            double[] xs = { -Padding, Radius, ActualWidth - Radius, ActualWidth + Padding };
            double[] ys = { -Padding, Radius, ActualHeight - Radius, ActualHeight + Padding };
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    if (row != 1 || column != 1)
                        drawing.DrawImage(tiles[row * 3 + column],
                            new Rect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]));
        }

        static BitmapSource[] CreateTiles()
        {
            // Blur once. Resizing stretches the edge strips and leaves the four corners intact.
            var near = ShadowLayer(1, 1);
            var far = ShadowLayer(8, 9);
            var pixels = new byte[Texture * Texture * 4];
            for (int i = 0; i < near.Length; i++)
                pixels[4 * i + 3] = (byte)Math.Round(255 * (1 - (1 - 0.04 * near[i]) * (1 - 0.06 * far[i])));
            var bitmap = BitmapSource.Create(Texture, Texture, 96, 96, PixelFormats.Pbgra32, null, pixels, Texture * 4);
            bitmap.Freeze();
            var result = new BitmapSource[9];
            int[] edges = { 0, Corner, Corner + Middle, Texture };
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    if (row == 1 && col == 1) continue;
                    var segment = new CroppedBitmap(bitmap,
                        new Int32Rect(edges[col], edges[row], edges[col + 1] - edges[col], edges[row + 1] - edges[row]));
                    segment.Freeze();
                    result[row * 3 + col] = segment;
                }
            return result;
        }

        static double[] ShadowLayer(int offset, double sigma)
        {
            int reach = (int)Math.Ceiling(sigma * 2.5);
            var weights = new double[reach * 2 + 1];
            double total = 0;
            for (int i = -reach; i <= reach; i++)
                total += weights[i + reach] = Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;

            var mask = new double[Texture * Texture];
            var horizontal = new double[mask.Length];
            var result = new double[mask.Length];
            for (int y = 0; y < Texture; y++)
                for (int x = 0; x < Texture; x++)
                {
                    double dx = Math.Max(0, Math.Abs(x + 0.5 - Texture / 2.0) - Middle / 2.0);
                    double dy = Math.Max(0, Math.Abs(y + 0.5 - offset - Texture / 2.0) - Middle / 2.0);
                    mask[y * Texture + x] = UiMotion.Unit(Radius + 0.5 - Math.Sqrt(dx * dx + dy * dy));
                }
            for (int y = 0; y < Texture; y++)
                for (int x = 0; x < Texture; x++)
                    for (int k = Math.Max(-reach, -x); k <= Math.Min(reach, Texture - 1 - x); k++)
                        horizontal[y * Texture + x] += weights[k + reach] * mask[y * Texture + x + k];
            for (int y = 0; y < Texture; y++)
                for (int x = 0; x < Texture; x++)
                    for (int k = Math.Max(-reach, -y); k <= Math.Min(reach, Texture - 1 - y); k++)
                        result[y * Texture + x] += weights[k + reach] * horizontal[(y + k) * Texture + x];
            return result;
        }
    }

    public sealed class BranchGlyph : FrameworkElement
    {
        public static readonly DependencyProperty SpreadProperty = DependencyProperty.Register(nameof(Spread),
            typeof(double), typeof(BranchGlyph), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke),
            typeof(Brush), typeof(BranchGlyph), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Spread { get { return (double)GetValue(SpreadProperty); } set { SetValue(SpreadProperty, value); } }
        public Brush Stroke { get { return (Brush)GetValue(StrokeProperty); } set { SetValue(StrokeProperty, value); } }

        internal static PathFigure UpperArm(double spread)
        {
            double end = 12 - 6 * spread;
            var figure = new PathFigure { StartPoint = new Point(3, 12), IsFilled = false };
            figure.Segments.Add(new LineSegment(new Point(7, 12), true));
            figure.Segments.Add(new BezierSegment(new Point(12, 12), new Point(12, end), new Point(17, end), true));
            figure.Segments.Add(new LineSegment(new Point(21, end), true));
            return figure;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var pen = new Pen(Stroke, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var upper = UpperArm(Spread);
            double y = 12 - 6 * Spread;
            var head = new PathFigure { StartPoint = new Point(18, y - 3), IsFilled = false };
            head.Segments.Add(new LineSegment(new Point(21, y), true));
            head.Segments.Add(new LineSegment(new Point(18, y + 3), true));
            var path = new PathGeometry(new[] { upper, head });
            drawing.DrawGeometry(null, pen, path);
            if (Spread <= 0.001) return;
            var lower = new PathGeometry(new[] { upper.Clone(), head.Clone() }) { Transform = new ScaleTransform(1, -1, 12, 12) };
            drawing.DrawGeometry(null, pen, lower);
        }
    }

    public sealed class SkeletonRows : FrameworkElement
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill),
            typeof(Brush), typeof(SkeletonRows), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
        readonly TranslateTransform sweep = new TranslateTransform(-1, 0);
        readonly ScaleTransform settle = new ScaleTransform(1, 1);
        readonly LinearGradientBrush highlight = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,0) };
        bool sweeping;

        public Brush Fill { get { return (Brush)GetValue(FillProperty); } set { SetValue(FillProperty, value); } }

        public SkeletonRows()
        {
            highlight.GradientStops.Add(new GradientStop(Colors.Transparent, 0.22));
            highlight.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
            highlight.GradientStops.Add(new GradientStop(Colors.Transparent, 0.78));
            highlight.RelativeTransform = sweep;
        }

        internal void SetSweep(bool animate, Color color)
        {
            highlight.GradientStops[1].Color = color;
            if (sweeping == animate) return;
            sweeping = animate;
            var gathering = animate ? new DoubleAnimation(2.8, 1, TimeSpan.FromMilliseconds(680)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            } : null;
            settle.BeginAnimation(ScaleTransform.ScaleYProperty, animate ? UiMotion.AtRefresh(gathering, DisplayRefresh.For(this)) : null);
            sweep.BeginAnimation(TranslateTransform.XProperty, animate
                ? UiMotion.AtRefresh(new DoubleAnimation(-0.82, 0.82, TimeSpan.FromMilliseconds(1200)) {
                    RepeatBehavior = RepeatBehavior.Forever
                }, DisplayRefresh.For(this)) : null);
        }

        protected override void OnRender(DrawingContext drawing)
        {
            double width = ActualWidth;
            if (width < 260) return;
            var mask = new GeometryGroup();
            for (int i = 0; i < 5; i++)
            {
                double y = 52 * i;
                mask.Children.Add(new RectangleGeometry(new Rect(8, y + 10, 32, 32), 8, 8));
                mask.Children.Add(new RectangleGeometry(new Rect(48, y + 14, width * (i % 2 == 0 ? 0.27 : 0.22), 9), 4, 4));
                mask.Children.Add(new RectangleGeometry(new Rect(48, y + 30, width * (i % 2 == 0 ? 0.16 : 0.18), 7), 4, 4));
                mask.Children.Add(new RectangleGeometry(new Rect(width - 48, y + 15, 38, 22), 11, 11));
            }
            mask.Freeze();
            drawing.DrawGeometry(Fill, null, mask);
            drawing.PushClip(mask);
            drawing.DrawRectangle(highlight, null, new Rect(RenderSize));
            drawing.Pop();
            // All five rows share one composited transform. The fixed seeds keep particles stable on resize.
            for (int row = 0; row < 5; row++)
            {
                var points = new GeometryGroup();
                var rng = new Random(171 + row);
                double start = 50 + width * (row % 2 == 0 ? .27 : .22);
                double span = Math.Max(0, width - 66 - start);
                for (int dot = 0; dot < 15; dot++)
                {
                    double radius = .5 + .7 * rng.NextDouble();
                    points.Children.Add(new EllipseGeometry(new Point(start + span * rng.NextDouble(), (rng.NextDouble() - .5) * 9), radius, radius));
                }
                points.Freeze();
                drawing.PushTransform(new TranslateTransform(0, 26 + 52 * row));
                drawing.PushTransform(settle);
                drawing.DrawGeometry(Fill, null, points);
                drawing.Pop();
                drawing.Pop();
            }
        }
    }
}
