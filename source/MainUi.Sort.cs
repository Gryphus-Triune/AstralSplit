using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Grid sortPlate;
        SortStarTrail sortTrail;
        Border sortGlint;
        TranslateTransform glintSlide;
        VirtualizingStackPanel sortItemsPanel;
        DispatcherOperation sortLayout;
        readonly HashSet<SortRowFace> movingSortRows = new HashSet<SortRowFace>();
        FrameworkElement sortMenuBody, sortMenuOptions;
        ScaleTransform sortMenuScale;
        TranslateTransform sortMenuShift;
        Rect? sortMenuRelease;

        void InitializeSortMenu()
        {
            var menu = sort.ContextMenu;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popup,button,offset) => new[] {
                new CustomPopupPlacement(new Point(button.Width-popup.Width+10,button.Height),PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(button.Width-popup.Width+10,-popup.Height+17),PopupPrimaryAxis.Horizontal)
            };
            menu.Opened += (s,e) => {
                menu.ApplyTemplate();
                var plate = (Grid)menu.Template.FindName("MovingPlate",menu);
                if (plate != sortPlate)
                {
                    sortPlate = plate;
                    sortTrail = (SortStarTrail)menu.Template.FindName("SortTrail",menu);
                    sortGlint = (Border)menu.Template.FindName("SortGlint",menu);
                    // Template Freezables are shared and frozen. This popup owns its animation targets.
                    plate.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
                    var band = sortGlint.Background.Clone();
                    glintSlide = new TranslateTransform();
                    band.RelativeTransform = glintSlide;
                    sortGlint.Background = band;
                    ((LiquidLens)menu.Template.FindName("SortGlass",menu)).FollowTransform(plate.RenderTransform);
                    sortMenuBody = (Grid)menu.Template.FindName("SortMenuBody",menu);
                    sortMenuOptions = (Grid)menu.Template.FindName("OrbitSurface",menu);
                    sortMenuScale = new ScaleTransform();
                    sortMenuShift = new TranslateTransform();
                    sortMenuBody.RenderTransform = new TransformGroup { Children = { sortMenuScale, sortMenuShift } };
                }
                menu.UpdateLayout();
                PositionSortPlate(false,null);
                var release = sortMenuRelease;
                sortMenuRelease = null;
                if (release.HasValue && UiMotion.CanAnimate(sortMenuBody)) UnfoldSortMenu(release.Value);
                menu.Opacity = 1;
            };
            menu.SizeChanged += (s,e) => { if (menu.IsOpen && sortPlate!=null) PositionSortPlate(false,null); };
            menu.Closed += (s,e) => { ResetSortMenu(); ResetSortEntrance(); ResetSortArtwork(); };
            menu.PreviewKeyDown += SortMenuKey;
            sort.PreviewKeyDown += (s,e) => {
                if (e.Key!=Key.Down && e.Key!=Key.Up) return;
                e.Handled=true;
                OpenSortMenu();
                menu.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{
                    if (menu.IsOpen) NavigateSort(sortByMemory ? memorySort : windowSort);
                }));
            };
        }

        void UnfoldSortMenu(Rect release)
        {
            double width = sortMenuBody.ActualWidth, height = sortMenuBody.ActualHeight;
            if (width < 1 || height < 1 || PresentationSource.FromVisual(sort) == null) return;
            Point InPopup(Point point) { return sortMenuBody.PointFromScreen(sort.PointToScreen(point)); }
            var capsule = new Rect(InPopup(release.TopLeft), InPopup(release.BottomRight));
            double center = Math.Max(width * .14, Math.Min(width * .86, capsule.Left + capsule.Width / 2));
            // The popup has transparent side margins. Use a little of that space to keep the glass anchored.
            double reach = Math.Min(center, width - center) + 5;
            double startWidth = Math.Min(Math.Min(width * .74, capsule.Width), 2 * reach);
            double startHeight = Math.Min(height * .52, capsule.Height * .38);
            double x = center - startWidth / 2;
            // Use the actual popup position. WPF can put the menu above or slide it at a monitor edge.
            bool above = capsule.Top + capsule.Height / 2 > height / 2;
            double lip = above ? capsule.Top + 2 - startHeight : capsule.Bottom - 2;
            double y = Math.Max(-2, Math.Min(height - startHeight + 2, lip));
            var rate = DisplayRefresh.For(sort);
            // Set the released shape before the native popup becomes visible. Animation clocks start on a later tick.
            sortMenuScale.ScaleX = startWidth / width;
            sortMenuScale.ScaleY = startHeight / height;
            sortMenuShift.X = x;
            sortMenuShift.Y = y;
            sortMenuBody.Opacity = .53;
            sortMenuOptions.Opacity = 0;
            DoubleAnimationUsingKeyFrames Expand(double from, double to)
            {
                var shape = UiMotion.Ease(from,to,235,rate,.19,.72,.23,1);
                shape.FillBehavior = FillBehavior.HoldEnd;
                return shape;
            }
            sortMenuScale.BeginAnimation(ScaleTransform.ScaleXProperty,Expand(sortMenuScale.ScaleX,1));
            sortMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty,Expand(sortMenuScale.ScaleY,1));
            sortMenuShift.BeginAnimation(TranslateTransform.XProperty,Expand(x,0));
            sortMenuShift.BeginAnimation(TranslateTransform.YProperty,Expand(y,0));
            var shell = UiMotion.Ease(.53,1,190,rate);
            var options = UiMotion.Ease(0,1,160,rate,delay:55);
            shell.FillBehavior = options.FillBehavior = FillBehavior.HoldEnd;
            sortMenuBody.BeginAnimation(UIElement.OpacityProperty,shell);
            sortMenuOptions.BeginAnimation(UIElement.OpacityProperty,options);
        }

        void ResetSortEntrance()
        {
            sortMenuRelease = null;
            if (sortMenuBody == null) return;
            sortMenuScale.BeginAnimation(ScaleTransform.ScaleXProperty,null);
            sortMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            sortMenuShift.BeginAnimation(TranslateTransform.XProperty,null);
            sortMenuShift.BeginAnimation(TranslateTransform.YProperty,null);
            sortMenuBody.BeginAnimation(UIElement.OpacityProperty,null);
            sortMenuOptions.BeginAnimation(UIElement.OpacityProperty,null);
            sortMenuScale.ScaleX = sortMenuScale.ScaleY = 1;
            sortMenuShift.X = sortMenuShift.Y = 0;
            sortMenuBody.Opacity = sortMenuOptions.Opacity = 1;
        }

        void SortMenuKey(object sender, KeyEventArgs e)
        {
            var focused = windowSort.IsHighlighted ? windowSort : memorySort.IsHighlighted ? memorySort : Keyboard.FocusedElement as MenuItem;
            if (focused!=windowSort && focused!=memorySort) focused = sortByMemory ? memorySort : windowSort;
            switch (e.Key)
            {
                case Key.Escape: CloseSortMenu(); break;
                case Key.Home: NavigateSort(windowSort); break;
                case Key.End: NavigateSort(memorySort); break;
                case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                    NavigateSort(focused==windowSort ? memorySort : windowSort); break;
                case Key.Enter: case Key.Space:
                    ChooseSort(focused==memorySort); break;
                default: return;
            }
            e.Handled = true;
        }

        static void NavigateSort(MenuItem option)
        {
            // A ContextMenu's popup can retain native keyboard focus on its root.
            // WPF uses Selector.IsSelected for the navigated item, separately from its checkmark.
            option.Focus();
            Selector.SetIsSelected(option,true);
        }

        void SetSort(bool byMemory)
        {
            ApplySortChoice(byMemory,false);
            foreach (var row in rows) row.ShowMemory(byMemory);
            if (rows.Count!=0) RefreshView();
        }

        void ApplySortChoice(bool byMemory,bool animate)
        {
            sortByMemory=byMemory;
            windowSort.IsChecked=!byMemory;
            memorySort.IsChecked=byMemory;
            AutomationProperties.SetName(sort,byMemory ? "应用列表排序，当前按内存占用从高到低" : "应用列表排序，当前优先显示有窗口的应用");
            var rate=animate ? DisplayRefresh.For(sort) : null;
            void Face(string name,bool selectedFace,double direction)
            {
                var face=Find<StackPanel>(name);
                var shift=(TranslateTransform)face.RenderTransform;
                double alpha=face.Opacity, x=shift.X;
                double targetAlpha=selectedFace ? 1 : 0, targetX=selectedFace ? 0 : 4*direction;
                face.Opacity=targetAlpha;
                shift.X=targetX;
                face.BeginAnimation(UIElement.OpacityProperty,animate ? UiMotion.Ease(alpha,targetAlpha,170,rate) : null);
                shift.BeginAnimation(TranslateTransform.XProperty,animate ? UiMotion.Ease(x,targetX,180,rate) : null);
            }
            Face("MemorySortFace",byMemory,1);
            Face("WindowSortFace",!byMemory,-1);
        }

        void ChooseSort(bool byMemory)
        {
            bool changed=byMemory!=sortByMemory;
            bool animate=changed && !contrastTheme && UiMotion.CanAnimate(sort);
            var previous=sortByMemory ? memorySort : windowSort;
            ApplySortChoice(byMemory,animate);
            if (sort.ContextMenu.IsOpen)
            {
                ResetSortMenu();
                if (animate && sortTrail!=null && UiMotion.CanAnimate(sortTrail))
                    AnimateSortChoice(previous,byMemory ? memorySort : windowSort);
                else { ResetSortArtwork(); CloseSortMenu(); }
            }
            if (!changed) return;
            foreach (var row in rows) row.ShowMemory(byMemory);
            if (!animate || visibleRows.Count<2 || !UiMotion.CanAnimate(applications)) { RefreshView(); return; }
            // Run after the click handler so render work cannot postpone the sort choice.
            sortLayout?.Abort();
            sortLayout=Window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(AnimateSortRows));
        }

        static Path SortCheck(MenuItem item) { return item.Template?.FindName("CheckMark",item) as Path; }

        void ResetSortArtwork()
        {
            if (sortPlate==null) return;
            sortTrail.Clear();
            sortGlint.BeginAnimation(UIElement.OpacityProperty,null);
            glintSlide.BeginAnimation(TranslateTransform.XProperty,null);
            foreach (var item in new[]{windowSort,memorySort})
            {
                var check=SortCheck(item);
                check?.BeginAnimation(Shape.StrokeDashOffsetProperty,null);
                check?.BeginAnimation(UIElement.OpacityProperty,null);
            }
            PositionSortPlate(false,null);
        }

        void PositionSortPlate(bool animate,int? rate)
        {
            var transforms=(TransformGroup)sortPlate.RenderTransform;
            var scale=(ScaleTransform)transforms.Children[0];
            var slide=(TranslateTransform)transforms.Children[1];
            double end=sortByMemory ? sortTrail.ActualWidth/2 : 0;
            double from=slide.X, oldScale=scale.ScaleX;
            slide.X=end;
            slide.BeginAnimation(TranslateTransform.XProperty,animate ? UiMotion.Ease(from,end,315,rate,.2,.71,.24,1) : null);
            if (!animate) { scale.BeginAnimation(ScaleTransform.ScaleXProperty,null); return; }
            var stretch=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(315),FillBehavior=FillBehavior.Stop};
            stretch.KeyFrames.Add(new LinearDoubleKeyFrame(oldScale,TimeSpan.Zero));
            stretch.KeyFrames.Add(new SplineDoubleKeyFrame(1.035,TimeSpan.FromMilliseconds(112),new KeySpline(.2,0,.24,1)));
            stretch.KeyFrames.Add(new SplineDoubleKeyFrame(1,TimeSpan.FromMilliseconds(315),new KeySpline(.22,.68,.28,1)));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,UiMotion.AtRefresh(stretch,rate));
        }

        void AnimateSortChoice(MenuItem previous,MenuItem next)
        {
            var old=SortCheck(previous);
            var check=SortCheck(next);
            if (old==null || check==null) { ResetSortArtwork(); CloseSortMenu(); return; }
            var rate=DisplayRefresh.For(sort);
            PositionSortPlate(true,rate);
            Point Center(FrameworkElement el) { return el.TranslatePoint(new Point(el.ActualWidth/2,el.ActualHeight/2),sortTrail); }
            sortTrail.Fly(Center(old),Center(check),rate);
            glintSlide.BeginAnimation(TranslateTransform.XProperty,UiMotion.Ease(-.8,.87,310,rate,.22,.2,.35,1));
            var shine=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(310),FillBehavior=FillBehavior.Stop};
            shine.KeyFrames.Add(new LinearDoubleKeyFrame(sortGlint.Opacity,TimeSpan.Zero));
            shine.KeyFrames.Add(new LinearDoubleKeyFrame(.74,TimeSpan.FromMilliseconds(145)));
            shine.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.FromMilliseconds(310)));
            sortGlint.BeginAnimation(UIElement.OpacityProperty,UiMotion.AtRefresh(shine,rate));
            int revision=menuRevision;
            var arrival=UiMotion.Ease(15,0,155,rate,.2,0,.2,1,delay:195);
            arrival.Completed+=(s,e)=>{ if (revision==menuRevision) CloseSortMenu(125); };
            check.BeginAnimation(UIElement.OpacityProperty,UiMotion.Ease(0,1,100,rate,delay:190));
            check.BeginAnimation(Shape.StrokeDashOffsetProperty,arrival);
        }

        IEnumerable<KeyValuePair<AppRow,SortRowFace>> RealizedSortRows()
        {
            if (sortItemsPanel==null || !sortItemsPanel.IsLoaded)
                sortItemsPanel=Descendants<VirtualizingStackPanel>(applications).FirstOrDefault();
            if (sortItemsPanel==null) yield break;
            foreach (UIElement child in sortItemsPanel.Children)
                if (child is ListBoxItem item && item.Content is AppRow row &&
                    item.Template?.FindName("SortRow",item) is SortRowFace face && ReferenceEquals(face.DataContext,row))
                    yield return new KeyValuePair<AppRow,SortRowFace>(row,face);
        }

        void AnimateSortRows()
        {
            sortLayout=null;
            if (closed) return;
            if (!UiMotion.CanAnimate(applications) || contrastTheme) { RefreshView(); return; }
            var original=new Dictionary<AppRow,int>();
            for (int i=0;i<visibleRows.Count;i++) original[visibleRows[i]]=i;
            var poses=new Dictionary<AppRow,SortRowFace.Pose>();
            double height=listViewport.ActualHeight;
            foreach (var entry in RealizedSortRows())
            {
                var pose=entry.Value.CurrentPose(listViewport);
                if (pose.Origin.Y+entry.Value.ActualHeight>0 && pose.Origin.Y<height) poses[entry.Key]=pose;
            }
            RefreshView();
            applications.UpdateLayout();
            var rate=DisplayRefresh.For(applications);
            int stagger=0;
            foreach (var entry in RealizedSortRows())
            {
                var face=entry.Value;
                var resting=face.CurrentPose(listViewport);
                double y=resting.Origin.Y;
                if (!poses.TryGetValue(entry.Key,out var start))
                {
                    if (y+face.ActualHeight<=0 || y>=height || !original.TryGetValue(entry.Key,out int previous)) continue;
                    var item=(ListBoxItem)face.TemplatedParent;
                    int current=applications.ItemContainerGenerator.IndexFromContainer(item);
                    if (previous==current || current<0) continue;
                    // Off-screen rows enter locally. Never materialize a whole desktop process list.
                    start=resting;
                    start.Origin.Y+=Math.Sign(previous-current)*Math.Min(82,Math.Abs(previous-current)*face.ActualHeight);
                    start.Opacity=.35;
                }
                Vector offset=start.Origin-resting.Origin;
                if (offset.Length<.5 && Math.Abs(start.Opacity-1)<.015 && Math.Abs(start.Scale-1)<.001) continue;
                bool visible=y+face.ActualHeight>0 && y<height;
                movingSortRows.Add(face);
                face.Move(offset,start,Math.Min(stagger,3)*12,rate,()=>movingSortRows.Remove(face));
                if (visible) stagger++;
            }
        }

        void StopSortRows()
        {
            if (movingSortRows.Count==0) return;
            foreach (var face in movingSortRows.ToArray()) face.Stop();
        }

        void CancelSortLayout()
        {
            sortLayout?.Abort();
            sortLayout=null;
            StopSortRows();
        }
    }
}
