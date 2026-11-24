using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AstralSplit
{
    // Only the foreground label represents this text to UI Automation.
    public sealed class TextHalo : TextBlock
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() { return null; }
    }

    public sealed class RowHoverPlate : Border
    {
        public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(nameof(Hovered),
            typeof(bool), typeof(RowHoverPlate), new PropertyMetadata(false, (element, args) => ((RowHoverPlate)element).Paint()));

        public bool Hovered { get { return (bool)GetValue(HoveredProperty); } set { SetValue(HoveredProperty, value); } }

        public RowHoverPlate()
        {
            Opacity = 0;
            DataContextChanged += (s, e) => Snap();
        }

        void Paint() { UiMotion.Fade(this, Hovered ? 1 : 0, Hovered ? 80 : 180); }

        internal void Snap()
        {
            Opacity = Hovered ? 1 : 0;
            BeginAnimation(OpacityProperty, null);
        }
    }

    public sealed class RouteTag : Border
    {
        public static readonly DependencyProperty RoutedProperty = DependencyProperty.Register(nameof(Routed), typeof(bool), typeof(RouteTag));
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RouteTag));
        public static readonly DependencyProperty ActiveFillProperty = DependencyProperty.Register(nameof(ActiveFill), typeof(SolidColorBrush), typeof(RouteTag));
        public static readonly DependencyProperty ActiveTextProperty = DependencyProperty.Register(nameof(ActiveText), typeof(SolidColorBrush), typeof(RouteTag));
        public static readonly DependencyProperty IdleTextProperty = DependencyProperty.Register(nameof(IdleText), typeof(SolidColorBrush), typeof(RouteTag));
        readonly SolidColorBrush fill = new SolidColorBrush(), ink = new SolidColorBrush();
        readonly TextBlock label;
        object previousContext;
        DispatcherOperation rebound;

        public bool Routed { get { return (bool)GetValue(RoutedProperty); } set { SetValue(RoutedProperty, value); } }
        public string Text { get { return (string)GetValue(TextProperty); } set { SetValue(TextProperty, value); } }
        public SolidColorBrush ActiveFill { get { return (SolidColorBrush)GetValue(ActiveFillProperty); } set { SetValue(ActiveFillProperty, value); } }
        public SolidColorBrush ActiveText { get { return (SolidColorBrush)GetValue(ActiveTextProperty); } set { SetValue(ActiveTextProperty, value); } }
        public SolidColorBrush IdleText { get { return (SolidColorBrush)GetValue(IdleTextProperty); } set { SetValue(IdleTextProperty, value); } }

        public RouteTag()
        {
            Background = fill;
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(8, 2, 8, 2);
            Child = label = new TextBlock { FontSize = 11, Foreground = ink };
            DataContextChanged += (s, e) => ResetContext();
            Loaded += (s, e) => ResetContext();
            Unloaded += (s, e) => Snap();
        }

        void ResetContext()
        {
            previousContext = DataContext;
            Snap();
            if (!IsLoaded || rebound != null || Dispatcher.HasShutdownStarted) return;
            rebound = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { rebound = null; Snap(); }));
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (label == null) return;
            if (e.Property == TextProperty)
            {
                label.Text = Text;
                Visibility = String.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (e.Property == RoutedProperty || e.Property == ActiveFillProperty || e.Property == ActiveTextProperty || e.Property == IdleTextProperty)
                Paint(e.Property == RoutedProperty && rebound == null && ReferenceEquals(previousContext, DataContext));
        }

        internal void Snap() { Paint(false); }

        void Paint(bool animate)
        {
            animate = animate && VisualTreeHelper.GetParent(this) is FrameworkElement row && UiMotion.CanAnimate(row);
            int? rate = animate ? DisplayRefresh.For(this) : null;
            Color wash = ActiveFill?.Color ?? Colors.Transparent;
            UiMotion.PaintColor(fill, SolidColorBrush.ColorProperty, Routed ? wash : Color.FromArgb(0, wash.R, wash.G, wash.B), 180, rate, animate);
            UiMotion.PaintColor(ink, SolidColorBrush.ColorProperty, (Routed ? ActiveText : IdleText)?.Color ?? Colors.Transparent, 180, rate, animate);
        }
    }

    internal sealed class CountTransition
    {
        readonly TextBlock current, previous;
        readonly TranslateTransform incoming = new TranslateTransform(), outgoing = new TranslateTransform();
        int? value;
        int revision;

        internal CountTransition(TextBlock current, TextBlock previous)
        {
            this.current = current;
            this.previous = previous;
            current.RenderTransform = incoming;
            previous.RenderTransform = outgoing;
            previous.Opacity = 0;
            current.IsVisibleChanged += (s, e) => { if (!current.IsVisible) Snap(); };
        }

        internal void Show(int? next, string text)
        {
            if (current.Text == text) return;
            int? old = value;
            value = next;
            if (!old.HasValue || !next.HasValue || old == next || SystemParameters.HighContrast || !UiMotion.CanAnimate(current))
            {
                current.Text = text;
                Snap();
                return;
            }

            int direction = next.Value > old.Value ? 1 : -1;
            double alpha = current.Opacity, y = incoming.Y, from = previous.Opacity;
            // On reversal the previous label returns from its sampled position.
            // For a third value keep whichever label is currently more visible.
            bool returning = previous.Text == text;
            double enterY = returning ? outgoing.Y : direction * 4 * (1 - from);
            if (returning || alpha >= from)
            {
                previous.Text = current.Text;
            }
            else
            {
                enterY = y;
                y = outgoing.Y;
                from = alpha;
                alpha = previous.Opacity;
            }
            // SnapshotAndReplace can present the old clock's last sample until
            // the next WPF tick. Seed both labels before exchanging their text.
            StopClocks();
            current.Opacity = from;
            previous.Opacity = alpha;
            incoming.Y = enterY;
            outgoing.Y = y;
            current.Text = text;
            var rate = DisplayRefresh.For(current);
            DoubleAnimationUsingKeyFrames Move(double start, double end)
            {
                var timeline = UiMotion.Ease(start, end, 160, rate);
                timeline.FillBehavior = FillBehavior.HoldEnd;
                return timeline;
            }
            int turn = ++revision;
            var settle = Move(from, 1);
            settle.Completed += (s, e) => { if (turn == revision) Snap(); };
            current.BeginAnimation(UIElement.OpacityProperty, settle);
            incoming.BeginAnimation(TranslateTransform.YProperty, Move(enterY, 0));
            previous.BeginAnimation(UIElement.OpacityProperty, Move(alpha, 0));
            outgoing.BeginAnimation(TranslateTransform.YProperty, Move(y, -direction * 4));
        }

        internal void Snap()
        {
            revision++;
            current.Opacity = 1;
            previous.Opacity = 0;
            incoming.Y = outgoing.Y = 0;
            StopClocks();
            previous.Text = String.Empty;
        }

        void StopClocks()
        {
            current.BeginAnimation(UIElement.OpacityProperty, null);
            previous.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.BeginAnimation(TranslateTransform.YProperty, null);
            outgoing.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    public sealed class SelectedCount : StackPanel
    {
        public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(nameof(Summary),
            typeof(string), typeof(SelectedCount), new PropertyMetadata(null, (element, args) => ((SelectedCount)element).UpdateCount()));
        readonly CountTransition transition;

        public string Summary { get { return (string)GetValue(SummaryProperty); } set { SetValue(SummaryProperty, value); } }

        public SelectedCount()
        {
            Orientation = Orientation.Horizontal;
            Children.Add(new TextBlock { Text = "已选 " });
            var current = new TextBlock();
            var previous = new TextHalo();
            previous.SetResourceReference(StyleProperty, typeof(TextBlock));
            transition = new CountTransition(current, previous);
            Children.Add(new Grid { ClipToBounds = true, Children = { previous, current } });
        }

        void UpdateCount()
        {
            string next = Summary == null ? String.Empty : Summary.Substring(Summary.LastIndexOf(' ') + 1);
            int count;
            transition.Show(Int32.TryParse(next, out count) ? count : (int?)null, next);
        }

        internal void Snap()
        {
            transition.Snap();
        }
    }

    public sealed class AppRowSurface : Grid
    {
        const double RowHeight = 52;
        public static readonly DependencyProperty PendingRemovalProperty = DependencyProperty.Register(nameof(PendingRemoval),
            typeof(bool), typeof(AppRowSurface), new PropertyMetadata(false, (element, args) => ((AppRowSurface)element).PaintPending()));
        Action onCollapsed;
        object paintedContext;
        DispatcherOperation rebound;

        public bool PendingRemoval { get { return (bool)GetValue(PendingRemovalProperty); } set { SetValue(PendingRemovalProperty, value); } }

        public AppRowSurface()
        {
            Height = RowHeight;
            Loaded += (s, e) => ResetBinding();
            Unloaded += (s, e) => ResetBinding();
            DataContextChanged += (s, e) => ResetBinding();
        }

        void ResetBinding()
        {
            var completed = onCollapsed;
            onCollapsed = null;
            paintedContext = DataContext;
            DrawPending(false);
            completed?.Invoke();
            if (!IsLoaded || rebound != null || Dispatcher.HasShutdownStarted) return;
            // A recycled row's bindings can change on either side of DataContextChanged.
            rebound = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                rebound = null;
                if (onCollapsed == null) DrawPending(false);
            }));
        }

        void PaintPending()
        {
            if (!ReferenceEquals(paintedContext, DataContext)) { ResetBinding(); return; }
            onCollapsed = null;
            DrawPending(rebound == null && UiMotion.CanAnimate(this));
        }

        void DrawPending(bool animate)
        {
            double height = Height, opacity = Opacity, target = PendingRemoval ? 0.55 : 1;
            Height = RowHeight;
            Opacity = target;
            IsHitTestVisible = IsEnabled = true;
            var rate = animate ? DisplayRefresh.For(this) : null;
            BeginAnimation(HeightProperty, animate && height != RowHeight ? UiMotion.Ease(height, RowHeight, 160, rate) : null);
            BeginAnimation(OpacityProperty, animate && opacity != target
                ? UiMotion.Ease(opacity, target, PendingRemoval ? 150 : 160, rate) : null);
        }

        internal void Collapse(Action completed)
        {
            double height = Height, opacity = Opacity;
            onCollapsed = completed;
            IsHitTestVisible = IsEnabled = false;
            Height = Opacity = 0;
            if (!UiMotion.CanAnimate(this))
            {
                BeginAnimation(HeightProperty, null);
                BeginAnimation(OpacityProperty, null);
                onCollapsed = null;
                completed();
                return;
            }
            var rate = DisplayRefresh.For(this);
            var collapse = UiMotion.Ease(height, 0, 240, rate, 0.3, 0, 0.1, 1, delay: 40);
            collapse.Completed += (s, e) => {
                if (onCollapsed != completed) return;
                onCollapsed = null;
                completed();
            };
            BeginAnimation(HeightProperty, collapse);
            BeginAnimation(OpacityProperty, UiMotion.Ease(opacity, 0, 120, rate));
        }
    }

    public sealed class ButtonFeedback : Border
    {
        public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(nameof(Hovered), typeof(bool),
            typeof(ButtonFeedback), new PropertyMetadata(false, Changed));
        public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed), typeof(bool),
            typeof(ButtonFeedback), new PropertyMetadata(false, Changed));

        public bool Hovered { get { return (bool)GetValue(HoveredProperty); } set { SetValue(HoveredProperty, value); } }
        public bool Pressed { get { return (bool)GetValue(PressedProperty); } set { SetValue(PressedProperty, value); } }

        public ButtonFeedback() { Opacity = 0; IsHitTestVisible = false; }

        static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            var feedback = (ButtonFeedback)element;
            double current = feedback.Opacity, target = feedback.Pressed ? 1 : feedback.Hovered ? 0.55 : 0;
            if (Equals(feedback.GetAnimationBaseValue(OpacityProperty), target)) return;
            feedback.Opacity = target;
            int duration = e.Property == PressedProperty ? 200 : feedback.Hovered ? 60 : 150;
            feedback.BeginAnimation(OpacityProperty, !feedback.Pressed && UiMotion.CanAnimate(feedback) && current != target
                ? UiMotion.Ease(current, target, duration, DisplayRefresh.For(feedback)) : null);
        }
    }
}
