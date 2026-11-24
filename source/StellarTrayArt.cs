using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AstralSplit
{
    internal static class StellarTrayArt
    {
        internal const int Frames = 72, BreathSteps = 20, CatchFrames = 36;

        internal static Color Accent(string key)
        {
            switch (key)
            {
                case "StatusOn": return Color.FromArgb(53, 211, 175);
                case "StatusBusy": return Color.FromArgb(107, 165, 255);
                case "StatusWarning": return Color.FromArgb(246, 175, 73);
                default: return Color.FromArgb(157, 172, 200);
            }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

        internal static Icon IconFor(string status, int frame) { return MotionIcon(status,frame,0,-1); }
        internal static Icon MotionIcon(string status,int frame,int breath,int catchFrame)
        {
            using (var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                    DrawPlanet(g,new RectangleF(0,0,48,48),Accent(status),frame,status=="StatusOn",breath/(double)BreathSteps,catchFrame);
                var native = bitmap.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(native)) return (Icon)icon.Clone();
                }
                finally { DestroyIcon(native); }
            }
        }

        internal static void Draw(Graphics g, RectangleF bounds, Color accent, int frame)
        { DrawPlanet(g,bounds,accent,frame,false,0,-1); }

        static void DrawPlanet(Graphics g,RectangleF bounds,Color accent,int frame,bool orbiting,double breath,int catchFrame)
        {
            var saved = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                g.ScaleTransform(bounds.Width / 32, bounds.Height / 32);
                double catchTime=Math.Max(0,catchFrame)/(double)(CatchFrames-1);
                double impact=catchFrame<0?0:catchTime<.35?Math.Sin(Math.PI*catchTime/.70):Math.Pow(Math.Max(0,1-(catchTime-.35)/.65),1.5);
                if(catchFrame>=0){
                    for(int i=0;i<2;i++){
                        double t=Math.Max(0,catchTime*1.4-i*.23);
                        if(t>=1)continue;
                        float r=(float)(3+12*t);
                        using(var wave=new Pen(Color.FromArgb((int)(170*(1-t)),255,223,161),1.0f))
                            g.DrawEllipse(wave,-r,-r,2*r,2*r);
                    }
                }
                float size=(float)(.88+.10*breath+.22*impact-(catchFrame>=0?.24*Math.Max(0,1-catchTime/.26):0));
                g.ScaleTransform(size,size);
                double angle = frame * Math.PI * 2 / Frames;
                float tilt=(float)(-25+5*Math.Sin(angle));
                double light=Math.Min(1,(orbiting?.2:0)+breath*.55+impact+(catchFrame>=0?.7*Math.Max(0,1-catchTime/.3):0));
                if(light>0){
                    using(var outlineGlow=new GraphicsPath()){
                        outlineGlow.AddEllipse(-16,-16,32,32);
                        using(var glow=new PathGradientBrush(outlineGlow)){
                            glow.CenterColor=Color.FromArgb((int)(198*light),accent);
                            glow.SurroundColors=new[]{Color.FromArgb(0,accent)};g.FillPath(glow,outlineGlow);
                        }
                    }
                    accent=Color.FromArgb((int)(accent.R+(255-accent.R)*light*.77),(int)(accent.G+(254-accent.G)*light*.77),(int)(accent.B+(240-accent.B)*light*.77));
                }
                var globe = new RectangleF(-8.6f, -8.7f, 17.2f, 17.4f);
                Color pearl = Color.FromArgb((accent.R + 510) / 3, (accent.G + 510) / 3, (accent.B + 510) / 3);
                using (var orbit = new Pen(pearl, 1.7f))
                using (var body = new LinearGradientBrush(globe, pearl, accent, 40))
                using (var outline = new Pen(Color.FromArgb(206, 36, 39, 55), 1.1f))
                using (var spark = new SolidBrush(Color.White))
                {
                    orbit.StartCap=orbit.EndCap=LineCap.Round;
                    DrawRingHalf(g,tilt,false,orbiting,angle,orbit,spark);
                    g.FillEllipse(body, globe);
                    g.DrawEllipse(outline, globe);
                    DrawRingHalf(g,tilt,true,orbiting,angle,orbit,spark);
                }
            }
            finally { g.Restore(saved); }
        }

        static void DrawRingHalf(Graphics g,float tilt,bool front,bool orbiting,double phase,Pen orbit,Brush spark)
        {
            const double turn=2*Math.PI, spacing=turn/8;
            var saved=g.Save();
            try{
                g.RotateTransform(tilt);
                if(!orbiting){
                    g.DrawArc(orbit,-14.1f,-4.8f,28.2f,9.6f,front?0:180,180);
                }else{
                    // All strokes share one material phase around the ellipse. Gaps
                    // are transparent and reveal the globe when the near arc crosses it.
                    double offset=phase/8;
                    for(int i=0;i<8;i++){
                        double a=i*spacing+offset,b=a+spacing*.58;
                        // Each whole dash changes depth at an outer tip beyond the globe.
                        if((Math.Sin((a+b)*.5)>=0)!=front)continue;
                        double k=(4.0/3)*Math.Tan((b-a)/4);
                        float x1=(float)(14.1*Math.Cos(a)),y1=(float)(4.8*Math.Sin(a));
                        float x2=(float)(14.1*Math.Cos(b)),y2=(float)(4.8*Math.Sin(b));
                        g.DrawBezier(orbit,x1,y1,x1-(float)(k*14.1*Math.Sin(a)),y1+(float)(k*4.8*Math.Cos(a)),
                            x2+(float)(k*14.1*Math.Sin(b)),y2-(float)(k*4.8*Math.Cos(b)),x2,y2);
                    }
                }
                if((phase<Math.PI)==front){
                    float x=(float)(14.1*Math.Cos(phase)),y=(float)(4.8*Math.Sin(phase));
                    g.FillEllipse(spark,x-1.2f,y-1.2f,2.4f,2.4f);
                }
            }finally{g.Restore(saved);}
        }
    }
}
