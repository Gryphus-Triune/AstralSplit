using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        readonly RotateTransform refreshSpin = new RotateTransform();
        readonly ScaleTransform clearScale = new ScaleTransform(1, 1);
        readonly TranslateTransform orbitShift = new TranslateTransform();
        readonly RotateTransform telescopeTilt = new RotateTransform(0, 28, 30);
        FrameworkElement orbitArt, telescopeArt;
        bool scanningSky;
        SkeletonRows listLoading;
        FrameworkElement topFade, bottomFade, scrollDivider;
        TextBlock emptyHint;
        Button clearSearch;
        bool appsRead, skeletonWanted, refreshFeedback, spinRunning, spinAgain;
        int loadingRevision, spinRevision;

        void InitializeListAppearance()
        {
            Find<FrameworkElement>("RefreshGlyph").RenderTransform = refreshSpin;
            clearSearch = Find<Button>("ClearSearch");
            clearSearch.RenderTransform = clearScale;
            listLoading = Find<SkeletonRows>("ListLoading");
            emptyHint = Find<TextBlock>("EmptyHint");
            orbitArt = Find<FrameworkElement>("OrbitArt");
            telescopeArt = Find<FrameworkElement>("TelescopeArt");
            orbitArt.RenderTransform = orbitShift;
            Find<FrameworkElement>("TelescopeTube").RenderTransform = telescopeTilt;
            var viewport = Find<Grid>("ListViewport");
            viewport.PreviewMouseMove += (s, e) => {
                if (!orbitArt.IsVisible || !AnimateLoading) return;
                Point p = e.GetPosition(viewport);
                orbitShift.X = 5 * (UiMotion.Unit(p.X / Math.Max(1, viewport.ActualWidth)) - .5);
                orbitShift.Y = 5 * (UiMotion.Unit(p.Y / Math.Max(1, viewport.ActualHeight)) - .5);
            };
            viewport.MouseLeave += (s,e) => { orbitShift.X = orbitShift.Y = 0; };
            telescopeArt.IsVisibleChanged += (s,e) => UpdateEmptyMotion();
            topFade = Find<FrameworkElement>("ListFadeTop");
            bottomFade = Find<FrameworkElement>("ListFadeBottom");
            scrollDivider = Find<FrameworkElement>("ScrollDivider");
        }

        void ShowSearchClear(bool visible)
        {
            if ((clearSearch.Visibility == Visibility.Visible) == visible) return;
            clearSearch.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible || !UiMotion.CanAnimate(clearSearch)) { SnapSearchClear(); return; }
            clearSearch.Opacity = 0;
            UiMotion.Fade(clearSearch, 1, 120);
            var motion = UiMotion.Ease(0.8, 1, 120, DisplayRefresh.For(clearSearch));
            clearScale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
            clearScale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        }

        void SnapSearchClear()
        {
            clearSearch.Opacity = 1;
            clearSearch.BeginAnimation(UIElement.OpacityProperty, null);
            clearScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            clearScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        void UpdateListEdges()
        {
            if (listScroller == null) return;
            double offset = listScroller.DisplayedOffset;
            double first = UiMotion.Unit(offset / 8), last = UiMotion.Unit((listScroller.ScrollableHeight - offset) / 8);
            scrollDivider.Opacity = first;
            topFade.Opacity = contrastTheme ? 0 : first;
            bottomFade.Opacity = contrastTheme ? 0 : last;
        }

        void ShowListLoading(bool wanted)
        {
            if (wanted == skeletonWanted) return;
            skeletonWanted = wanted;
            int revision = ++loadingRevision;
            if (wanted)
            {
                SnapListLoading();
            }
            else
            {
                UiMotion.Fade(listLoading, 0, 120, () => {
                    if (loadingRevision != revision) return;
                    listLoading.Visibility = Visibility.Collapsed;
                    UpdateLoadingMotion();
                });
                UiMotion.Fade(applications, 1, 180);
            }
            UpdateLoadingMotion();
        }

        void SnapListLoading()
        {
            loadingRevision++;
            listLoading.Visibility = skeletonWanted ? Visibility.Visible : Visibility.Collapsed;
            listLoading.Opacity = skeletonWanted ? 1 : 0;
            applications.Opacity = skeletonWanted ? 0 : 1;
            listLoading.BeginAnimation(UIElement.OpacityProperty, null);
            applications.BeginAnimation(UIElement.OpacityProperty, null);
        }

        bool AnimateLoading { get { return !contrastTheme && CanAnimateWindowContent(refresh); } }

        void ShowEmptyArt(bool searching)
        {
            orbitArt.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            telescopeArt.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateEmptyMotion()
        {
            bool active = AnimateLoading && telescopeArt.IsVisible;
            if (scanningSky == active) return;
            scanningSky = active;
            var sweep = active ? new DoubleAnimation(-7, 7, TimeSpan.FromSeconds(2.5)) {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            } : null;
            telescopeTilt.BeginAnimation(RotateTransform.AngleProperty, active ? UiMotion.AtRefresh(sweep, DisplayRefresh.For(telescopeArt)) : null);
        }

        void SetRefreshFeedback(bool pending)
        {
            if (pending && spinRunning) spinAgain = true;
            refreshFeedback = pending;
            UpdateLoadingMotion();
        }

        void UpdateLoadingMotion()
        {
            bool animate = AnimateLoading;
            UpdateEmptyMotion();
            listLoading.SetSweep(animate && listLoading.IsVisible, Theme("SkeletonGlint"));
            if (animate)
            {
                if (refreshFeedback && !spinRunning) SpinRefresh();
            }
            else
            {
                spinAgain = spinRunning = false;
                spinRevision++;
                refreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        void SpinRefresh()
        {
            int turn = ++spinRevision;
            spinRunning = true;
            var spin = UiMotion.Ease(0, 360, 500, DisplayRefresh.For(refresh), 0.42, 0, 0.58, 1);
            spin.Completed += (s, e) => {
                if (spinRevision != turn || closed) return;
                spinRunning = false;
                if (AnimateLoading && (refreshFeedback || spinAgain))
                {
                    spinAgain = false;
                    SpinRefresh();
                }
            };
            refreshSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }
}
