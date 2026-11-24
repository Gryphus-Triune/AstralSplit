using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralSplit
{
    internal static class ArtworkResolution
    {
        internal const int Ratio = 2;

        internal static double PixelsPerDip(Visual visual, double width, double height)
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            return PixelsPerDip(Math.Max(dpi.DpiScaleX,dpi.DpiScaleY),width,height);
        }

        internal static double PixelsPerDip(double dpi,double width,double height)
        {
            double target = Ratio * dpi;
            var limit = RenderCapability.MaxHardwareTextureSize;
            double edge = limit.Width > 0 && limit.Height > 0 ? Math.Min(limit.Width, limit.Height) : 8192;
            // A 4K display at 200% fits in one surface. Bound oversized or spanned windows.
            return Math.Min(target, Math.Min(edge / Math.Max(1, Math.Max(width, height)),
                Math.Sqrt(40000000.0 / Math.Max(1, width * height))));
        }
    }

    // Built on a worker. Only frozen drawing resources cross back to the UI dispatcher.
    internal sealed class SkyAtlas
    {
        internal const int StarHeight = 320, ReferenceWidth = 752, Padding = 82;
        internal readonly int Width;
        internal readonly BitmapSource Galaxy, Far, Middle;
        internal static readonly Color[] TintColors = {
            Color.FromRgb(75,85,129), Color.FromRgb(63,107,232), Color.FromRgb(38,156,183), Color.FromRgb(192,124,61)
        };
        internal readonly BitmapSource[] TintImages = new BitmapSource[TintColors.Length];
        internal readonly ImageDrawing DayField, NightField, NightTransmission;
        internal const int StarsPerStrip = 1180;
        static readonly Color[] DayStars = { Color.FromRgb(52,121,194), Color.FromRgb(96,99,193), Color.FromRgb(16,143,158), Color.FromRgb(176,112,72) };
        static readonly Color[] NightStars = { Color.FromRgb(162,208,255), Color.FromRgb(195,184,255), Color.FromRgb(144,233,232), Color.FromRgb(255,211,155) };
        internal readonly List<NearStar> Near = new List<NearStar>();
        internal struct NearStar
        {
            internal Point Point;
            internal double Radius, Phase, Period;
            internal Color Color;
            internal bool Spikes;
        }

        internal SkyAtlas(int width, int height, double scale, int day)
        {
            Width = width;
            Galaxy = BakeGalaxy(day,TintImages,width,scale);
            int w = (int)Math.Ceiling((width + 2 * Padding) * scale), h = (int)Math.Ceiling((StarHeight + 2 * Padding) * scale);
            var far = new byte[w * h * 4];
            var middle = new byte[far.Length];
            var occupied = new Dictionary<long,Point>();
            for (int strip = 0; strip * ReferenceWidth < width + 2*Padding; strip++)
            {
                var rand = new Random(unchecked(day * 397 ^ strip * 104729));
                for (int i = 0; i < StarsPerStrip; i++)
                {
                    Point pt;
                    int tries = 0;
                    do {
                        double x = rand.NextDouble() * ReferenceWidth - Padding, y = rand.NextDouble() * (StarHeight + 2 * Padding) - Padding;
                        pt = new Point(strip * ReferenceWidth + x, y);
                        double ridge = Math.Exp(-Math.Pow((y - (104 - (x - 622) * 0.404)) / 44, 2));
                        if (rand.NextDouble() * 2.5 > 1 + 1.5 * ridge) continue;
                        if (Place(occupied,pt)) break;
                    } while (++tries < 80);
                    if (tries >= 80) continue;
                    double light = Math.Pow(rand.NextDouble(), 2.2);
                    Color color = StarColor(rand);
                    if (i < 20)
                    {
                        Near.Add(new NearStar { Point = pt, Radius = 1.05 + 0.52 * light, Color = color,
                            Phase = rand.NextDouble(), Period = 2.4 + 2.8 * rand.NextDouble(), Spikes = i < 4 });
                    }
                    else
                    {
                        bool mid = i < 340;
                        Dot(mid ? middle : far, w, h, (pt.X + Padding) * scale, (pt.Y + Padding) * scale,
                            (mid ? 0.62 + 0.65 * light : 0.38 + 0.32 * light) * scale, color,
                            (mid ? 0.62 + 0.36 * light : 0.35 + 0.42 * light)*UiMotion.Unit((StarHeight+Padding-pt.Y)/96));
                    }
                }
            }
            Far = Pixels(far, w, h, scale);
            Middle = Pixels(middle, w, h, scale);
            BakeLowerField(width,height,scale,day,out DayField,out NightField,out NightTransmission);
        }

        // Static starlight behind the list. Both palettes share exactly the same sky coordinates.
        // A bright core and a wider aureole stay legible through different glass thicknesses.
        static void BakeLowerField(int width,int height,double scale,int day,out ImageDrawing dayField,out ImageDrawing nightField,out ImageDrawing transmitted)
        {
            const int top=145;
            int w=(int)Math.Ceiling((width+2*Padding)*scale),h=(int)Math.Ceiling((height+Padding-top)*scale);
            var light=new byte[w*h*4];var dark=new byte[light.Length];
            for(int strip=0;strip*ReferenceWidth<width+2*Padding;strip++)
            {
                var rng=new Random(unchecked(day*613 ^ strip*19391));
                for(int y=0;y<h/scale;y+=12)
                {
                    for(int n=0;n<17;n++)
                    {
                        double x=(strip+rng.NextDouble())*ReferenceWidth, yy=y+rng.NextDouble()*12;
                        double depth=rng.NextDouble(), brightness=rng.NextDouble(),band=(1+Math.Cos((x+yy*1.55)*0.012))/2;
                        double fade=0.48+0.52*Math.Min(1,(yy+3)/68);
                        bool jewel=depth>0.956-0.036*band, medium=depth>0.64;
                        int choice=rng.Next(10);int tint=choice<4?0:choice<7?1:choice<9?2:3;
                        Color hue=DayStars[tint],ice=NightStars[tint];
                        double px=x*scale,py=yy*scale,r=scale*(jewel?1.15+brightness*1.05:medium?0.67+brightness*0.47:0.35+brightness*0.32);
                        if(jewel)
                        {
                            double halo=scale*(4.2+4.8*brightness), strength=fade*(0.30+0.21*brightness);
                            Halo(light,dark,w,h,px,py,halo,hue,ice,strength);
                            if(brightness>0.75)
                            {
                                for(int arm=1;arm<=4;arm++)
                                {
                                    double a=fade*(5-arm)*0.115;
                                    foreach(var v in Rays) {
                                        Dot(light,w,h,px+arm*scale*v.X,py+arm*scale*v.Y,0.55*scale,hue,a);
                                        Dot(dark,w,h,px+arm*scale*v.X,py+arm*scale*v.Y,0.55*scale,ice,a);
                                    }
                                }
                            }
                        }
                        double alpha=fade*(jewel?0.97:medium?0.53+0.36*brightness:0.27+0.24*brightness);
                        Dot(light,w,h,px,py,r,hue,alpha);
                        Dot(dark,w,h,px,py,r,jewel?Colors.White:ice,alpha);
                        if(jewel)Dot(light,w,h,px,py,r*0.46,Colors.White,0.88);
                    }
                }
            }
            var rect=new Rect(-Padding,top,w/scale,h/scale);
            dayField=new ImageDrawing(Pixels(light,w,h,scale),rect);dayField.Freeze();
            nightField=new ImageDrawing(Pixels(dark,w,h,scale),rect);nightField.Freeze();
            // Lift faint points without letting the brightest cores compete with white text.
            var gain=new double[256];
            for(int a=1;a<gain.Length;a++)gain[a]=46*(1-Math.Exp(-6*a/255.0))/a;
            for(int p=0;p<dark.Length;p+=4){
                // Premultiplied transparent pixels are already four zeroes. Most of
                // the full-monitor field between stars never acquired any light.
                int alpha=dark[p+3];
                if(alpha==0)continue;
                double s=gain[alpha];
                for(int c=0;c<4;c++)dark[p+c]=(byte)Math.Round(dark[p+c]*s);
            }
            transmitted=new ImageDrawing(Pixels(dark,w,h,scale),rect);transmitted.Freeze();
        }
        static readonly Vector[] Rays={new Vector(-1,0),new Vector(1,0),new Vector(0,1),new Vector(0,-1)};
        static void Halo(byte[] day,byte[] night,int w,int h,double x,double y,double radius,Color a,Color b,double power)
        {
            for(int row=Math.Max(0,(int)(y-radius));row<=Math.Min(h-1,(int)(y+radius));row++)
                for(int col=Math.Max(0,(int)(x-radius));col<=Math.Min(w-1,(int)(x+radius));col++) {
                    double dx=col+0.5-x,dy=row+0.5-y,t=1-(dx*dx+dy*dy)/(radius*radius);
                    if(t<=0)continue;
                    double v=t*t*t*power;int p=(row*w+col)*4;Blend(day,p,a,v);Blend(night,p,b,v);
                }
        }
        static void Blend(byte[] pixels,int p,Color c,double a)
        {
            pixels[p]=(byte)(c.B*a+pixels[p]*(1-a));pixels[p+1]=(byte)(c.G*a+pixels[p+1]*(1-a));
            pixels[p+2]=(byte)(c.R*a+pixels[p+2]*(1-a));pixels[p+3]=(byte)(255*a+pixels[p+3]*(1-a));
        }

        static bool Place(Dictionary<long,Point> cells, Point point)
        {
            const int spacing = 3;
            int x = (int)Math.Floor(point.X/spacing), y = (int)Math.Floor(point.Y/spacing);
            long slot = ((long)x << 32) ^ (uint)y;
            if (cells.ContainsKey(slot)) return false;
            for (int row = y-1; row <= y+1; row++)
                for (int col = x-1; col <= x+1; col++)
                {
                    Point neighbor;
                    if (cells.TryGetValue(((long)col << 32) ^ (uint)row,out neighbor) &&
                        (neighbor-point).LengthSquared < spacing*spacing) return false;
                }
            cells.Add(slot,point); return true;
        }

        static Color StarColor(Random rng)
        {
            int k = rng.Next(10);
            return k < 7 ? Colors.White : k < 9 ? Color.FromRgb(207,224,255) : Color.FromRgb(255,226,196);
        }

        [ThreadStatic] static SolidColorBrush[] solidBrushes;
        [ThreadStatic] static Color[] solidColors;
        internal static SolidColorBrush Brush(Color color)
        {
            if(solidBrushes==null){solidBrushes=new SolidColorBrush[1024];solidColors=new Color[1024];}
            int index=(color.A*67+color.R*31+color.G*17+color.B)&1023;
            if(solidBrushes[index]!=null && solidColors[index]==color)return solidBrushes[index];
            var brush = new SolidColorBrush(color);
            brush.Freeze();solidColors[index]=color;solidBrushes[index]=brush;
            return brush;
        }

        static void Dot(byte[] pixels, int w, int h, double x, double y, double radius, Color c, double opacity)
        {
            for (int py = Math.Max(0, (int)(y - radius - 1)); py <= Math.Min(h - 1, (int)(y + radius + 1)); py++)
                for (int px = Math.Max(0, (int)(x - radius - 1)); px <= Math.Min(w - 1, (int)(x + radius + 1)); px++)
                {
                    double dx = px + 0.5 - x, dy = py + 0.5 - y;
                    double a = UiMotion.Unit(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy)) * opacity;
                    if (a <= 0) continue;
                    Blend(pixels,(py*w+px)*4,c,a);
                }
        }

        internal static BitmapSource Pixels(byte[] p, int w, int h, double scale)
        {
            var bitmap = BitmapSource.Create(w, h, scale * 96, scale * 96, PixelFormats.Pbgra32, null, p, w * 4);
            bitmap.Freeze();
            return bitmap;
        }

        static double Lattice(int x, int y, uint seed)
        {
            unchecked {
                uint h = (uint)x * 1597334677u ^ (uint)y * 3812015801u ^ seed;
                h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
                return (h & 0xffffff) / 16777215.0;
            }
        }

        static double Noise(double x, double y, uint seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            double u = x - ix, v = y - iy;
            u *= u * (3 - 2 * u); v *= v * (3 - 2 * v);
            double a = Lattice(ix,iy,seed), b = Lattice(ix+1,iy,seed);
            double c = Lattice(ix,iy+1,seed), d = Lattice(ix+1,iy+1,seed);
            return (a + (b - a) * u) * (1 - v) + (c + (d - c) * u) * v;
        }

        static readonly object galaxyLock=new object();
        static BitmapSource cachedGalaxy;
        static BitmapSource[] cachedTints;
        static int cachedDay;

        static BitmapSource BakeGalaxy(int day,BitmapSource[] tints,int width,double scale)
        {
            int w=(int)Math.Ceiling(Math.Min(1600,Math.Max(1060,width*1.4))*scale);
            int h=(int)Math.Ceiling(260*scale);
            lock(galaxyLock) {
                if(cachedGalaxy!=null && cachedDay==day && cachedGalaxy.PixelWidth==w &&
                    cachedGalaxy.PixelHeight==h && Math.Abs(cachedGalaxy.DpiX-scale*96)<.001) {
                    Array.Copy(cachedTints,tints,tints.Length);
                    return cachedGalaxy;
                }
                var image=GalaxyGpu.TryBake(w,h,day,scale,tints) ?? BakeGalaxyCpu(day,tints,w,h,scale);
                cachedDay=day;cachedGalaxy=image;cachedTints=(BitmapSource[])tints.Clone();
                return image;
            }
        }

        static BitmapSource BakeGalaxyCpu(int day,BitmapSource[] tints,int w,int h,double scale)
        {
            var pixels = new byte[w * h * 4];
            uint seed = (uint)day;
            var ridge = new double[w];
            for(int x=0;x<w;x++)
            {
                double u=(x+0.5)*512/w-0.5;
                ridge[x]=79+14*Math.Sin(u/65)+17*(Noise(u/36,1,seed)-0.5);
            }
            Parallel.For(0,h,new ParallelOptions {MaxDegreeOfParallelism=4}, y=>{
                double v=(y+0.5)*160/h-0.5;
                for (int x = 0; x < w; x++)
                {
                    double u=(x+0.5)*512/w-0.5;
                    double cloud = 0, amp = 0.52, fx = u / 53.0, fy = v / 22.0;
                    for (int i = 0; i < 5; i++) { cloud += amp * Noise(fx, fy, seed + (uint)i); fx *= 2.03; fy *= 2.03; amp *= 0.5; }
                    double dist = v - ridge[x] + (cloud - 0.46) * 42;
                    double envelope = Math.Exp(-dist * dist / 1420);
                    double core = Math.Exp(-dist * dist / 210) * UiMotion.Unit(2.5 * cloud - 0.35);
                    double lane = 0.55 * Math.Exp(-Math.Pow((dist + 4 + Math.Sin(u / 31.0) * 5) / 3.8, 2))
                        + 0.32 * Math.Exp(-Math.Pow((dist - 13 - Math.Sin(u / 40.0) * 5) / 3.4, 2));
                    double edge = UiMotion.Unit(Math.Min(Math.Min(u,511-u)/35.0,Math.Min(v,159-v)/16.0));
                    double filaments = 1 - Math.Min(1, Math.Abs(2*Noise(u/9.0,v/3.7,seed+41)-1));
                    double a = Math.Min(0.275,(0.19 * envelope + 0.32 * core) * (0.18 + cloud + 0.18*filaments) * (1 - lane) * edge);
                    double warm = UiMotion.Unit(core * 1.06), pink = Noise(u / 34.0, v / 27.0, seed + 98);
                    int p = (y * w + x) * 4;
                    pixels[p] = (byte)((255 - 41 * warm) * a);
                    pixels[p + 1] = (byte)((112 + 121 * warm) * a);
                    pixels[p + 2] = (byte)((105 + 62 * pink + (150 - 62 * pink) * warm) * a);
                    pixels[p + 3] = (byte)(255 * a);
                }
            });
            var bitmap = Pixels(pixels,w,h,scale);
            // Keep the per-column colors but write each high-resolution texture in scanline order.
            var wash = new byte[pixels.Length];
            var weight=new double[w];var red=new double[w];var green=new double[w];var blue=new double[w];
            for (int i=0;i<tints.Length;i++)
            {
                Color c=TintColors[i];
                for (int x=0;x<w;x++)
                {
                    double u=x/(double)(w-1);
                    weight[x]=(1-u)*185/255.0;
                    red[x]=c.R+(255-c.R)*u;green[x]=c.G+(255-c.G)*u;blue[x]=c.B+(255-c.B)*u;
                }
                for (int y=0;y<h;y++)
                {
                    int p=y*w*4;
                    for(int x=0;x<w;x++,p+=4)
                    {
                        byte alpha=(byte)Math.Round(pixels[p+3]*weight[x]);
                        wash[p]=(byte)Math.Round(blue[x]*alpha/255); wash[p+1]=(byte)Math.Round(green[x]*alpha/255);
                        wash[p+2]=(byte)Math.Round(red[x]*alpha/255); wash[p+3]=alpha;
                    }
                }
                tints[i]=Pixels(wash,w,h,scale);
            }
            return bitmap;
        }
    }
}
