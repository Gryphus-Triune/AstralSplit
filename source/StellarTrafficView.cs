using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    public sealed class StellarTrafficView : FrameworkElement
    {
        const int Capacity=28;
        readonly double?[] tunnel=new double?[Capacity], uplink=new double?[Capacity];
        int cursor, count;
        readonly SolidColorBrush tunnelTint=new SolidColorBrush(Colors.PeachPuff),uplinkTint=new SolidColorBrush(Colors.LightSteelBlue);
        internal void SetDirections(double? up,double? down,double? physicalUp,double? physicalDown){
            SetTone(tunnelTint,up,down,Colors.PeachPuff);SetTone(uplinkTint,physicalUp,physicalDown,Colors.LightSteelBlue);
        }
        void SetTone(SolidColorBrush brush,double? up,double? down,Color idle){
            Color target=idle;
            if(up.HasValue && down.HasValue && up+down>0){
                double t=UiMotion.Unit(up.Value/(up.Value+down.Value));
                target=Color.FromRgb((byte)(250-133*t),(byte)(144+57*t),(byte)(107+147*t));
            }
            bool motion=UiMotion.CanAnimate(this) && !SystemParameters.HighContrast;
            UiMotion.PaintColor(brush,SolidColorBrush.ColorProperty,target,520,motion?DisplayRefresh.For(this):null,motion);
        }
        static readonly Typeface labels=new Typeface("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        static readonly Typeface figures=new Typeface("Cascadia Mono, Consolas, Microsoft YaHei UI");

        internal void Clear() { cursor=count=0; InvalidateVisual(); }
        internal void Push(double? a,double? b)
        {
            tunnel[cursor]=a; uplink[cursor]=b;
            cursor=(cursor+1)%Capacity;
            count=Math.Min(count+1,Capacity);
            InvalidateVisual();
        }
        internal static string Format(double? value)
        {
            if(!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value<0) return "—";
            double bytes=value.Value;
            if(bytes<1000) return bytes.ToString("0",CultureInfo.InvariantCulture)+" B/s";
            if(bytes<1000000) return (bytes/1000).ToString("0.#",CultureInfo.InvariantCulture)+" KB/s";
            if(bytes<1000000000) return (bytes/1000000).ToString("0.#",CultureInfo.InvariantCulture)+" MB/s";
            return (bytes/1000000000).ToString("0.#",CultureInfo.InvariantCulture)+" GB/s";
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
            var ink=(Brush)FindResource("SkyText");
            var secondary=(Brush)FindResource("SkyBodyText");
            double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            DrawLine(dc,tunnel,"TUN",0,SystemParameters.HighContrast?ink:tunnelTint,ink,secondary,dpi);
            DrawLine(dc,uplink,"上联",22,SystemParameters.HighContrast?ink:uplinkTint,ink,secondary,dpi);
        }
        void DrawLine(DrawingContext dc,double?[] values,string label,double y,Brush color,Brush ink,Brush secondary,double dpi)
        {
            var culture=CultureInfo.CurrentUICulture;
            var name=new FormattedText(label,culture,FlowDirection.LeftToRight,labels,11,secondary,dpi);
            dc.DrawText(name,new Point(0,y+1));
            double? latest=count==0 ? null:values[(cursor+Capacity-1)%Capacity];
            var text=new FormattedText(Format(latest),culture,FlowDirection.LeftToRight,figures,11,ink,dpi);
            dc.DrawText(text,new Point(Math.Max(84,ActualWidth-text.Width),y+1));
            double max=1024;
            for(int i=0;i<count;i++) max=Math.Max(max,values[(cursor-count+i+Capacity)%Capacity]??0);
            var pen=new Pen(color,1.2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            var hue=(color as SolidColorBrush)?.Color??Colors.White;
            var wash=new LinearGradientBrush(Color.FromArgb(91,hue.R,hue.G,hue.B),Color.FromArgb(0,hue.R,hue.G,hue.B),90){
                MappingMode=BrushMappingMode.Absolute,StartPoint=new Point(0,y+3),EndPoint=new Point(0,y+18)};
            wash.Freeze();
            Point? previous=null;
            for(int i=0;i<count;i++)
            {
                var rate=values[(cursor-count+i+Capacity)%Capacity];
                if(!rate.HasValue) { previous=null; continue; }
                var point=new Point(33+50.0*(Capacity-count+i)/(Capacity-1),y+14-10*UiMotion.Unit(rate.Value/max));
                if(previous.HasValue){
                    if(!SystemParameters.HighContrast){
                        var area=new StreamGeometry();using(var g=area.Open()){
                            g.BeginFigure(previous.Value,true,true);g.LineTo(point,false,false);
                            g.LineTo(new Point(point.X,y+18),false,false);g.LineTo(new Point(previous.Value.X,y+18),false,false);}
                        area.Freeze();dc.DrawGeometry(wash,null,area);
                    }
                    dc.DrawLine(pen,previous.Value,point);
                }
                else dc.DrawEllipse(color,null,point,0.7,0.7);
                if(i==count-1 && rate.Value>0 && !SystemParameters.HighContrast){
                    dc.PushOpacity(.24);dc.DrawEllipse(color,null,point,3.2,2.3);dc.Pop();
                    dc.DrawEllipse(color,null,point,1.65,1.45);dc.DrawEllipse(Brushes.White,null,point,.72,.65);
                }
                previous=point;
            }
        }
    }

    internal static class StellarIconPalette
    {
        internal static bool TryGetColor(ImageSource image,out Color color)
        {
            // Count hue in a small sample; ignore transparent padding and downweight neutral highlights.
            const int side=16, bins=16;
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen()) dc.DrawImage(image,new Rect(0,0,side,side));
            var bitmap=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bytes=new byte[side*side*4];bitmap.CopyPixels(bytes,side*4,0);
            var weights=new double[bins+1];var red=new double[bins+1];var green=new double[bins+1];var blue=new double[bins+1];
            for(int i=0;i<bytes.Length;i+=4)
            {
                double alpha=bytes[i+3]/255.0;
                if(alpha<0.45) continue;
                double r=bytes[i+2]/alpha,g=bytes[i+1]/alpha,b=bytes[i]/alpha;
                double high=Math.Max(r,Math.Max(g,b)),low=Math.Min(r,Math.Min(g,b)),span=high-low;
                int bin=bins;
                double weight=alpha*0.17;
                if(span>24 && high>48){
                    double sector=high==r?(g-b)/span:high==g?2+(b-r)/span:4+(r-g)/span;
                    bin=(int)Math.Floor((sector+6)*bins/6)%bins;
                    weight=alpha*span/160.0*(0.5+high/510.0);
                }
                weights[bin]+=weight;red[bin]+=r*weight;green[bin]+=g*weight;blue[bin]+=b*weight;
            }
            int best=0;
            for(int i=1;i<weights.Length;i++)if(weights[i]>weights[best])best=i;
            if(weights[best]<=0){color=Colors.Transparent;return false;}
            double v=weights[best],peak=Math.Max(red[best],Math.Max(green[best],blue[best]))/v;
            double lift=Math.Max(0,160-peak);
            color=Color.FromRgb((byte)Math.Min(255,Math.Round(red[best]/v+lift)),(byte)Math.Min(255,Math.Round(green[best]/v+lift)),
                (byte)Math.Min(255,Math.Round(blue[best]/v+lift)));
            return true;
        }
    }
}
