using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AstralSplit
{
    public sealed class ObservatoryGlassButton : Button
    {
        readonly SpringProgress press=new SpringProgress();
        readonly DispatcherTimer hold;
        readonly TranslateTransform origin=new TranslateTransform();
        readonly SolidColorBrush ink=new SolidColorBrush();
        Border field;
        CapsuleThumb face;
        FrameworkElement legend,resting,raised;
        Window owner;
        internal AnimatedSwitch Switch {get;set;}
        bool pointer,inside,engaged,cancelled,discardRelease;

        public ObservatoryGlassButton(){
            hold=new DispatcherTimer(DispatcherPriority.Input,Dispatcher){Interval=TimeSpan.FromMilliseconds(UiMotion.GlassHoldMilliseconds)};
            hold.Tick+=(s,e)=>{hold.Stop();if(engaged)Target(1,UiMotion.GlassLiftFrequency);};
            press.ValueChanged+=Paint;
            Loaded+=(s,e)=>{owner=Window.GetWindow(this);if(owner!=null)owner.Deactivated+=Cancel;Paint();};
            Unloaded+=(s,e)=>{if(owner!=null)owner.Deactivated-=Cancel;owner=null;Cancel(s,e);};
            IsEnabledChanged+=(s,e)=>{if(!IsEnabled)Cancel(s,EventArgs.Empty);};
            IsVisibleChanged+=(s,e)=>{if(!IsVisible)Cancel(s,EventArgs.Empty);};
        }
        public override void OnApplyTemplate(){
            base.OnApplyTemplate();
            field=GetTemplateChild("Field") as Border;face=GetTemplateChild("Held") as CapsuleThumb;
            legend=GetTemplateChild("Legend") as FrameworkElement;
            resting=GetTemplateChild("RestingSurface") as FrameworkElement;raised=GetTemplateChild("RaisedSurface") as FrameworkElement;
            if(face!=null)face.RenderTransform=origin;
            if(legend!=null)TextElement.SetForeground(legend,ink);
            Paint();
        }
        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e){
            pointer=true;inside=ContainsPress(e.GetPosition(this));cancelled=false;
            base.OnPreviewMouseLeftButtonDown(e);
        }
        bool ContainsPress(Point p){return new Rect(RenderSize).Contains(p);}
        internal void TrackPress(Point point){
            if(!pointer)return;
            inside=ContainsPress(point);Engage(inside && IsPressed && !cancelled);
        }
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);TrackPress(e.GetPosition(this));}
        protected override void OnMouseLeave(MouseEventArgs e){base.OnMouseLeave(e);if(pointer){inside=false;Engage(false);}}
        protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e){
            base.OnIsPressedChanged(e);if(hold!=null)Engage(IsPressed && !cancelled && (!pointer || inside));
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e){
            if(e.Key==Key.Space && !e.IsRepeat){pointer=false;cancelled=false;}
            if(e.Key==Key.Escape && CancelPress()){e.Handled=true;return;}
            base.OnPreviewKeyDown(e);
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){
            discardRelease=cancelled || (pointer && !ContainsPress(e.GetPosition(this)));
            try{base.OnMouseLeftButtonUp(e);}finally{pointer=discardRelease=cancelled=false;Engage(false);}
        }
        protected override void OnLostMouseCapture(MouseEventArgs e){
            base.OnLostMouseCapture(e);if(pointer){pointer=false;Engage(false);}
        }
        protected override void OnKeyUp(KeyEventArgs e){
            discardRelease=cancelled && e.Key==Key.Space;
            try{base.OnKeyUp(e);}finally{discardRelease=false;if(e.Key==Key.Space)cancelled=false;}
        }
        protected override void OnClick(){if(!discardRelease && !cancelled)base.OnClick();}
        internal bool CancelPress(){
            if(!IsPressed && !IsMouseCaptured && !engaged)return false;
            Cancel(this,EventArgs.Empty);return true;
        }
        void Cancel(object sender,EventArgs e){
            cancelled=IsPressed || IsMouseCaptured || engaged;pointer=false;
            if(IsMouseCaptured)ReleaseMouseCapture();
            Engage(false);
        }
        void Engage(bool value){
            if(value==engaged)return;engaged=value;hold.Stop();
            if(face!=null)face.Pressed=value;
            Target(value?AnimatedSwitch.TapPress:0,value?38:UiMotion.GlassReleaseFrequency);
            if(value)hold.Start();
        }
        void Target(double value,double frequency){
            bool motion=UiMotion.CanAnimate(this);
            press.MoveTo(value,motion,frequency,motion?DisplayRefresh.For(this):null);
        }
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e){
            base.OnPropertyChanged(e);if(e.Property==LiquidLens.GlassEnabledProperty)Paint();
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo e){base.OnRenderSizeChanged(e);Paint();}
        void Paint(){
            if(field==null || face==null || resting==null || raised==null || ActualWidth<1 || ActualHeight<1)return;
            double lift=AnimatedSwitch.GlassLift(press.Value);
            double thumb=Switch?.ThumbWidth??44,diameter=Switch?.ThumbDiameter??34;
            double reference=ActualHeight/Math.Min(diameter,thumb/AnimatedSwitch.RestThumbAspect);
            double rail=(Switch!=null && Switch.ActualHeight>0?Switch.ActualHeight:40)*reference;
            double label=Math.Max(0,ActualWidth-thumb*reference);
            var maximum=AnimatedSwitch.ThumbAtLift(ActualWidth,ActualHeight,rail,label,1);
            // Reserve the entire optical face, as the switch does, without expanding the button's hit box.
            if(face.Width!=Math.Ceiling(maximum.Width)+8)face.Width=Math.Ceiling(maximum.Width)+8;
            if(face.Height!=Math.Ceiling(maximum.Height)+8)face.Height=Math.Ceiling(maximum.Height)+8;
            var size=AnimatedSwitch.ThumbAtLift(ActualWidth,ActualHeight,rail,label,lift);
            var aperture=new Rect((ActualWidth-size.Width)/2,(ActualHeight-size.Height)/2,size.Width,size.Height);
            origin.X=aperture.X;origin.Y=aperture.Y;
            face.DrawCapsule(field,aperture,lift);
            bool glass=LiquidLens.GetGlassEnabled(this);
            double mix=glass?lift*lift*(3-2*lift):(lift>=.5?1:0);
            resting.Visibility=mix==1?Visibility.Hidden:Visibility.Visible;
            raised.Visibility=mix==0?Visibility.Hidden:Visibility.Visible;
            resting.Opacity=1-mix;raised.Opacity=mix;
            ink.Color=glass?Color.FromRgb(243,246,251):(mix==0?SystemColors.WindowTextColor:SystemColors.WindowColor);
        }
    }
}
