using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed class LiquidScrollThumb : Thumb
    {
        const double RestWidth = 12, HoverWidth = 15, HeldWidth = 19;
        readonly SpringProgress hover = new SpringProgress(), lift = new SpringProgress();
        readonly DispatcherTimer hold;
        readonly TranslateTransform origin = new TranslateTransform();
        Size arrangedSlot;
        Border field;
        CapsuleThumb face;
        Window owner;
        WheelScrollViewer dragViewer;
        double dragOrigin, lastDragTarget;
        bool followedDrag;

        public LiquidScrollThumb()
        {
            hover.ValueChanged += Paint;
            lift.ValueChanged += Paint;
            hold = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) {
                Interval = TimeSpan.FromMilliseconds(UiMotion.GlassHoldMilliseconds)
            };
            hold.Tick += (s, e) => {
                hold.Stop();
                if (IsDragging && IsMouseCaptured && IsEnabled && IsVisible) MoveLift(true);
            };
            DragStarted += (s, e) => {
                if (!IsMouseCaptured || !IsDragging || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
                dragViewer = (TemplatedParent as ScrollBar)?.TemplatedParent as WheelScrollViewer;
                if (dragViewer != null) dragOrigin = dragViewer.ThumbTarget;
                followedDrag = false;
            };
            DragDelta += (s, e) => {
                if (dragViewer == null || !IsMouseCaptured || !IsDragging ||
                    !dragViewer.FollowThumb(this, e, dragOrigin, out double target)) return;
                // Handle the delta at its source, before ScrollBar issues a second, immediate scroll command.
                e.Handled = true;
                followedDrag = true;
                lastDragTarget = target;
                ReportDrag(ScrollEventType.ThumbTrack, target);
            };
            DragCompleted += (s, e) => {
                dragViewer = null;
                if (!followedDrag) return;
                followedDrag = false;
                // Capture can end just before mouse-up. The last accepted position still belongs to this drag.
                ReportDrag(ScrollEventType.EndScroll, lastDragTarget);
            };
            Loaded += (s, e) => {
                owner = Window.GetWindow(this);
                if (owner != null) owner.Deactivated += CancelInteraction;
            };
            Unloaded += (s, e) => {
                if (owner != null) owner.Deactivated -= CancelInteraction;
                owner = null;
                CancelInteraction(s, e);
            };
            IsVisibleChanged += (s, e) => { if (!IsVisible) CancelInteraction(s, EventArgs.Empty); };
            IsEnabledChanged += (s, e) => { if (!IsEnabled) CancelInteraction(s, EventArgs.Empty); };
        }

        void ReportDrag(ScrollEventType type, double offset)
        {
            (TemplatedParent as ScrollBar)?.RaiseEvent(new ScrollEventArgs(type, offset) { RoutedEvent = ScrollBar.ScrollEvent });
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            field = GetTemplateChild("GripField") as Border;
            face = GetTemplateChild("Grip") as CapsuleThumb;
            if (face != null) face.RenderTransform = origin;
        }

        protected override Size ArrangeOverride(Size available)
        {
            var size = base.ArrangeOverride(available);
            if (size.Width > 0 && size.Height > 0)
            {
                arrangedSlot = size;
                Paint();
            }
            return size;
        }

        protected override void OnDraggingChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnDraggingChanged(e);
            hold.Stop();
            if (IsDragging) hold.Start();
            else MoveLift(false);
        }

        internal void SetHovered(bool inside)
        {
            bool animate = UiMotion.CanAnimate(this);
            hover.MoveTo(inside ? 1 : 0, animate, 32, animate ? DisplayRefresh.For(this) : null);
        }

        void MoveLift(bool raised)
        {
            bool animate = UiMotion.CanAnimate(this);
            lift.MoveTo(raised ? 1 : 0, animate, raised ? UiMotion.GlassLiftFrequency : UiMotion.GlassReleaseFrequency,
                animate ? DisplayRefresh.For(this) : null);
        }

        void CancelInteraction(object sender, EventArgs e)
        {
            hold.Stop();
            if (IsDragging) CancelDrag();
            SnapMotion();
        }

        internal void SnapMotion()
        {
            hover.MoveTo(IsVisible && (TemplatedParent as ScrollBar)?.IsMouseOver == true ? 1 : 0, false, 32, null);
            lift.MoveTo(IsLoaded && IsVisible && IsEnabled && IsMouseCaptured && IsDragging && !hold.IsEnabled ? 1 : 0,
                false, UiMotion.GlassReleaseFrequency, null);
            // Hidden visuals may never get the last animated value notification or another arrange pass.
            Paint();
        }

        void Paint()
        {
            // A collapsing track already reports zero size when its held glass needs to reset.
            var slot = arrangedSlot;
            if (face == null || field == null || slot.Width <= 0 || slot.Height <= 0) return;
            double raised = lift.Value;
            double width = RestWidth + (HoverWidth - RestWidth) * hover.Value;
            width = Math.Min(slot.Width, width + (HeldWidth - width) * raised);
            double inset = Math.Min(slot.Height / 2, 3.5 - 3 * raised);
            var aperture = new Rect((slot.Width - width) / 2, inset, width, slot.Height - 2 * inset);
            origin.X = aperture.X;
            origin.Y = aperture.Y;
            // The native thumb owns the entire slot. Only the optical surface moves inside it.
            face.DrawCapsule(field, aperture, 0.70 * raised);
        }
    }

    public sealed class WheelScrollViewer : ScrollViewer
    {
        static int nextScrollOwner;
        readonly int scrollOwner=System.Threading.Interlocked.Increment(ref nextScrollOwner);
        SceneDevice scrollDevice;
        System.Windows.Interop.HwndSource inputSource;
        internal int ScrollOwnerId {get{return scrollOwner;}}
        internal double NativeAnchor {get{return listPanel==null?VerticalOffset:ClampOffset(listPanel.VerticalOffset-listSlide.Y);}}
        internal double NativeThumbAnchor {get{return motion==Motion.Thumb?destination:NativeAnchor;}}
        internal bool IsMovingThumb(Visual v){return v!=null && v==scrollTrack?.Thumb;}
        internal double TrackRatio {get{return scrollTrack?.Thumb==null || ScrollableHeight<=0?0:Math.Max(0,scrollTrack.ActualHeight-scrollTrack.Thumb.ActualHeight)/ScrollableHeight;}}
        internal void BindScroll(SceneDevice owner){
            if(scrollDevice==owner)return;
            UnbindScroll(scrollDevice);scrollDevice=owner;
            inputSource=PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            inputSource?.AddHook(BeforePointerButton);
            PushScroll();
        }
        internal void UnbindScroll(SceneDevice owner){
            if(scrollDevice==null || scrollDevice!=owner)return;
            if(IsScrollAnimating && IsLoaded && listPanel==ScrollInfo && owner.DisplayedScroll(scrollOwner,out double visible)){
                position=ClampOffset(visible);lastTick=Stopwatch.GetTimestamp();DrawPosition();
            }
            if(inputSource?.IsDisposed!=false)inputSource=null;
            inputSource?.RemoveHook(BeforePointerButton);inputSource=null;scrollDevice=null;
        }
        IntPtr BeforePointerButton(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled){
            if(!IsScrollAnimating || IsMouseCaptureWithin || !IsVisible || inputSource?.CompositionTarget==null)return IntPtr.Zero;
            if(message!=0x201 && message!=0x203 && message!=0x204 && message!=0x207 && message!=0x20b)return IntPtr.Zero;
            long bits=lp.ToInt64();var client=new Point((short)(bits&65535),(short)((bits>>16)&65535));
            var root=inputSource.RootVisual as UIElement;
            if(root==null || !root.IsAncestorOf(this))return IntPtr.Zero;
            var local=root.TranslatePoint(inputSource.CompositionTarget.TransformFromDevice.Transform(client),this);
            if(new Rect(RenderSize).Contains(local)){FreezeDisplayed();Mouse.Synchronize();}
            return IntPtr.Zero;
        }
        void FreezeDisplayed(){
            if(scrollDevice==null || !IsScrollAnimating || !scrollDevice.DisplayedScroll(scrollOwner,out double visible))return;
            position=destination=ClampOffset(visible);velocity=0;
            DrawPosition();
            StopWheelScroll(true);
        }
        void PushScroll(){
            if(scrollDevice==null)return;
            bool active=IsScrollAnimating;
            scrollDevice.SetScroll(new SceneDevice.ScrollMotion{
                Stamp=active?lastTick:Stopwatch.GetTimestamp(),Position=active?position:VerticalOffset,
                Velocity=active?velocity:0,Target=active?destination:VerticalOffset,Frequency=active?(IsWheelScrolling?WheelFrequency:ThumbFrequency):0,
                Owner=scrollOwner,Drag=motion==Motion.Thumb?1:0});
        }
        void SampleBeforeRetarget(){
            if(scrollDevice==null || !IsScrollAnimating)return;
            long now=Stopwatch.GetTimestamp();
            // Detents delivered together belong to the same input frame. After a
            // dispatcher stall, sample the old trajectory before installing a new target.
            if(SinceLastTick(now)>=4)AdvanceMotion(now);
        }
        enum Motion { None, Wheel, Thumb }
        const int WheelDetent = 120;
        const double LineScrollDistance = 16, WheelFrequency = 36, ThumbFrequency = 72, MaxFrameSeconds = 0.024;
        static readonly DependencyProperty MotionTimeProperty = DependencyProperty.Register("MotionTime", typeof(double),
            typeof(WheelScrollViewer), new PropertyMetadata(0.0, (element, args) => ((WheelScrollViewer)element).AdvanceMotion(Stopwatch.GetTimestamp())));
        Timer heartbeat;
        readonly TranslateTransform listSlide = new TranslateTransform(), thumbSlide = new TranslateTransform();
        VirtualizingStackPanel listPanel;
        Track scrollTrack;
        Motion motion;
        int generation, queuedTick;
        long lastTick, finalRequest;
        double position, destination, velocity, requestedOffset, pixelsPerDip, lateFrameMilliseconds;

        public WheelScrollViewer()
        {
            Unloaded += (sender, args) => {StopWheelScroll();UnbindScroll(scrollDevice);};
            IsVisibleChanged += (sender, args) => { if (!IsVisible) StopWheelScroll(); };
        }

        internal bool IsWheelScrolling { get { return motion == Motion.Wheel; } }
        internal bool IsScrollAnimating { get { return motion != Motion.None; } }
        internal double DisplayedOffset { get { return motion == Motion.Thumb ? ClampOffset(listPanel.VerticalOffset - listSlide.Y) :
            IsWheelScrolling ? position : VerticalOffset; } }
        internal double ThumbTarget { get { return motion == Motion.Thumb ? destination : VerticalOffset; } }
        internal event Action ScrollActivity, MotionCompleted, PositionChanged;

        internal bool FollowThumb(LiquidScrollThumb thumb, DragDeltaEventArgs delta, double initialOffset, out double target)
        {
            SampleBeforeRetarget();
            target = VerticalOffset;
            var panel = ScrollInfo as VirtualizingStackPanel;
            if (panel == null || ScrollableHeight <= 0 || !UiMotion.CanAnimate(this) || IsDeferredScrollingEnabled) return false;
            if (listPanel != panel || scrollTrack?.Thumb != thumb) ConnectSurfaces(panel);
            if (scrollTrack?.Thumb != thumb) return false;
            double travel = scrollTrack.ActualHeight - thumb.ActualHeight;
            if (travel <= 0) return false;
            // WPF reports displacement from the original grab point in the moving thumb's coordinates.
            // Converting it back to the track gives an absolute target even between two layout passes.
            double top = thumb.TranslatePoint(new Point(), scrollTrack).Y + delta.VerticalChange;
            double halfPixel = 0.5 / VisualTreeHelper.GetDpi(this).DpiScaleY;
            if (Math.Abs(delta.HorizontalChange) > 150) target = ClampOffset(initialOffset);
            else if (top <= halfPixel) target = 0;
            else if (top >= travel - halfPixel) target = ScrollableHeight;
            else target = top / travel * ScrollableHeight;
            if (motion != Motion.Thumb)
            {
                if (IsWheelScrolling) StopWheelScroll();
                if (Math.Abs(target - VerticalOffset) < 0.001) return true;
                StartMotion(Motion.Thumb, panel, VerticalOffset, target);
                ScrollActivity?.Invoke();
            }
            else if (target != destination)
            {
                // Follow only the latest pointer position. A true reversal should respond on the very next frame.
                if (velocity * (target - position) < 0) velocity = 0;
                destination = target;
                finalRequest = 0;
                PushScroll();
            }
            AlignThumb();
            return true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            int lines = SystemParameters.WheelScrollLines;
            if (e.Delta == 0) return;
            if (scrollTrack?.Thumb?.IsDragging == true) { e.Handled = true; return; }
            if (motion == Motion.Thumb) StopWheelScroll();
            SampleBeforeRetarget();
            var panel = ScrollInfo as VirtualizingStackPanel;
            if (lines == 0 || ScrollableHeight <= 0 || panel == null || IsMouseCaptureWithin || !UiMotion.CanAnimate(this))
            {
                StopWheelScroll();
                base.OnMouseWheel(e);
                return;
            }

            double distance = -(double)e.Delta / WheelDetent * (lines < 0 ? ViewportHeight : lines * LineScrollDistance);
            double current = IsWheelScrolling ? position : VerticalOffset;
            // Accumulate consecutive detents. A reversal discards the old, untravelled distance.
            bool continues = IsWheelScrolling && Math.Sign(destination - current) == Math.Sign(distance);
            double origin = continues ? destination : current;
            double next = ClampOffset(origin + distance);
            e.Handled = true;
            if (next == current) { StopWheelScroll(); return; }
            if (next == destination && IsWheelScrolling) return;
            if (IsWheelScrolling)
            {
                if (!continues) velocity = 0;
                destination = next;
                finalRequest = 0;
                PushScroll();
            }
            else StartMotion(Motion.Wheel, panel, current, next, wheelInput: true);
            ScrollActivity?.Invoke();
        }

        internal void Locate(double offset)
        {
            SampleBeforeRetarget();
            double target=ClampOffset(offset),current=DisplayedOffset;
            var panel=ScrollInfo as VirtualizingStackPanel;
            if(IsWheelScrolling && panel==listPanel && UiMotion.CanAnimate(this)){
                if(target==destination)return;
                if(velocity*(target-position)<0)velocity=0;
                destination=target;finalRequest=0;PushScroll();ScrollActivity?.Invoke();return;
            }
            StopWheelScroll();
            if(panel==null || !UiMotion.CanAnimate(this)){ScrollToVerticalOffset(target);return;}
            StartMotion(Motion.Wheel,panel,current,target);ScrollActivity?.Invoke();
        }
        void StartMotion(Motion kind, VirtualizingStackPanel panel, double current, double target, bool wheelInput = false)
        {
            ConnectSurfaces(panel);
            position = requestedOffset = current;
            destination = target;
            velocity = 0;
            finalRequest = 0;
            pixelsPerDip = VisualTreeHelper.GetDpi(this).DpiScaleY;
            Volatile.Write(ref lastTick, Stopwatch.GetTimestamp());
            motion = kind;
            if (kind == Motion.Thumb) LayoutUpdated += AlignDragAfterLayout;
            // WPF samples the timekeeper before layout and keeps the frame cadence active during the glide.
            int? rate = DisplayRefresh.For(this);
            lateFrameMilliseconds = Math.Max(8, 1000.0 / (rate ?? 60) - 2);
            var timekeeper = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
            // A delayed composition callback should not stall the animation until the input stream ends.
            heartbeat = new Timer(WakeMotion, ++generation, 8, 8);
            BeginAnimation(MotionTimeProperty, UiMotion.AtRefresh(timekeeper, rate));
            double travelPixels = Math.Abs(target - current) * pixelsPerDip;
            if (wheelInput && IsWheelScrolling && travelPixels >= 2)
            {
                // Make the first scene packet carry motion even if WPF hasn't sampled its clock yet.
                // For a resting critical spring, 1-(1+z)*exp(-z) <= z*z/2, z = frequency*time.
                // This limits the preview to three physical pixels, including full-page scrolls.
                double firstStep = Math.Min(.010, Math.Sqrt(6 / travelPixels) / WheelFrequency);
                long now = Stopwatch.GetTimestamp();
                Volatile.Write(ref lastTick, now - (long)(firstStep * Stopwatch.Frequency));
                AdvanceMotion(now);
            }
            PushScroll();
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            if (motion != Motion.Thumb || e.ChangedButton != MouseButton.Left || scrollTrack?.Thumb?.IsMouseOver != true)
            {
                FreezeDisplayed();
                StopWheelScroll();
            }
            base.OnPreviewMouseDown(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            FreezeDisplayed();
            StopWheelScroll();
            base.OnPreviewKeyDown(e);
        }

        internal void StopWheelScroll(bool finish = false)
        {
            if (!IsScrollAnimating) return;
            double offset = ClampOffset(finish ? destination : DisplayedOffset);
            EndMotion();
            if (listPanel != ScrollInfo) return;
            listPanel.SetVerticalOffset(offset);
            // Commit the displayed position before a thumb drag, click or keyboard scroll starts.
            if (IsLoaded && IsVisible && (!listPanel.IsMeasureValid || !listPanel.IsArrangeValid || !IsArrangeValid)) UpdateLayout();
            PushScroll();
        }

        void EndMotion()
        {
            if (motion == Motion.Thumb) LayoutUpdated -= AlignDragAfterLayout;
            motion = Motion.None;
            heartbeat?.Dispose();
            heartbeat = null;
            BeginAnimation(MotionTimeProperty, null);
            listSlide.Y = thumbSlide.Y = 0;
            PushScroll();
            MotionCompleted?.Invoke();
        }

        void ConnectSurfaces(VirtualizingStackPanel panel)
        {
            if (listPanel != panel)
            {
                listPanel = panel;
                panel.RenderTransform = listSlide;
            }
            var bar = GetTemplateChild("PART_VerticalScrollBar") as ScrollBar;
            if (bar == null) return;
            bar.ApplyTemplate();
            scrollTrack = bar.Template.FindName("PART_Track", bar) as Track;
            if (scrollTrack?.Thumb != null && scrollTrack.Thumb.RenderTransform != thumbSlide)
                scrollTrack.Thumb.RenderTransform = thumbSlide;
        }

        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            if (IsScrollAnimating)
            {
                if (listPanel != ScrollInfo || ScrollableHeight <= 0) EndMotion();
                else
                {
                    position = ClampOffset(position);
                    destination = ClampOffset(destination);
                    if (IsWheelScrolling && Math.Abs(position - VerticalOffset) > PreviewLimit + 1) EndMotion();
                    else DrawPosition();
                }
            }
            else if (ScrollInfo is VirtualizingStackPanel panel && panel != listPanel)
                ConnectSurfaces(panel);
            if(!IsScrollAnimating)PushScroll();
            base.OnScrollChanged(e);
        }

        double SinceLastTick(long now) { return (now - lastTick) * 1000.0 / Stopwatch.Frequency; }

        void WakeMotion(object id)
        {
            double elapsed = (Stopwatch.GetTimestamp() - Volatile.Read(ref lastTick)) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < Volatile.Read(ref lateFrameMilliseconds) || Dispatcher.HasShutdownStarted ||
                Interlocked.Exchange(ref queuedTick, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
                Volatile.Write(ref queuedTick, 0);
                if (!IsScrollAnimating || (int)id != generation) return;
                long now = Stopwatch.GetTimestamp();
                if (SinceLastTick(now) >= lateFrameMilliseconds) AdvanceMotion(now);
            }));
        }

        void AdvanceMotion(long now)
        {
            if (!IsScrollAnimating) return;
            if (listPanel != ScrollInfo || !UiMotion.CanAnimate(this))
            {
                StopWheelScroll(!SystemParameters.ClientAreaAnimation);
                return;
            }

            // A drag tracks wall time, including a missed frame. It must not build up seconds of lag on a large seek.
            double elapsed = (now - lastTick) / (double)Stopwatch.Frequency;
            if (IsWheelScrolling && scrollDevice==null) elapsed = Math.Min(MaxFrameSeconds, elapsed);
            Volatile.Write(ref lastTick, now);
            double end = destination = ClampOffset(destination);
            double displacement = position - end;
            double frequency = IsWheelScrolling ? WheelFrequency : ThumbFrequency;
            double coefficient = velocity + frequency * displacement;
            double decay = Math.Exp(-frequency * elapsed);
            double remaining = (displacement + coefficient * elapsed) * decay;
            velocity = (velocity - frequency * coefficient * elapsed) * decay;
            if (Math.Sign(remaining) != Math.Sign(displacement) || Math.Abs(remaining) * pixelsPerDip <= 0.7)
            {
                remaining = 0;
                velocity = 0;
            }
            double limit = PreviewLimit;
            double next = end + remaining;
            position = ClampOffset(IsWheelScrolling ? Math.Max(VerticalOffset - limit, Math.Min(VerticalOffset + limit, next)) : next);
            DrawPosition();

            bool settled = position == end;
            if (settled && Math.Abs(VerticalOffset - end) * pixelsPerDip <= 0.51)
            {
                EndMotion();
                return;
            }
            if (Math.Abs(position - requestedOffset) >= Math.Min(48, ViewportHeight / 4) ||
                (settled && finalRequest == 0))
            {
                // Cached rows cover the movement between native anchors, without measuring the list each frame.
                requestedOffset = position;
                listPanel.SetVerticalOffset(position);
                if (settled) finalRequest = now;
            }
            else if (settled && (now - finalRequest) * 1000.0 / Stopwatch.Frequency >= 80)
                StopWheelScroll(true);
        }

        double PreviewLimit { get { return Math.Min(144, ViewportHeight * 0.6); } }

        void AlignThumb()
        {
            var thumb = scrollTrack?.Thumb;
            if (thumb == null || ScrollableHeight <= 0) return;
            double travel = Math.Max(0, scrollTrack.ActualHeight - thumb.ActualHeight);
            thumbSlide.Y = destination / ScrollableHeight * travel - VisualTreeHelper.GetOffset(thumb).Y;
        }

        void AlignDragAfterLayout(object sender, EventArgs args)
        {
            // Row recycling and thumb arrangement may finish after ScrollChanged in the same frame.
            if (motion == Motion.Thumb && listPanel == ScrollInfo) DrawThumbPosition();
        }

        void DrawThumbPosition()
        {
            // Never translate the realized rows outside their cache while the next section is being laid out.
            double limit = PreviewLimit;
            listSlide.Y = Math.Max(-limit, Math.Min(limit, listPanel.VerticalOffset - position));
            AlignThumb();
        }

        void DrawPosition()
        {
            if (motion == Motion.Thumb) DrawThumbPosition();
            else
            {
                double shift = VerticalOffset - position;
                listSlide.Y = shift;
                if (scrollTrack?.Thumb != null && ScrollableHeight > 0)
                    thumbSlide.Y = -shift * Math.Max(0, scrollTrack.ActualHeight - scrollTrack.Thumb.ActualHeight) / ScrollableHeight;
            }
            PositionChanged?.Invoke();
        }

        double ClampOffset(double value) { return Math.Max(0, Math.Min(ScrollableHeight, value)); }
    }
}
