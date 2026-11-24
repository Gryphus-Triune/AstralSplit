using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        readonly SpringProgress segmentProgress = new SpringProgress();
        readonly Stopwatch presentationTime = Stopwatch.StartNew();
        readonly Dictionary<AppRow, RowRemoval> rowRemovals = new Dictionary<AppRow, RowRemoval>();
        readonly SolidColorBrush runningText = new SolidColorBrush(), selectedText = new SolidColorBrush();
        readonly SolidColorBrush statusFill = new SolidColorBrush(), networkFill = new SolidColorBrush();
        readonly ScaleTransform searchRingScale = new ScaleTransform(1, 1);
        DispatcherTimer removalTimer, scrollTimer;
        long lastScrollActivity;
        Grid noticeReveal, captionButtons;
        Border searchRing, busyPressShield;
        DispatcherOperation removalPass;
        WheelScrollViewer listScroller;
        ScrollBar scrollBar;
        FadingText statusTitle, statusDetail;
        bool listUpdatePending, noticeWanted;
        int noticeRevision, menuRevision;
        string noticeMessage;

        void InitializePresentation()
        {
            noticeReveal = Find<Grid>("NoticeReveal");
            statusTitle = new FadingText(mode, 0);
            statusDetail = new FadingText(detail, 30);
            statusGlyph.Fill = statusFill;
            dot.Fill = networkFill;
            all.Foreground = runningText;
            selected.Foreground = selectedText;
            UpdateSegmentText(false);
            segmentProgress.ValueChanged += DrawFilterIndicator;
            all.SizeChanged += (s, e) => { if (e.WidthChanged) DrawFilterIndicator(); };

            searchRing = Find<Border>("SearchRing");
            searchRing.RenderTransform = searchRingScale;
            Find<Border>("SearchField").IsKeyboardFocusWithinChanged += (s, e) => PaintSearchRing((bool)e.NewValue);
            captionButtons = Find<Grid>("CaptionButtons");
            captionButtons.MouseEnter += (s, e) => ShowCaptionMarks(true);
            captionButtons.MouseLeave += (s, e) => ShowCaptionMarks(false);
            busyPressShield = Find<Border>("BusyPressShield");
            busyPressShield.PreviewMouseLeftButtonDown += (s, e) => { master.RejectBusyPress(); e.Handled = true; };
            InitializeTransitions();
            InitializeArtwork();
            InitializeListAppearance();
            InitializeTheme();
            InitializeStellar();

            windowSort.StaysOpenOnClick = memorySort.StaysOpenOnClick = true;
            sort.ContextMenu.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
            InitializeSortMenu();
            removalTimer = new DispatcherTimer(DispatcherPriority.Normal);
            removalTimer.Tick += (s, e) => RunRowRemovals();
            applications.ItemContainerGenerator.StatusChanged += (s, e) => ScheduleRowRemovals();
            scrollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            scrollTimer.Tick += (s, e) => {
                long remaining = 1000 - (presentationTime.ElapsedMilliseconds - lastScrollActivity);
                if (remaining > 0)
                {
                    scrollTimer.Interval = TimeSpan.FromMilliseconds(remaining);
                    return;
                }
                scrollTimer.Stop();
                if (scrollBar != null && !scrollBar.IsMouseOver && !scrollBar.IsMouseCaptureWithin)
                    SetOpacity(scrollBar, 0, 300);
            };
            applications.Loaded += (s, e) => ConnectScrollBar();

            SystemParameters.StaticPropertyChanged += PresentationPreferenceChanged;
            Window.IsVisibleChanged += (s, e) => ResetPresentation();
            Window.StateChanged += (s, e) => { if (!ProjectingMinimizedWindow) ResetPresentation(); };
            Window.Closed += (s, e) => {
                SystemParameters.StaticPropertyChanged -= PresentationPreferenceChanged;
                CancelSortLayout();
                listScroller?.StopWheelScroll();
                removalTimer.Stop();
                scrollTimer.Stop();
                CancelRowRemovals();
            };
        }

        void PositionFilterIndicator(bool animate)
        {
            bool motion = animate && all.ActualWidth > 0 && UiMotion.CanAnimate(filterIndicator);
            segmentProgress.MoveTo(selectedOnly ? 1 : 0, motion, 22, motion ? DisplayRefresh.For(filterIndicator) : null);
            DrawFilterIndicator();
            UpdateSegmentText(motion);
        }

        void DrawFilterIndicator()
        {
            double progress = segmentProgress.Value;
            filterSlide.X = all.ActualWidth * progress;
            // Blend out the flat indicator as it arrives beneath the lifted
            // glass. The release spring brings it back at the same position.
            double destination = selectedOnly ? 1 : 0;
            double arrival = UiMotion.Unit(1 - 2 * Math.Abs(destination - progress));
            double lift = selectedOnly ? selectedGlassLift : runningGlassLift;
            filterIndicator.Opacity = 1 - lift * arrival * arrival * (3 - 2 * arrival);
        }

        void UpdateSegmentText(bool animate)
        {
            var ink = Theme("SelectionInk");
            var secondary = Theme("Subtle");
            var rate = animate ? DisplayRefresh.For(filterIndicator) : null;
            SetColor(runningText, selectedOnly ? secondary : ink, 150, rate, animate);
            SetColor(selectedText, selectedOnly ? ink : secondary, 150, rate, animate);
        }

        sealed class RowRemoval
        {
            internal readonly long Deadline;
            internal ListBoxItem Watched;
            internal bool DeadlineReached, Collapsing;

            internal RowRemoval(long deadline) { Deadline = deadline; }
        }

        void HoldRow(AppRow row)
        {
            row.PendingRemoval = true;
            rowRemovals[row] = new RowRemoval(presentationTime.ElapsedMilliseconds + 600);
            ArmRowRemovalTimer();
        }

        void ArmRowRemovalTimer()
        {
            removalTimer.Stop();
            long now = presentationTime.ElapsedMilliseconds;
            long due = rowRemovals.Values.Where(item => !item.DeadlineReached)
                .Select(item => item.Deadline).DefaultIfEmpty(-1).Min();
            if (due < 0) return;
            removalTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, due - now));
            removalTimer.Start();
        }

        void CancelRowRemoval(AppRow row)
        {
            if (!rowRemovals.TryGetValue(row, out RowRemoval removal)) return;
            StopWatching(removal);
            rowRemovals.Remove(row);
            row.PendingRemoval = false;
            ArmRowRemovalTimer();
        }

        void CancelRowRemovals()
        {
            foreach (var item in rowRemovals)
            {
                StopWatching(item.Value);
                item.Key.PendingRemoval = false;
            }
            rowRemovals.Clear();
            removalTimer.Stop();
            removalPass?.Abort();
            removalPass = null;
        }

        void StopWatching(RowRemoval removal)
        {
            if (removal.Watched == null) return;
            removal.Watched.MouseLeave -= RowPointerLeft;
            removal.Watched.Unloaded -= RowUnloaded;
            removal.Watched.IsMouseCaptureWithinChanged -= RowCaptureChanged;
            removal.Watched = null;
        }

        void RowPointerLeft(object sender, System.Windows.Input.MouseEventArgs e) { ScheduleRowRemovals(); }
        void RowUnloaded(object sender, RoutedEventArgs e) { ScheduleRowRemovals(); }
        void RowCaptureChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) ScheduleRowRemovals(); }

        void ScheduleRowRemovals()
        {
            if (rowRemovals.Count == 0 || removalPass != null || Window.Dispatcher.HasShutdownStarted) return;
            removalPass = Window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                removalPass = null;
                RunRowRemovals();
            }));
        }

        void RunRowRemovals()
        {
            long now = presentationTime.ElapsedMilliseconds;
            removalTimer.Stop();
            foreach (var item in rowRemovals.ToArray())
            {
                var removal = item.Value;
                if (removal.Collapsing || now < removal.Deadline) continue;
                removal.DeadlineReached = true;
                if (listScroller?.IsScrollAnimating == true || scrollBar?.IsMouseCaptureWithin == true) continue;
                var row = item.Key;
                var container = applications.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
                if (container?.IsMouseOver == true || container?.IsMouseCaptureWithin == true)
                {
                    if (removal.Watched != container)
                    {
                        StopWatching(removal);
                        removal.Watched = container;
                        container.MouseLeave += RowPointerLeft;
                        container.Unloaded += RowUnloaded;
                        container.IsMouseCaptureWithinChanged += RowCaptureChanged;
                    }
                    continue;
                }
                StopWatching(removal);
                var surface = container == null ? null : Descendants<AppRowSurface>(container).FirstOrDefault();
                if (surface == null || !UiMotion.CanAnimate(surface))
                {
                    FinishRowRemoval(row, removal);
                    continue;
                }
                removal.Collapsing = true;
                surface.Collapse(() => Window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => FinishRowRemoval(row, removal))));
            }
            ArmRowRemovalTimer();
        }

        void FinishRowRemoval(AppRow row, RowRemoval removal)
        {
            if (!rowRemovals.TryGetValue(row, out RowRemoval current) || current != removal) return;
            if (!row.Selected) visibleRows.Remove(row);
            CancelRowRemoval(row);
            UpdateListSummary();
        }

        void ApplyDeferredListUpdates()
        {
            if (listUpdatePending && sortLayout == null && movingSortRows.Count == 0 && (visibleRows.Count == 0 || !applications.IsMouseOver) && listScroller?.IsScrollAnimating != true &&
                !Descendants<AnimatedSwitch>(applications).Any(control => control.IsMoving))
                RefreshView();
        }

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        void PaintSearchRing(bool focused)
        {
            bool motion = UiMotion.CanAnimate(searchRing);
            var rate = motion ? DisplayRefresh.For(searchRing) : null;
            double scale = searchRingScale.ScaleX;
            double target = focused ? 1 : 1.02;
            searchRingScale.ScaleX = searchRingScale.ScaleY = target;
            var expand = motion && focused ? UiMotion.Ease(scale, target, 150, rate) : null;
            searchRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            searchRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            SetOpacity(searchRing, focused ? 1 : 0, focused ? 150 : 100);
        }

        void ShowCaptionMarks(bool hovered)
        {
            bool active=Window.IsActive || !Window.IsLoaded || contrastTheme;
            bool darkMoon=eclipse && !contrastTheme && !hovered;
            if(moonButtons!=null)foreach(var button in moonButtons){
                var face=button.Template?.FindName("CaptionPaint",button) as FrameworkElement;
                var ring=button.Template?.FindName("CaptionEclipse",button) as FrameworkElement;
                if(face!=null)SetOpacity(face,active && !darkMoon?1:0,190);
                if(ring!=null)SetOpacity(ring,darkMoon?(active?1:.32):0,210);
            }
            double moon=hovered || darkMoon?0:active?1:.45;
            SetOpacity(Find<FrameworkElement>("CloseMoon"),moon,150);
            SetOpacity(Find<FrameworkElement>("MinimizeMoon"),moon,150);
            SetOpacity(Find<FrameworkElement>("ZoomMoon"),moon,150);
            SetOpacity(Find<FrameworkElement>("CloseMark"), hovered ? 1 : 0, 100);
            SetOpacity(Find<FrameworkElement>("MinimizeMark"), hovered ? 1 : 0, 100);
            SetOpacity(Find<FrameworkElement>("ZoomMark"), hovered ? 1 : 0, 100);
        }

        void ResetSortMenu()
        {
            menuRevision++;
            sort.ContextMenu.BeginAnimation(UIElement.OpacityProperty, null);
            sort.ContextMenu.Opacity = 1;
        }

        void OpenSortMenu(bool fromClick = false)
        {
            var menu = sort.ContextMenu;
            var released = fromClick ? (sort.Template?.FindName("HeldGlass", sort) as HeldButtonGlass)?.TakeRelease() : null;
            ResetSortMenu();
            if (menu.IsOpen) return;
            sortMenuRelease = !contrastTheme ? released : null;
            if (sortMenuRelease.HasValue) menu.Opacity = 0;
            menu.IsOpen = true;
        }

        void CloseSortMenu(int duration = 120)
        {
            var menu = sort.ContextMenu;
            if (!menu.IsOpen) return;
            if (!UiMotion.CanAnimate(menu)) { menu.IsOpen = false; return; }
            int revision = ++menuRevision;
            var rate = DisplayRefresh.For(sort);
            AnimationTimeline fade = duration == 120 ? (AnimationTimeline)UiMotion.Linear(menu.Opacity, 0, 120, rate)
                : UiMotion.Ease(menu.Opacity, 0, duration, rate);
            fade.Completed += (s, e) => { if (revision == menuRevision) menu.IsOpen = false; };
            menu.Opacity = 0;
            menu.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void ConnectScrollBar()
        {
            if (scrollBar != null) return;
            var viewer = Descendants<WheelScrollViewer>(applications).FirstOrDefault();
            if (viewer == null) return;
            listScroller = viewer;
            viewer.ApplyTemplate();
            scrollBar = viewer.Template.FindName("PART_VerticalScrollBar", viewer) as ScrollBar;
            if (scrollBar == null) return;
            viewer.ScrollActivity += RevealScrollBar;
            viewer.ScrollActivity += StopSortRows;
            viewer.PositionChanged += UpdateListEdges;
            viewer.MotionCompleted += UpdateListEdges;
            viewer.MotionCompleted += ScheduleRowRemovals;
            viewer.ScrollChanged += (s, e) => { UpdateListEdges(); if (e.VerticalChange != 0) RevealScrollBar(); };
            UpdateListEdges();
            scrollBar.MouseEnter += (s, e) => { WidenGrip(true); RevealScrollBar(); };
            scrollBar.MouseLeave += (s, e) => { WidenGrip(false); ScrollIdle(); };
            scrollBar.IsMouseCaptureWithinChanged += (s, e) => {
                if ((bool)e.NewValue) RevealScrollBar();
                else { ScrollIdle(); ScheduleRowRemovals(); }
            };
        }

        void ScrollIdle()
        {
            lastScrollActivity = presentationTime.ElapsedMilliseconds;
            if (!scrollTimer.IsEnabled)
            {
                scrollTimer.Interval = TimeSpan.FromSeconds(1);
                scrollTimer.Start();
            }
        }

        void RevealScrollBar()
        {
            SetOpacity(scrollBar, 1, 100);
            ScrollIdle();
        }

        void WidenGrip(bool wide)
        {
            (scrollBar.Track?.Thumb as LiquidScrollThumb)?.SetHovered(wide);
        }

        Color Theme(string key) { return ((SolidColorBrush)Window.Resources[key]).Color; }

        void ShowStatus(string title, string explanation, string color)
        {
            statusTitle.Show(title);
            statusDetail.Show(explanation);
            PaintArtwork(color, true);
        }

        static void SetColor(SolidColorBrush brush, Color color, double milliseconds, int? rate, bool animate)
        {
            if (animate && Equals(brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty), color)) return;
            var from = brush.Color;
            brush.Color = color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animate
                ? UiMotion.AtRefresh(new ColorAnimation(from, color, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop }, rate)
                : null);
        }

        void ShowNotice(bool visible, string text, bool animate = true)
        {
            if (visible == noticeWanted && text == noticeMessage && animate) return;
            int revision = ++noticeRevision;
            double oldHeight = noticeReveal.ActualHeight, oldOpacity = noticeReveal.Opacity;
            noticeWanted = visible;
            noticeMessage = text;
            if (visible)
            {
                noticeText.Text = text;
                noticeReveal.Visibility = Visibility.Visible;
            }
            bool motion = animate && UiMotion.CanAnimate(windowBody) && windowBody.ActualWidth > 0;
            if (!motion || (!visible && oldHeight == 0))
            {
                FinishNotice(visible);
                return;
            }
            notice.Measure(new Size(windowBody.ActualWidth, Double.PositiveInfinity));
            double nextHeight = visible ? notice.DesiredSize.Height : 0;
            int time = visible ? 220 : 160;
            var rate = DisplayRefresh.For(noticeReveal);
            var resize = UiMotion.Ease(oldHeight, nextHeight, time, rate);
            resize.Completed += (s, e) => { if (noticeRevision == revision) FinishNotice(visible); };
            noticeReveal.Height = nextHeight;
            double nextOpacity = visible ? 1 : 0;
            noticeReveal.Opacity = nextOpacity;
            noticeReveal.BeginAnimation(FrameworkElement.HeightProperty, resize);
            noticeReveal.BeginAnimation(UIElement.OpacityProperty, UiMotion.Ease(oldOpacity, nextOpacity, time, rate));
        }

        void FinishNotice(bool visible)
        {
            noticeReveal.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            noticeReveal.BeginAnimation(FrameworkElement.HeightProperty, null);
            noticeReveal.BeginAnimation(UIElement.OpacityProperty, null);
            noticeReveal.Height = Double.NaN;
            noticeReveal.Opacity = visible ? 1 : 0;
            if (!visible) noticeText.Text = noticeMessage ?? "";
        }

        static void SetOpacity(FrameworkElement element, double target, int time)
        {
            double from = element.Opacity;
            if (Equals(element.GetAnimationBaseValue(UIElement.OpacityProperty), target)) return;
            element.Opacity = target;
            element.BeginAnimation(UIElement.OpacityProperty, from != target && UiMotion.CanAnimate(element)
                ? UiMotion.Linear(from, target, time, DisplayRefresh.For(element)) : null);
        }

        void PresentationPreferenceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation) || e.PropertyName == nameof(SystemParameters.HighContrast)) {
                if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) taskbarSweep.Stop();
                (tray?.ContextMenuStrip as GlassTrayMenu)?.RefreshOrbit();
                ResetPresentation();
            }
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) QueueThemeUpdate();
        }

        void ResetPresentation()
        {
            ResetIdleTransitions();
            if (SystemParameters.HighContrast || !UiMotion.CanAnimate(surface)) sky?.Milestone.Settle();
            listScroller?.StopWheelScroll(!SystemParameters.ClientAreaAnimation);
            PositionFilterIndicator(false);
            statusTitle.Snap();
            statusDetail.Snap();
            networkFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            PaintArtwork(artworkState ?? "StatusOff", false);
            SnapListLoading();
            UpdateLoadingMotion();
            ShowNotice(noticeWanted, noticeMessage, false);
            if (scrollBar != null) {
                scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
                if (!UiMotion.CanAnimate(surface)) (scrollBar.Track?.Thumb as LiquidScrollThumb)?.SnapMotion();
            }
            if (SystemParameters.HighContrast || !UiMotion.CanAnimate(surface))
            {
                counter.Snap();
                foreach (var count in Descendants<SelectedCount>(selected)) count.Snap();
            }
            if (!UiMotion.CanAnimate(surface))
            {
                StopSortRows();
                ApplySortChoice(sortByMemory, false);
                if (sort.ContextMenu.IsOpen) sort.ContextMenu.IsOpen = false;
                DropThemeGhost();
                SnapSearchClear();
                foreach (var badge in Descendants<RouteTag>(applications)) badge.Snap();
                foreach (var plate in Descendants<RowHoverPlate>(applications)) plate.Snap();
                foreach (var item in rowRemovals.Where(item => item.Value.Collapsing).ToArray())
                    FinishRowRemoval(item.Key, item.Value);
            }
        }

        sealed class FadingText
        {
            readonly TextBlock text;
            readonly int delay;
            readonly TranslateTransform entrance = new TranslateTransform();
            string desired;
            int revision;

            internal FadingText(TextBlock text, int delay)
            {
                this.text = text;
                this.delay = delay;
                desired = text.Text;
                text.RenderTransform = entrance;
            }

            internal void Show(string value)
            {
                if (desired == value) return;
                desired = value;
                if (!UiMotion.CanAnimate(text)) { Snap(); return; }
                int current = ++revision;
                var rate = DisplayRefresh.For(text);
                var fade = UiMotion.Ease(text.Opacity, 0, 100, rate, 0.42, 0, 1, 1, delay);
                fade.Completed += (s, e) => {
                    if (current != revision) return;
                    text.Text = value;
                    text.Opacity = 1;
                    text.BeginAnimation(UIElement.OpacityProperty, UiMotion.Ease(0, 1, 180, rate));
                    entrance.BeginAnimation(TranslateTransform.YProperty, UiMotion.Ease(4, 0, 180, rate));
                };
                text.Opacity = 0;
                text.BeginAnimation(UIElement.OpacityProperty, fade);
            }

            internal void Snap()
            {
                revision++;
                text.Text = desired;
                text.Opacity = 1;
                text.BeginAnimation(UIElement.OpacityProperty, null);
                entrance.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
    }

    public sealed class KeyboardFocusRing : Border
    {
        public KeyboardFocusRing()
        {
            Loaded += (s, e) => {
                if (!UiMotion.CanAnimate(this)) return;
                var scale = new ScaleTransform(1, 1);
                RenderTransform = scale;
                RenderTransformOrigin = new Point(0.5, 0.5);
                var rate = DisplayRefresh.For(this);
                double width = Math.Max(24, ActualWidth), height = Math.Max(24, ActualHeight);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, UiMotion.Ease(1 + 8 / width, 1, 150, rate));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, UiMotion.Ease(1 + 8 / height, 1, 150, rate));
                BeginAnimation(OpacityProperty, UiMotion.Ease(0, 1, 150, rate));
            };
        }
    }
}
