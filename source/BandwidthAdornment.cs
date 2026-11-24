using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed partial class AppRow
    {
        internal double VisualRate {get;private set;}
        internal Color StarColor {get;private set;}=Color.FromRgb(153,206,255);
        internal bool StarFocused {get;private set;}
        internal void FocusStar(bool value){if(value!=StarFocused){StarFocused=value;Notify(nameof(StarFocused));}}
        internal void TintStar(Color value){if(value!=StarColor){StarColor=value;Notify(nameof(StarColor));}}
        internal void SetVisualRate(double rate){
            rate=UiMotion.IsFinite(rate)?Math.Max(0,rate):0;
            if(rate==VisualRate)return;
            double old=VisualRate;VisualRate=rate;Notify(nameof(VisualRate));
            if(rate>8192 && rate>old*2.4)Notify("TrafficBurst");
        }
    }

    public sealed class BandwidthAdornment : FrameworkElement
    {
        public bool TagPulse {get;set;}
        static readonly DependencyProperty Phase=DependencyProperty.Register("Phase",typeof(double),typeof(BandwidthAdornment),
            new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        static readonly DependencyProperty FocusLevel=DependencyProperty.Register("FocusLevel",typeof(double),typeof(BandwidthAdornment),
            new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        AppRow row;
        bool breathing;
        Brush glow;
        public BandwidthAdornment(){
            IsHitTestVisible=false;Loaded+=(s,e)=>Bind();Unloaded+=(s,e)=>Unbind();DataContextChanged+=(s,e)=>Bind();
            IsVisibleChanged+=(s,e)=>Bind();
        }
        void Unbind(){
            if(row!=null)row.PropertyChanged-=Changed;
            row=null;breathing=false;BeginAnimation(Phase,null);BeginAnimation(FocusLevel,null);SetValue(FocusLevel,0d);InvalidateVisual();
        }
        void Bind(){
            var next=IsVisible && IsLoaded?DataContext as AppRow:null;
            if(!ReferenceEquals(row,next)){
                Unbind();row=next;
                if(row!=null){row.PropertyChanged+=Changed;BrushForRow();SetValue(FocusLevel,row.StarFocused?.9:0);}
            }
            Update();
        }
        void BrushForRow(){
            var c=row.StarColor;var b=new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(c,0));b.GradientStops.Add(new GradientStop(Color.FromArgb(128,c.R,c.G,c.B),.60));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0,c.R,c.G,c.B),1));b.Freeze();glow=b;
        }
        void Changed(object sender,PropertyChangedEventArgs e){
            if(e.PropertyName==nameof(AppRow.StarFocused) && !TagPulse){
                double source=(double)GetValue(FocusLevel),target=row.StarFocused?.9:0;
                SetValue(FocusLevel,target);
                BeginAnimation(FocusLevel,UiMotion.CanAnimate(this) && !SystemParameters.HighContrast?
                    UiMotion.Ease(source,target,row.StarFocused?330:230,DisplayRefresh.For(this)):null);
            }
            if(e.PropertyName==nameof(AppRow.StarColor)){BrushForRow();InvalidateVisual();return;}
            if(e.PropertyName=="TrafficBurst" && TagPulse && row.Selected && UiMotion.CanAnimate(this) && !SystemParameters.HighContrast)
                BeginAnimation(Phase,UiMotion.Linear(0,1,620,DisplayRefresh.For(this)));
            if(e.PropertyName==nameof(AppRow.VisualRate) || e.PropertyName==nameof(AppRow.StarFocused) || e.PropertyName==nameof(AppRow.Selected))Update();
        }
        void Update(){
            bool next=!TagPulse && row!=null && row.Selected && row.VisualRate>0 && !SystemParameters.HighContrast && UiMotion.CanAnimate(this);
            if(next!=breathing){
                breathing=next;
                var pulse=next?UiMotion.Linear(0,1,1900,DisplayRefresh.For(this)):null;
                if(pulse!=null)pulse.RepeatBehavior=RepeatBehavior.Forever;
                BeginAnimation(Phase,pulse);
            }
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc){
            if(row==null || glow==null || SystemParameters.HighContrast)return;
            double t=(double)GetValue(Phase),w=ActualWidth,h=ActualHeight;
            if(TagPulse){
                if(t<=0 || t>=1 || !row.Selected || w<8 || h<4)return;
                // Follow the rounded upper edge, curling around the end caps.
                double angle=Math.PI+2*Math.PI*t,rx=Math.Max(0,(w-h)*.5),r=h*.5-1;
                var at=new Point(w*.5+rx*Math.Tanh(3*Math.Cos(angle))+r*Math.Cos(angle),h*.5+r*Math.Sin(angle));
                dc.PushOpacity(Math.Sin(Math.PI*t));dc.DrawEllipse(glow,null,at,4,2.7);dc.DrawEllipse(Brushes.White,null,at,1,.8);dc.Pop();
            }else{
                double rate=row.Selected?Math.Min(1,Math.Log10(1+row.VisualRate)/6.4):0;
                double power=Math.Max((double)GetValue(FocusLevel),rate*(.43+.17*Math.Cos(t*2*Math.PI)));
                if(power<=0)return;
                dc.PushOpacity(power);dc.DrawEllipse(glow,null,new Point(w/2,h/2),w/2,h/2);dc.Pop();
            }
        }
    }
}
