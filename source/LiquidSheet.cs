using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AstralSplit
{
    public sealed class ListCardBorder : Border
    {
        public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
            typeof(SolidColorBrush), typeof(ListCardBorder), new PropertyMetadata(null, (o, e) => ((ListCardBorder)o).UpdateHighlight()));

        public SolidColorBrush Highlight { get { return (SolidColorBrush)GetValue(HighlightProperty); } set { SetValue(HighlightProperty, value); } }

        protected override void OnRenderSizeChanged(SizeChangedInfo change)
        {
            base.OnRenderSizeChanged(change);
            if (change.WidthChanged) UpdateHighlight();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == LiquidLens.GlassEnabledProperty) UpdateHighlight();
        }

        void UpdateHighlight()
        {
            var color = Highlight;
            BorderBrush = color == null || ActualWidth < 1 || !LiquidLens.GetGlassEnabled(this)
                ? (Brush)color : FadeEnds(color.Color, ActualWidth);
        }

        // Distance is measured in local DIPs so resizing only changes the length of the solid middle.
        internal static Brush FadeEnds(Color color, double length)
        {
            double reach = Math.Min(40, length / 2);
            var brush = new LinearGradientBrush {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0), EndPoint = new Point(length, 0)
            };
            for (int end = 0; end < 2; end++)
            {
                for (int i = 0; i <= 6; i++)
                {
                    double t = (end == 0 ? i : 6 - i) / 6.0;
                    double strength = 0.035 + 0.965 * t * t * (3 - 2 * t);
                    var tint = color;
                    tint.A = (byte)Math.Round(color.A * strength);
                    double offset = t * reach / length;
                    brush.GradientStops.Add(new GradientStop(tint, end == 0 ? offset : 1 - offset));
                }
            }
            brush.Freeze();
            return brush;
        }
    }

    // The bevel bends light only near the perimeter. Four narrow targets cover that area.
    public sealed class LiquidSheet : FrostedPane
    {
        public static readonly DependencyProperty CompactProperty=DependencyProperty.Register(nameof(Compact),typeof(bool),typeof(LiquidSheet),
            new PropertyMetadata(false,(o,e)=>{var pane=(LiquidSheet)o;pane.Draw();pane.Optics();}));
        public bool Compact {get{return (bool)GetValue(CompactProperty);}set{SetValue(CompactProperty,value);}}
        protected override double SurfaceRadius {get{return Compact?8:base.SurfaceRadius;}}

        readonly GlassEffect optic=GlassEffect.Available ? new GlassEffect() : null;
        readonly ContainerVisual rim=new ContainerVisual();
        readonly GlassEffect[] bevels=new GlassEffect[4];
        readonly ContainerVisual glint=new ContainerVisual();
        static readonly Point RestLight=new Point(-0.54,-0.84);
        static readonly DependencyProperty DirectionProperty=DependencyProperty.Register("Direction",typeof(Point),typeof(LiquidSheet),
            new PropertyMetadata(RestLight,(o,e)=>((LiquidSheet)o).LightDirection((Point)e.NewValue)));
        readonly MouseEventHandler pointer;
        FrameworkElement host;
        Point latest;
        bool pendingLight,returning;
        public LiquidSheet()
        {
            Coating.Children.Add(rim);Coating.Children.Add(glint);pointer=Move;
            for(int i=0;i<bevels.Length && optic!=null;i++)
            {
                bevels[i]=new GlassEffect();
                var tile=new ContainerVisual();
                if(Environment.GetEnvironmentVariable("ASTRALSPLIT_UNCACHED_RIM")!="1")tile.CacheMode=new BitmapCache(ArtworkResolution.Ratio);
                tile.Children.Add(new DrawingVisual {Effect=bevels[i]});rim.Children.Add(tile);
                glint.Children.Add(new DrawingVisual {CacheMode=new BitmapCache(ArtworkResolution.Ratio)});
            }
            Loaded+=(s,e)=>{
                host=VisualTreeHelper.GetParent(this) as FrameworkElement;
                for(var parent=host;parent!=null;parent=VisualTreeHelper.GetParent(parent) as FrameworkElement)
                    if(parent is Button || parent.Name=="ListInteractionHost" || parent.Name=="StatusCard"){host=parent;break;}
                if(host!=null){host.AddHandler(Mouse.PreviewMouseMoveEvent,pointer,true);host.MouseLeave+=Leave;}
                Optics();
            };
            Unloaded+=(s,e)=>{
                StopLight();
                if(host!=null){host.RemoveHandler(Mouse.PreviewMouseMoveEvent,pointer);host.MouseLeave-=Leave;host=null;}
            };
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo i) {base.OnRenderSizeChanged(i);Optics();}
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if((e.Property==LiquidLens.GlassEnabledProperty || e.Property==BackdropProperty) && glint!=null) Optics();
        }
        void Optics()
        {
            double w=ActualWidth,h=ActualHeight;
            if(w<1 || h<1) return;
            bool glass=LiquidLens.GetGlassEnabled(this);
            optic?.Device(this);
            double bevel=Compact?Math.Min(3,Math.Min(w,h)*.10):14;
            bool tiled=glass && optic!=null && (Compact?w>2*SurfaceRadius && h>2*SurfaceRadius:w>=80 && h>=40) && Backdrop is StarfieldLayer;
            LensSurface.Effect=glass&&!tiled?optic:null;
            LensSurface.CacheMode=null;
            rim.Opacity=glint.Opacity=tiled?1:0;
            if(tiled)
            {
                // A button fits into one perimeter pass. Large cards sample only their four edges.
                var full=new Rect(0,0,w,h);
                var sides=Compact?new[]{full}:new[]{new Rect(0,0,w,bevel),new Rect(0,h-bevel,w,bevel),
                    new Rect(0,bevel,bevel,h-2*bevel),new Rect(w-bevel,bevel,bevel,h-2*bevel)};
                for(int i=0;i<bevels.Length;i++)
                {
                    var tile=(ContainerVisual)rim.Children[i];tile.Opacity=i<sides.Length?1:0;
                    if(i>=sides.Length)continue;
                    var source=sides[i];source.Inflate(29,29);source.Intersect(full);
                    var face=(DrawingVisual)tile.Children[0];
                    tile.Offset=new Vector(source.X,source.Y);
                    var crop=sides[i];crop.Offset(-source.X,-source.Y);
                    Geometry clip;
                    if(Compact){
                        // Keep the flat middle single-coated on software and bitmap rendering paths too.
                        var inside=crop;inside.Inflate(-bevel,-bevel);
                        var band=new GeometryGroup{FillRule=FillRule.EvenOdd};
                        band.Children.Add(new RectangleGeometry(crop,SurfaceRadius,SurfaceRadius));
                        double innerRadius=SurfaceRadius-bevel;
                        band.Children.Add(new RectangleGeometry(inside,innerRadius,innerRadius));
                        clip=band;
                    }else clip=new RectangleGeometry(crop);
                    clip.Freeze();tile.Clip=clip;
                    var sampler=GlassSampling.Sampler();sampler.Drawing=RimMaterial;sampler.Viewbox=source;
                    using(var dc=face.RenderOpen())dc.DrawRectangle(sampler,null,new Rect(source.Size));
                    bevels[i].Device(this);bevels[i].Size(w,h,SurfaceRadius,bevel,source);bevels[i].Finish(0,false,true);
                }
                DrawGlints(w,h);LightDirection((Point)GetValue(DirectionProperty));
            }
            else {optic?.Size(w,h,SurfaceRadius,bevel);optic?.Finish(Compact?.25:1.05,false);}
        }
        void DrawGlints(double w,double h)
        {
            const double inset=.65;double radius=SurfaceRadius,r=radius-inset,d=r/Math.Sqrt(2);
            var color=Color.FromRgb(235,248,255);
            var horizontal=new Pen(ListCardBorder.FadeEnds(color,w),1.15);horizontal.Freeze();
            var vertical=new Pen(ListCardBorder.FadeEnds(color,h),1.15);vertical.Freeze();
            for(int i=0;i<4;i++){
                double length=i%2==0?w:h;var path=new StreamGeometry();
                using(var g=path.Open()){
                    g.BeginFigure(new Point(radius-d,radius-d),false,false);
                    g.ArcTo(new Point(radius,inset),new Size(r,r),0,false,SweepDirection.Clockwise,true,false);
                    g.LineTo(new Point(length-radius,inset),true,false);
                    g.ArcTo(new Point(length-radius+d,radius-d),new Size(r,r),0,false,SweepDirection.Clockwise,true,false);
                }
                path.Freeze();var strip=(DrawingVisual)glint.Children[i];
                var m=Matrix.Identity;m.Rotate(90*i);m.Translate(i==1||i==2?w:0,i>=2?h:0);
                strip.Transform=new MatrixTransform(m);
                using(var dc=strip.RenderOpen())dc.DrawGeometry(null,i%2==0?horizontal:vertical,path);
            }
        }
        Vector movingLightDirection;
        internal void WindowLight(Vector motion){
            movingLightDirection=motion*.045;LightDirection((Point)GetValue(DirectionProperty));
        }
        void LightDirection(Point light)
        {
            if(movingLightDirection.LengthSquared>0){var v=new Vector(light.X,light.Y)+movingLightDirection;
                if(v.LengthSquared>.001){v.Normalize();light=new Point(v.X,v.Y);}}
            if(glint.Opacity==0){optic?.SetValue(GlassEffect.LightProperty,light);return;}
            for(int i=0;i<glint.Children.Count;i++){
                double angle=i==0?-light.Y:i==1?light.X:i==2?light.Y:-light.X;
                ((DrawingVisual)glint.Children[i]).Opacity=0.10+(angle>=0?0.69:0.31)*angle*angle;
            }
        }
        void Move(object sender,MouseEventArgs e)
        {
            if(optic==null || !LiquidLens.GetGlassEnabled(this) || !UiMotion.CanAnimate(this) || ActualWidth<1 || ActualHeight<1) return;
            latest=e.GetPosition(this);
            if(!pendingLight){pendingLight=true;UiFrames.Rendering+=FlushLight;}
        }
        void StopLight()
        {
            if(pendingLight){UiFrames.Rendering-=FlushLight;pendingLight=false;}
        }
        void FlushLight(object s,EventArgs e){StopLight();if(IsVisible && ActualWidth>0 && ActualHeight>0)ApplyLight(latest);}
        internal void ApplyLight(Point p)
        {
            if(returning){
                BeginAnimation(DirectionProperty,null);returning=false;
            }
            var v=new Vector(-0.54+1.30*(p.X/ActualWidth-0.48),-0.84+0.92*(p.Y/ActualHeight-0.42));v.Normalize();
            SetValue(DirectionProperty,new Point(v.X,v.Y));
        }
        void Leave(object s,MouseEventArgs e)
        {
            StopLight();
            if(optic==null)return;
            returning=true;
            bool animate=UiMotion.CanAnimate(this);int? rate=animate?DisplayRefresh.For(this):null;
            var from=(Point)GetValue(DirectionProperty);SetValue(DirectionProperty,RestLight);
            BeginAnimation(DirectionProperty,animate?UiMotion.AtRefresh(new PointAnimation(from,RestLight,TimeSpan.FromMilliseconds(220)){FillBehavior=FillBehavior.Stop},rate):null);
        }
    }
}
