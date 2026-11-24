using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace AstralSplit
{
    // Keep the shell's native popup, dismissal, keyboard handling and accessibility peers.
    internal sealed class GlassTrayMenu : Forms.ContextMenuStrip
    {
        readonly Func<bool> darkMode;
        readonly GlassRenderer glass = new GlassRenderer();
        readonly StatusCard card = new StatusCard();
        // Sample more often than a 60 Hz frame; the monotonic clock and tile check
        // prevent duplicate shell updates and avoid rounding 16 ms up to two OS ticks.
        readonly Forms.Timer orbitTimer = new Forms.Timer { Interval = 10 };
        readonly System.Diagnostics.Stopwatch time=System.Diagnostics.Stopwatch.StartNew();
        readonly TrayStatusFrames artwork = new TrayStatusFrames();
        readonly TrayIconMotion iconMotion = new TrayIconMotion();
        readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        long caughtAt=-1000;
        bool windowHidden, lightTaskbar, displayedLight;
        Forms.NotifyIcon trayIcon;
        Icon displayedIcon;
        int pixels = 16, displayedPixels, displayedTile = -1, displayedGlint = -1;
        int displayedOrbitTile, displayedOrbitQuarters;
        string state = "StatusOff", stateTitle = "分流已关闭", stateDetail = "应用选择尚未确认";
        Bitmap normal, hot;
        Rectangle capturedAt;
        bool opening, lightInk, highContrast;
        float scale = 1;
        [StructLayout(LayoutKind.Sequential)] struct WindowPosition { public IntPtr handle, after; public int x,y,cx,cy; public uint flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt,uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);

        internal GlassTrayMenu(Func<bool> darkMode)
        {
            this.darkMode=darkMode;
            ShowImageMargin=false;
            ShowCheckMargin=false;
            Renderer=glass;
            Items.Add(card);
            orbitTimer.Tick += (s,e) => RefreshOrbit();
        }
        internal void Bind(Forms.NotifyIcon icon)
        {
            trayIcon=icon;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += DisplayChanged;
            RefreshIconSize();
            UpdateStatus(stateTitle,state,-1,false);
        }
        void DisplayChanged(object sender, EventArgs e)
        {
            if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(new Action(RefreshIconSize));
        }
        void RefreshIconSize()
        {
            if (IsDisposed || trayIcon == null) return;
            int size = Forms.SystemInformation.SmallIconSize.Width;
            var point = StellarWindowFlight.TrayPoint(trayIcon);
            try {
                uint x, y;
                var monitor = MonitorFromPoint(new Point((int)point.X, (int)point.Y), 2);
                if (GetDpiForMonitor(monitor,0,out x,out y)==0 && x>0)
                    size=(int)Math.Round(16*x/96.0, MidpointRounding.AwayFromZero);
            } catch (DllNotFoundException) {} catch (EntryPointNotFoundException) {}
            pixels = Math.Max(16, Math.Min(128, size));
            lightTaskbar = !darkMode();
            try {
                const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
                if (Microsoft.Win32.Registry.GetValue(key, "SystemUsesLightTheme", null) is int light)
                    lightTaskbar = light != 0;
            } catch (Exception ex) when (ex is System.Security.SecurityException ||
                ex is UnauthorizedAccessException || ex is System.IO.IOException) {}
            if (Forms.SystemInformation.HighContrast) lightTaskbar = SystemColors.Window.GetBrightness() >= .5f;
            RefreshOrbit();
        }
        internal void SetWindowHidden(bool hidden){windowHidden=hidden;if(!hidden)caughtAt=-1000;RefreshOrbit();}
        internal void CatchStar(){caughtAt=time.ElapsedMilliseconds;RefreshOrbit();}
        internal void UpdateStatus(string title,string color,int selected,bool confirmed)
        {
            if(IsDisposed)return;
            string detail=selected<0?"应用选择尚未确认":"已选 "+selected+" 个应用";
            bool colorChanged=state!=color;
            iconMotion.SetConnected(confirmed, time.ElapsedMilliseconds);
            state=color;stateTitle=title;stateDetail=detail;
            string text=title+"，"+detail;
            if(card.AccessibleName!=text){card.Text=title;card.AccessibleName=text;card.AccessibleDescription=detail;card.Invalidate();}
            else if(colorChanged)card.Invalidate();
            if(trayIcon!=null && trayIcon.Text!="星云分流 · "+title)trayIcon.Text="星云分流 · "+title;
            RefreshOrbit();
        }
        internal void RefreshOrbit()
        {
            if(IsDisposed || trayIcon==null)return;
            long ms=time.ElapsedMilliseconds;
            bool motion = trayIcon.Visible && System.Windows.SystemParameters.ClientAreaAnimation && !Forms.SystemInformation.HighContrast;
            var frame = iconMotion.Sample(ms, motion);
            bool catching = motion && windowHidden && ms-caughtAt<620;
            bool playing = motion && (iconMotion.NeedsTicks || catching);
            if (orbitTimer.Enabled != playing) orbitTimer.Enabled = playing;
            int glint = catching ? (int)Math.Round(10 * Math.Sin(Math.PI * (ms-caughtAt) / 620.0)) : 0;
            if (displayedTile == frame.Tile && displayedOrbitTile == frame.OrbitTile && displayedOrbitQuarters == frame.OrbitQuarters
                && displayedGlint == glint && displayedPixels == pixels && displayedLight == lightTaskbar) return;
            Icon next = artwork.Create(frame.Tile, pixels, glint, lightTaskbar, frame.OrbitTile, frame.OrbitQuarters);
            try { trayIcon.Icon = next; } catch { next.Dispose(); throw; }
            displayedIcon?.Dispose();
            displayedIcon = next; displayedTile = frame.Tile; displayedGlint = glint; displayedPixels = pixels; displayedLight = lightTaskbar;
            displayedOrbitTile = frame.OrbitTile; displayedOrbitQuarters = frame.OrbitQuarters;
        }
        protected override void SetVisibleCore(bool visible)
        {
            if (!visible || Visible) { base.SetVisibleCore(visible); return; }
            opening=true;
            try
            {
                highContrast=Forms.SystemInformation.HighContrast;
                Renderer=highContrast ? (Forms.ToolStripRenderer)new Forms.ToolStripSystemRenderer() : glass;
                try
                {
                    uint x,y;
                    if (GetDpiForMonitor(MonitorFromPoint(Forms.Cursor.Position,2),0,out x,out y)==0) scale=Math.Max(1f,x/96f);
                }
                catch (DllNotFoundException) { scale=1; }
                catch (EntryPointNotFoundException) { scale=1; }
                Padding=new Forms.Padding(Px(6));
                MinimumSize=new Size(Px(206),0);
                MaximumSize=new Size(Px(206),0);
                foreach (Forms.ToolStripItem item in Items)
                {
                    item.AutoSize=false;
                    item.Size=new Size(Px(item==card?194:206),Px(item==card?63:33));
                    item.TextAlign=ContentAlignment.MiddleLeft;
                }
                base.SetVisibleCore(true);
            }
            finally { opening=false; if (!Visible) ReleasePlate(); }
        }
        int Px(float value) { return (int)Math.Round(value*scale); }
        protected override void OnOpening(CancelEventArgs e)
        {
            base.OnOpening(e);
            if (!e.Cancel) Prepare(Bounds);
        }
        protected override void WndProc(ref Forms.Message m)
        {
            // The shell may flip the menu above or to the left of the pointer on the last positioning message.
            if (opening && m.Msg==0x46 && m.LParam!=IntPtr.Zero && !IsWindowVisible(m.HWnd))
            {
                var p=(WindowPosition)Marshal.PtrToStructure(m.LParam,typeof(WindowPosition));
                if ((p.flags & 0x40)!=0)
                    Prepare(new Rectangle((p.flags&2)!=0 ? Location : new Point(p.x,p.y),(p.flags&1)!=0 ? Size : new Size(p.cx,p.cy)));
            }
            base.WndProc(ref m);
        }
        protected override void OnClosed(Forms.ToolStripDropDownClosedEventArgs e) { try { base.OnClosed(e); } finally { ReleasePlate(); } }
        protected override void Dispose(bool disposing)
        {
            if(disposing){
                orbitTimer.Dispose();
                if (trayIcon != null) {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                    Microsoft.Win32.SystemEvents.UserPreferenceChanged -= DisplayChanged;
                    trayIcon.Icon = null;
                    trayIcon = null;
                }
                displayedIcon?.Dispose(); displayedIcon = null;
                artwork.Dispose();
                ReleasePlate();
            }
            base.Dispose(disposing);
        }
        void ReleasePlate()
        {
            normal?.Dispose(); hot?.Dispose(); normal=hot=null; capturedAt=Rectangle.Empty;
        }
        static GraphicsPath Shape(RectangleF box,float r)
        {
            var path=new GraphicsPath(); float d=Math.Min(2*r, Math.Min(box.Width,box.Height));
            path.AddArc(box.X,box.Y,d,d,180,90); path.AddArc(box.Right-d,box.Y,d,d,270,90);
            path.AddArc(box.Right-d,box.Bottom-d,d,d,0,90); path.AddArc(box.X,box.Bottom-d,d,d,90,90); path.CloseFigure();
            return path;
        }
        void Prepare(Rectangle bounds)
        {
            if (bounds.Width<2 || bounds.Height<2 || bounds.Width>2048 || bounds.Height>2048 || capturedAt==bounds) return;
            ReleasePlate();
            var previousRegion=Region;
            using (var path=Shape(new RectangleF(0,0,bounds.Width,bounds.Height),Px(12))) Region=highContrast ? null : new Region(path);
            previousRegion?.Dispose();
            if (highContrast) return;
            capturedAt=bounds;
            using (var capture=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb))
            {
                bool fallbackDark=darkMode();
                using (var g=Graphics.FromImage(capture))
                {
                    try { g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy); }
                    catch (Exception ex) when (ex is Win32Exception || ex is ExternalException || ex is System.Security.SecurityException)
                    { g.Clear(fallbackDark ? Color.FromArgb(41,52,77) : Color.FromArgb(225,237,249)); }
                }
                Bake(capture,Apertures(),scale,out normal,out hot,out lightInk);
            }
        }
        internal Rectangle[] Apertures()
        {
            var boxes=new List<Rectangle>();
            foreach(Forms.ToolStripItem item in Items){
                if(item==card)continue;
                var bounds=item.Bounds;bounds.Inflate(-Px(5),-Px(2));boxes.Add(bounds);
            }
            return boxes.ToArray();
        }

        sealed class StatusCard : Forms.ToolStripLabel
        {
            internal StatusCard()
            {
                Name="RoutingStatus";AccessibleRole=Forms.AccessibleRole.StaticText;Margin=Forms.Padding.Empty;
            }
            public override bool CanSelect {get{return false;}}
            protected override void OnPaint(Forms.PaintEventArgs e)
            {
                var menu=(GlassTrayMenu)Owner;
                var g=e.Graphics;
                var box=new Rectangle(menu.Px(7),menu.Px(4),Width-menu.Px(14),Height-menu.Px(8));
                Color ink=menu.highContrast?SystemColors.MenuText:menu.lightInk?Color.FromArgb(245,248,255):Color.FromArgb(25,36,56);
                Color secondary=menu.highContrast?ink:menu.lightInk?Color.FromArgb(199,211,228):Color.FromArgb(64,79,104);
                Color accent=menu.highContrast?SystemColors.Highlight:StellarTrayArt.Accent(menu.state);
                using(var path=Shape(box,menu.Px(9)))
                using(var fill=new SolidBrush(menu.highContrast?SystemColors.Menu:Color.FromArgb(menu.lightInk?25:38,Color.White)))
                using(var edge=new Pen(menu.highContrast?ink:Color.FromArgb(64,Color.White)))
                {
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.FillPath(fill,path);g.DrawPath(edge,path);
                }
                StellarTrayArt.Draw(g,new RectangleF(box.X+menu.Px(6),box.Y+(box.Height-menu.Px(33))/2f,menu.Px(33),menu.Px(33)),accent,0);
                int left=menu.Px(55),width=Width-left-menu.Px(12),middle=Height/2;
                var flags=Forms.TextFormatFlags.Left|Forms.TextFormatFlags.VerticalCenter|Forms.TextFormatFlags.SingleLine|
                    Forms.TextFormatFlags.NoPadding|Forms.TextFormatFlags.EndEllipsis|Forms.TextFormatFlags.NoPrefix;
                using(var titleFont=new Font(menu.Font,FontStyle.Bold))
                    Forms.TextRenderer.DrawText(g,menu.stateTitle,titleFont,new Rectangle(left,middle-menu.Px(20),width,menu.Px(20)),ink,flags);
                Forms.TextRenderer.DrawText(g,menu.stateDetail,menu.Font,new Rectangle(left,middle,width,menu.Px(19)),secondary,flags);
            }
        }

        // One normalized, separable Gaussian on the small popup capture. It never captures a full display.
        static byte[] Frost(Bitmap capture,float scale)
        {
            int sw=capture.Width, sh=capture.Height;
            var pixels=new byte[sw*sh*4];
            var bits=capture.LockBits(new Rectangle(0,0,sw,sh),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try
            {
                for(int y=0;y<sh;y++)Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),pixels,y*sw*4,sw*4);
            }
            finally { capture.UnlockBits(bits); }
            var tmp=new byte[pixels.Length];
            double sigma=Math.Max(1,6.6*scale); int radius=(int)Math.Ceiling(2.6*sigma);
            var kernel=new double[2*radius+1]; double sum=0;
            for(int i=-radius;i<=radius;i++) { double v=Math.Exp(-i*i/(2*sigma*sigma)); kernel[i+radius]=v; sum+=v; }
            for(int i=0;i<kernel.Length;i++) kernel[i]/=sum;
            for(int pass=0;pass<2;pass++)
            {
                byte[] src=pass==0 ? pixels : tmp, dst=pass==0 ? tmp : pixels;
                for(int y=0;y<sh;y++) for(int x=0;x<sw;x++)
                {
                    double b=0,g=0,r=0;
                    for(int k=-radius;k<=radius;k++)
                    {
                        int sx=pass==0?Clamp(x+k,sw):x, sy=pass==1?Clamp(y+k,sh):y, index=4*(sy*sw+sx);
                        double weight=kernel[k+radius];
                        b+=src[index]*weight; g+=src[index+1]*weight; r+=src[index+2]*weight;
                    }
                    int j=(y*sw+x)*4;
                    dst[j]=(byte)Math.Round(b); dst[j+1]=(byte)Math.Round(g); dst[j+2]=(byte)Math.Round(r); dst[j+3]=255;
                }
            }
            return pixels;
        }
        static int Clamp(int value,int limit) { return Math.Max(0,Math.Min(limit-1,value)); }
        static double Unit(double value) { return Math.Max(0,Math.Min(1,value)); }
        static void Lens(double x,double y,Rectangle rect,double radius,double bevel,out double dx,out double dy,out double shine)
        {
            double px=x-rect.Left-rect.Width/2.0, py=y-rect.Top-rect.Height/2.0;
            double qx=Math.Abs(px)-rect.Width/2.0+radius,qy=Math.Abs(py)-rect.Height/2.0+radius;
            double ax=Math.Max(0,qx),ay=Math.Max(0,qy),len=Math.Sqrt(ax*ax+ay*ay);
            double d=radius-len-Math.Min(Math.Max(qx,qy),0);
            dx=dy=shine=0;
            if (d<0 || d>=bevel || len<0.001) return;
            double nx=Math.Sign(px)*ax/len,ny=Math.Sign(py)*ay/len;
            double sin=Unit(1-d/bevel),bend=bevel*Math.Tan(Math.Asin(sin)-Math.Asin(sin/1.48));
            dx=nx*bend; dy=ny*bend;
            double a=-0.54*nx-0.84*ny;
            shine=Unit(1-Math.Abs(d-0.8)/1.5)*(0.08+(a>0?0.70:0.40)*a*a);
        }
        internal static void Bake(Bitmap capture,Rectangle[] items,float scale,out Bitmap normal,out Bitmap hot,out bool lightInk)
        {
            int sw=capture.Width,sh=capture.Height,w=sw*ArtworkResolution.Ratio,h=sh*ArtworkResolution.Ratio;
            var backdrop=Frost(capture,scale);
            items=Array.ConvertAll(items,r=>new Rectangle(r.X*ArtworkResolution.Ratio,r.Y*ArtworkResolution.Ratio,
                r.Width*ArtworkResolution.Ratio,r.Height*ArtworkResolution.Ratio));
            scale*=ArtworkResolution.Ratio;
            double light=0;
            for(int i=0;i<backdrop.Length;i+=4) light+=0.0722*backdrop[i]+0.7152*backdrop[i+1]+0.2126*backdrop[i+2];
            lightInk=light/(sw*sh)<130;
            var pixels=new byte[w*h*4]; var selected=new byte[pixels.Length];
            var full=new Rectangle(0,0,w,h);
            for(int y=0;y<h;y++) for(int x=0;x<w;x++)
            {
                double dx,dy,rim; Lens(x+0.5,y+0.5,full,12*scale,5*scale,out dx,out dy,out rim);
                bool inRow=false; double glint=0;
                foreach(var item in items) if(item.Contains(x,y))
                {
                    double ix,iy; Lens(x+0.5,y+0.5,item,8*scale,4*scale,out ix,out iy,out glint);
                    dx+=ix;dy+=iy;
                    double qx=Math.Max(item.Left+8*scale-x,Math.Max(0,x-(item.Right-8*scale)));
                    double qy=Math.Max(item.Top+8*scale-y,Math.Max(0,y-(item.Bottom-8*scale)));
                    inRow=qx*qx+qy*qy<=64*scale*scale; break;
                }
                double sx=Math.Max(0,Math.Min(sw-1,(x-dx+0.5)*sw/w-0.5)),sy=Math.Max(0,Math.Min(sh-1,(y-dy+0.5)*sh/h-0.5));
                int xx=(int)sx,yy=(int)sy,at=(yy*sw+xx)*4;
                int xr=(yy*sw+Clamp(xx+1,sw))*4, yb=(Clamp(yy+1,sh)*sw+xx)*4,br=(Clamp(yy+1,sh)*sw+Clamp(xx+1,sw))*4;
                double fx=sx-xx,fy=sy-yy;
                int dst=(y*w+x)*4;
                for(int c=0;c<3;c++)
                {
                    double raw=(backdrop[at+c]*(1-fx)+backdrop[xr+c]*fx)*(1-fy)+(backdrop[yb+c]*(1-fx)+backdrop[br+c]*fx)*fy;
                    double baseTone=lightInk ? raw*0.47+(c==0?39:c==1?31:26) : raw*0.38+(c==0?255:c==1?252:248)*0.62;
                    double face=baseTone+(255-baseTone)*(rim+(1-rim)*glint*0.22);
                    pixels[dst+c]=(byte)Math.Round(face);
                    double active=inRow ? baseTone*0.36+(lightInk ? (c==0?159:c==1?105:52) : (c==0?252:c==1?218:179))*0.64 : face;
                    selected[dst+c]=(byte)Math.Round(active+(255-active)*(inRow?glint*0.70:0));
                }
                pixels[dst+3]=selected[dst+3]=255;
            }
            normal=Resolve(sw,sh,pixels); hot=Resolve(sw,sh,selected);
        }
        static Bitmap Resolve(int w,int h,byte[] samples)
        {
            const int ratio=ArtworkResolution.Ratio;
            var display=new byte[w*h*4];
            for(int y=0;y<h;y++)for(int x=0;x<w;x++){
                int origin=(y*ratio*w*ratio+x*ratio)*4, target=(y*w+x)*4;
                for(int c=0;c<3;c++){
                    int sum=0;
                    for(int j=0;j<ratio;j++)for(int i=0;i<ratio;i++)sum+=samples[origin+4*(j*w*ratio+i)+c];
                    display[target+c]=(byte)((sum+ratio*ratio/2)/(ratio*ratio));
                }
                display[target+3]=255;
            }
            return BitmapFrom(w,h,display);
        }
        static Bitmap BitmapFrom(int w,int h,byte[] data)
        {
            var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            var bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try { Marshal.Copy(data,0,bits.Scan0,data.Length); } finally { bitmap.UnlockBits(bits); }
            return bitmap;
        }

        sealed class GlassRenderer : Forms.ToolStripRenderer
        {
            protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
            {
                var menu=(GlassTrayMenu)e.ToolStrip;
                if (menu.normal!=null) e.Graphics.DrawImageUnscaled(menu.normal,Point.Empty);
                else e.Graphics.Clear(menu.darkMode() ? Color.FromArgb(45,56,77) : Color.FromArgb(237,243,251));
            }
            protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e) { }
            protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
            {
                var menu=(GlassTrayMenu)e.ToolStrip;
                if ((e.Item.Selected || e.Item.Pressed) && menu.hot!=null)
                    e.Graphics.DrawImage(menu.hot,new Rectangle(Point.Empty,e.Item.Size),e.Item.Bounds,GraphicsUnit.Pixel);
            }
            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                var menu=(GlassTrayMenu)e.ToolStrip;
                var g=e.Graphics; var fg=menu.lightInk ? Color.FromArgb(249,251,255) : Color.FromArgb(28,40,58);
                var box=new Rectangle(menu.Px(34),0,e.Item.Width-menu.Px(40),e.Item.Height);
                Forms.TextRenderer.DrawText(g,e.Text,e.TextFont,box,fg,Forms.TextFormatFlags.VerticalCenter|Forms.TextFormatFlags.Left|
                    Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.NoPadding|Forms.TextFormatFlags.EndEllipsis);
                var save=g.Save();
                try
                {
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.TranslateTransform(menu.Px(9),(e.Item.Height-menu.Px(16))/2f); g.ScaleTransform(menu.scale,menu.scale);
                    using(var p=new Pen(fg,1.4f) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round })
                    {
                        if(e.Item.Name=="OpenWindow") {
                            g.DrawLines(p,new[]{new PointF(2,8),new PointF(6,8),new PointF(11,3),new PointF(14,3)});
                            g.DrawLines(p,new[]{new PointF(6,8),new PointF(11,13),new PointF(14,13)});
                        } else { g.DrawArc(p,2.4f,2.8f,11.2f,11.2f,-52,282); g.DrawLine(p,8,1,8,7); }
                    }
                }
                finally { g.Restore(save); }
            }
        }
    }
}
