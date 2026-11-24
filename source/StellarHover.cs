using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AstralSplit
{
    public sealed partial class StarfieldLayer
    {
        internal event Action StarMapChanged;

        internal (string Path, string Name, string Status, Point Position)? ApplicationAt(Point point, string previousPath)
        {
            if (disposed || observing || highContrast || !IsVisible || !viewport.Contains(point)) return null;

            const double enterRadius = 14, leaveRadius = 18;
            ChartMarker nearest = null, held = null;
            double nearestDistance = enterRadius * enterRadius, heldDistance = 0;
            foreach (var marker in markerLayout.Concat(surveyExtras))
            {
                var screen=MapToScreen(marker.Position);
                if (!viewport.Contains(screen) || (marker.Selected && IsFlyingIn(marker.Path))) continue;
                double distance = (screen - point).LengthSquared;
                if (distance <= leaveRadius * leaveRadius && String.Equals(marker.Path,previousPath,StringComparison.OrdinalIgnoreCase))
                {
                    held = marker;
                    heldDistance = distance;
                }
                if (distance <= nearestDistance)
                {
                    nearest = marker;
                    nearestDistance = distance;
                }
            }
            // A small dead band prevents chatter between neighbours. Pointing at a star's centre always wins.
            if (held != null && (nearest == null || (nearestDistance > 1 && Math.Sqrt(heldDistance) <= Math.Sqrt(nearestDistance) + 1.5)))
                nearest = held;
            if (nearest == null) return null;
            var names = nearest.Selected ? selectedNames : directNames;
            if (!names.TryGetValue(nearest.Path,out var name) && !surveyCatalog.TryGetValue(nearest.Path,out name)) return null;
            return (nearest.Path, name, nearest.Selected ? SelectedStarStatus() : "未选中 · 查看连接", MapToScreen(nearest.Position));
        }

        string SelectedStarStatus()
        {
            switch (stellarPhase)
            {
                case "active": return "已选 · 星云分流已启用";
                case "starting": return "已选 · 正在启用分流";
                case "waiting": return "已选 · 等待网络连接";
                case "restoring": return "已选 · 正在恢复网络";
                case "unknown": return "已选 · 正在确认分流状态";
                default: return "已选 · 分流已关闭";
            }
        }
    }

    internal sealed partial class MainUi
    {
        static Action BindStarHover(Border input, StarfieldLayer sky, Border card, TextBlock name, TextBlock status)
        {
            var placement = new TranslateTransform();
            card.RenderTransform = placement;
            bool tracking = false;
            string current = null;
            Point anchor = new Point();
            const double margin = 8, gap = 14;

            void Place(Size size)
            {
                double x = anchor.X + gap;
                if (x + size.Width > sky.ActualWidth - margin) x = anchor.X - gap - size.Width;
                placement.X = Math.Max(margin,Math.Min(x,sky.ActualWidth - size.Width - margin));
                placement.Y = Math.Max(margin,Math.Min(anchor.Y - size.Height / 2,sky.ActualHeight - size.Height - margin));
            }

            void Hide()
            {
                if (current == null) return;
                current = null;
                sky.Inspect(null);
                UiMotion.Fade(card,0,100,() => { if (current == null) card.Visibility = Visibility.Collapsed; });
            }

            void Refresh()
            {
                if (!tracking) return;
                var hit = sky.ApplicationAt(Mouse.GetPosition(sky),current);
                if (!hit.HasValue) { Hide(); return; }

                var star = hit.Value;
                bool entering = current == null;
                current = star.Path;
                sky.Inspect(current);
                anchor = star.Position;
                if (name.Text != star.Name) name.Text = star.Name;
                if (status.Text != star.Status) status.Text = star.Status;
                card.Visibility = Visibility.Visible;
                double available = Math.Max(1,sky.ActualWidth - margin * 2);
                double width = Math.Min(320,available);
                if (card.MaxWidth != width) card.MaxWidth = width;
                Place(card.RenderSize);
                if (entering) UiMotion.Fade(card,1,120);
            }

            void Dismiss()
            {
                tracking = false;
                current = null;
                sky.Inspect(null);
                card.BeginAnimation(UIElement.OpacityProperty,null);
                card.Opacity = 0;
                card.Visibility = Visibility.Collapsed;
            }

            MouseEventHandler follow = (s,e) => { tracking = true; Refresh(); };
            input.MouseEnter += follow;
            input.MouseMove += follow;
            input.MouseLeave += (s,e) => { tracking = false; Hide(); };
            input.IsVisibleChanged += (s,e) => { if (!input.IsVisible) Dismiss(); };
            card.SizeChanged += (s,e) => { if (current != null) Place(e.NewSize); };
            sky.StarMapChanged += Refresh;
            return Dismiss;
        }
    }
}
