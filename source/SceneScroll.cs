using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AstralSplit
{
    internal sealed partial class SceneGraph
    {
        internal bool NativeScrolling { get; set; }
        WheelScrollViewer scroller;
        readonly ConditionalWeakTable<GlyphRun,RowText> rowText=new ConditionalWeakTable<GlyphRun,RowText>();
        readonly ConditionalWeakTable<ApplicationIcon,RowIcon> rowIcons=new ConditionalWeakTable<ApplicationIcon,RowIcon>();
        sealed class RowIcon {
            internal uint Version;
            internal DrawingGroup Drawing;
            public RowIcon(){}
        }
        DrawingGroup IconDrawing(ApplicationIcon icon){
            var entry=rowIcons.GetOrCreateValue(icon);
            if(entry.Version!=icon.DrawingVersion || entry.Drawing==null){
                entry.Version=icon.DrawingVersion;
                entry.Drawing=VisualTreeHelper.GetDrawing(icon);
            }
            return entry.Drawing;
        }
        sealed class RowText {
            internal Rect Bounds,Tile;
            internal double Density;
            internal Brush Ink;
            internal int Image;
            public RowText(){}
        }
        bool insideScroll,seenScroll;
        void FinishScrollPacket(){
            if(seenScroll || scroller==null)return;
            scroller.UnbindScroll(device);scroller=null;
        }

        bool ScrollSurface(Visual visual,SceneDevice.Pass pass,Matrix world,Rect clip,double alpha,
                           double opacity,Effect effect,Brush mask,Geometry shape)
        {
            if(!Axis(world) || world.M22<=0 || world.M11<=0 || opacity<=0)return false;
            var panel=visual as VirtualizingStackPanel;
            if(panel!=null && panel.ScrollOwner is WheelScrollViewer viewer && effect==null && mask==null && shape==null)
                return ScrollRows(panel,viewer,pass,world,clip,alpha*opacity);
            if(scroller==null || !scroller.IsMovingThumb(visual) || effect!=null || mask!=null || shape!=null)return false;
            var bounds=Bounds(visual);
            if(!Valid(bounds) || scroller.TrackRatio<=0)return false;
            // Dragging pins the native grip to the pointer target. Wheel scrolling instead
            // derives the grip's position from the same sampled offset as the text.
            int image=ScrollLayer(visual,bounds,pass,world);
            var q=SceneDevice.Quad(pass,bounds,world,clip,image,Unit,alpha*opacity,23);
            q.Constant(0,scroller.NativeThumbAnchor,0,scroller.ScrollableHeight,-2*world.M22*scroller.TrackRatio/pass.Area.Height);
            q.Constant(1,scroller.ScrollOwnerId);
            pass.Draws.Add(q);
            return true;
        }

        bool ScrollRows(VirtualizingStackPanel panel,WheelScrollViewer viewer,SceneDevice.Pass pass,
                        Matrix world,Rect clip,double opacity)
        {
            if(!world.HasInverse || viewer.ScrollableHeight<=0 || panel.Children.Count==0)return false;
            var inverse=world;inverse.Invert();
            Rect visible=Rect.Transform(clip,inverse);
            // Only realized application rows form the strip. Clamp look-ahead to these
            // bounds so a large track jump cannot reveal unpainted or recycled rows.
            double top=double.PositiveInfinity,bottom=double.NegativeInfinity;
            foreach(UIElement child in panel.Children){
                if(!child.IsVisible || child.RenderSize.Height<=0)continue;
                double y=VisualTreeHelper.GetOffset(child).Y;
                top=Math.Min(top,y);bottom=Math.Max(bottom,y+child.RenderSize.Height);
            }
            if(top>=bottom)return false;
            // The virtualizing panel can realize a full extra page either side. A few
            // rows of look-ahead cover the spring; quantization keeps this band stable.
            const double quantum=64;
            double ahead=Math.Min(144,Math.Max(32,viewer.ViewportHeight*.6));
            top=Math.Max(top,quantum*Math.Floor((visible.Top-ahead)/quantum));
            bottom=Math.Min(bottom,quantum*Math.Ceiling((visible.Bottom+ahead)/quantum));
            if(top>=bottom)return false;
            double sx=pass.Sx*world.M11,sy=pass.Sy*world.M22;
            if(sx<=0 || sy<=0)return false;
            double x=Math.Floor(visible.Left*sx)/sx,right=Math.Ceiling(visible.Right*sx)/sx;
            var band=new Rect(x,Math.Floor(top*sy)/sy,right-x,0);
            band.Height=Math.Ceiling(bottom*sy)/sy-band.Top;
            if(!Valid(band) || band.Width*sx>8192 || band.Height*sy>8192)return false;
            if(scroller!=viewer){scroller?.UnbindScroll(device);scroller=viewer;}
            seenScroll=true;
            viewer.BindScroll(device);
            int image=ScrollLayer(panel,band,pass,world);
            var command=SceneDevice.Quad(pass,band,world,clip,image,Unit,opacity,22);
            double anchor=viewer.NativeAnchor;
            double lower=Math.Max(0,anchor-Math.Max(0,visible.Top-band.Top));
            double upper=Math.Min(viewer.ScrollableHeight,anchor+Math.Max(0,band.Bottom-visible.Bottom));
            command.Constant(0,anchor,lower,upper,2*world.M22/pass.Area.Height);
            command.Constant(1,viewer.ScrollOwnerId);
            pass.Draws.Add(command);
            return true;
        }

        int ScrollLayer(Visual owner,Rect area,SceneDevice.Pass parent,Matrix world)
        {
            int w=Size(area.Width*parent.Sx*world.M11),h=Size(area.Height*parent.Sy*world.M22);
            insideScroll=true;
            try{return LayerSized(null,area,w,h,p=>VisualContent(owner,p,Matrix.Identity,area,1),PassKey(owner,LayerRole.ScrollStrip));}
            finally{insideScroll=false;}
        }

        // Realization or a different scroll anchor changes the row's placement, not its lettering.
        // Keep glyph coverage in local coordinates and rebuild only the small positioned quad.
        bool DrawRowText(GlyphRunDrawing text,SceneDevice.Pass pass,Matrix world,Rect clip,double opacity)
        {
            var run=text.GlyphRun;
            if(run==null)return false;
            var item=rowText.GetOrCreateValue(run);
            if(item.Density==0)item.Bounds=text.Bounds;
            if(!Valid(item.Bounds) || !Rect.Transform(item.Bounds,world).IntersectsWith(clip))return true;
            double density=Pixels(pass,world);
            if(item.Image==0 || item.Density!=density || item.Ink!=text.ForegroundBrush || !resources.Touch(item.Image)){
                var tile=item.Bounds;
                tile.Inflate(1/density,1/density);
                if(LargeTile(tile,density))return false;
                item.Image=resources.Tile(text,tile,Size(tile.Width*density),Size(tile.Height*density),null);
                item.Density=density;item.Ink=text.ForegroundBrush;item.Tile=tile;
            }
            pass.Draws.Add(SceneDevice.Quad(pass,item.Tile,world,clip,item.Image,Unit,opacity,0));
            return true;
        }
    }
}
