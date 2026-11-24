using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    internal static class StellarSkyArt
    {
        static readonly ConditionalWeakTable<BitmapSource,BitmapSource> dayBodies=new ConditionalWeakTable<BitmapSource,BitmapSource>();
        internal static BitmapSource Load(string theme)
        {
            var image = ReadImage(theme + ".jpg");
            dayBodies.Add(image,DayBody(image));
            return image;
        }

        internal static BitmapSource ReadImage(string filename)
        {
            var name = "AstralSplit.StellarArt." + filename;
            using (var stream = typeof(StellarSkyArt).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new FileNotFoundException("Missing stellar artwork", name);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        internal static Rect PanoramaCrop(double imageWidth,double imageHeight,Size size){
            double scale=Math.Max(size.Width/imageWidth,size.Height/imageHeight);
            double w=size.Width/scale,h=size.Height/scale;
            return new Rect((imageWidth-w)*.5,(imageHeight-h)*.34,w,h);
        }

        // Fit the photograph to the destination monitor, even when the current
        // client only shows a small, off-centre rectangle of that photograph.
        internal static void PaintMonitor(DrawingContext dc,BitmapSource image,Rect monitor,Size canvas){
            if(image==null || monitor.IsEmpty || monitor.Width<=0 || monitor.Height<=0)return;
            var crop=PanoramaCrop(image.PixelWidth,image.PixelHeight,monitor.Size);
            double scale=monitor.Width/crop.Width;
            var brush=new ImageBrush(image){ViewportUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill,
                Viewport=new Rect(monitor.X-crop.X*scale,monitor.Y-crop.Y*scale,image.PixelWidth*scale,image.PixelHeight*scale),
                TileMode=TileMode.FlipXY};
            brush.Freeze();dc.DrawRectangle(brush,null,new Rect(canvas));
        }

        internal static void Paint(DrawingContext dc, BitmapSource image, Size size, double horizon,bool day,bool panorama=false)
        {
            if (image == null || size.Width < 1 || size.Height < 1) return;
            if(panorama){
                var crop=PanoramaCrop(image.PixelWidth,image.PixelHeight,size);
                var brush=new ImageBrush(image){Viewbox=new Rect(crop.X/image.PixelWidth,crop.Y/image.PixelHeight,
                    crop.Width/image.PixelWidth,crop.Height/image.PixelHeight),Stretch=Stretch.Fill};
                brush.Freeze();dc.DrawRectangle(brush,null,new Rect(size));return;
            }
            // Share exactly the same pair of sampling rectangles with the optical bake.
            // Moving the status/details card changes the visible horizon without stretching the text or stars of the chart.
            var split = Math.Min(size.Height, Math.Max(1, horizon));
            DrawSlice(dc, image, 0, 330.0 / 820, new Rect(0, 0, size.Width, split));
            if (size.Height <= split) return;
            var lower=new Rect(0,split,size.Width,size.Height-split);
            BitmapSource body;
            if(day && dayBodies.TryGetValue(image,out body)) DrawSlice(dc,body,0,1,lower);
            else DrawSlice(dc,image,330.0 / 820,1 - 330.0 / 820,lower);
        }

        static BitmapSource DayBody(BitmapSource source)
        {
            // A daylight exposure of the same lower dust field illuminates light mode's dark labels.
            // Recolor the incident sky once, never the glass or text. Bright stellar cores retain a pearl highlight.
            int top=(int)Math.Round(source.PixelHeight*(330.0/820));
            int width=source.PixelWidth,height=source.PixelHeight-top,stride=width*4;
            var format=new FormatConvertedBitmap(source,PixelFormats.Bgr32,null,0);
            byte[] pixels=new byte[checked(stride*height)];
            format.CopyPixels(new Int32Rect(0,top,width,height),pixels,stride,0);
            for(int i=0;i<pixels.Length;i+=4){
                int b=pixels[i],g=pixels[i+1],r=pixels[i+2];
                int l=(r*54+g*183+b*19)>>8, shine=Math.Max(0,Math.Min(27,(l-64)/2));
                pixels[i+2]=(byte)Math.Max(157,Math.Min(251,224-(g+b)/4+r/3+shine));
                pixels[i+1]=(byte)Math.Max(169,Math.Min(252,233-(r+b)/4+g/5+shine));
                pixels[i]=(byte)Math.Max(183,Math.Min(254,242-(r+g)/3+b/5+shine));
            }
            var texture=BitmapSource.Create(width,height,source.DpiX,source.DpiY,PixelFormats.Bgr32,null,pixels,stride);
            texture.Freeze();return texture;
        }

        static void DrawSlice(DrawingContext dc, BitmapSource image, double y, double height, Rect bounds)
        {
            var brush = new ImageBrush(image) { Viewbox = new Rect(0, y, 1, height), Stretch = Stretch.Fill };
            brush.Freeze();
            dc.DrawRectangle(brush, null, bounds);
        }
    }
}
