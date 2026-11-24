using System;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    // The reference drawing uses the 2x capture's pixels, centered on the seed.
    // Its parent supplies the same placement, breath and collapse as the stellar core.
    internal sealed class StandbyProminence
    {
        const double Tau=2*Math.PI;
        static readonly Color Ice=Color.FromRgb(142,224,255), Pearl=Color.FromRgb(248,252,255), Gold=Color.FromRgb(255,199,131);
        static readonly Brush ColdLight=Glow(Ice), WarmLight=Glow(Gold), WhiteLight=Glow(Pearl);

        internal DrawingGroup Art {get;}=new DrawingGroup();
        readonly Halo corona,center;
        readonly Ray[] rays=new Ray[32];
        readonly Loop[] loops;
        double lastPhase=double.NaN;

        internal StandbyProminence()
        {
            corona=new Halo(Art,ColdLight);
            for(int i=0;i<rays.Length;i++)rays[i]=new Ray(Art,i);
            loops=new[]{new Loop(Art,Ice,.20,-.79,45,1),new Loop(Art,Gold,.68,2.40,34,.91),
                new Loop(Art,Ice,.49,-2.25,17,.43)};
            center=new Halo(Art,WhiteLight);
            Paint(0);
        }

        internal void Paint(double phase)
        {
            phase-=Math.Floor(phase);
            if(phase==lastPhase)return;
            lastPhase=phase;
            double angle=Tau*phase,breath=.5-.5*Math.Cos(angle);
            corona.Set(new Point(),59+16*breath,49+14*breath,.13+.15*breath);
            foreach(var ray in rays)ray.Update(angle);
            foreach(var loop in loops)loop.Update(phase);
            center.Set(new Point(),13+4*breath,12+3*breath,.53+.20*breath);
        }

        static Brush Glow(Color color)
        {
            var result=new RadialGradientBrush();
            foreach(var stop in new[]{new GradientStop(color,0),new GradientStop(Alpha(color,193),.18),
                new GradientStop(Alpha(color,86),.42),new GradientStop(Alpha(color,14),.74),new GradientStop(Alpha(color,0),1)})
                result.GradientStops.Add(stop);
            result.Freeze();return result;
        }
        static Color Alpha(Color c,byte alpha){return Color.FromArgb(alpha,c.R,c.G,c.B);}
        static Color Mix(Color a,Color b,double t){return Color.FromRgb((byte)(a.R+(b.R-a.R)*t),(byte)(a.G+(b.G-a.G)*t),(byte)(a.B+(b.B-a.B)*t));}
        static Pen PenFor(Brush brush,double width)
        {
            var pen=new Pen(brush,width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
            pen.Freeze();return pen;
        }
        static Pen PenFor(Color color,double opacity,double width)
        {
            return PenFor(new SolidColorBrush(Alpha(color,(byte)Math.Round(255*opacity))),width);
        }

        sealed class Halo
        {
            readonly DrawingGroup group=new DrawingGroup();
            readonly MatrixTransform transform=new MatrixTransform();
            static readonly EllipseGeometry Circle=UnitCircle();
            static EllipseGeometry UnitCircle(){var g=new EllipseGeometry(new Point(),1,1);g.Freeze();return g;}
            internal Halo(DrawingGroup parent,Brush brush)
            {
                group.Transform=transform;
                group.Children.Add(new GeometryDrawing(brush,null,Circle));parent.Children.Add(group);
            }
            internal void Set(Point at,double rx,double ry,double opacity)
            {
                transform.Matrix=new Matrix(rx,0,0,ry,at.X,at.Y);
                group.Opacity=opacity;
            }
        }

        // Keep the ray's pen, brush, and cubic on the compositor. The changing angle
        // and length are applied to the transform; only its gentle bend changes shape.
        sealed class Ray
        {
            const double Length=30;
            readonly int index;
            readonly DrawingGroup group=new DrawingGroup();
            readonly MatrixTransform transform=new MatrixTransform();
            readonly QuadraticBezierSegment curve;
            internal Ray(DrawingGroup parent,int i)
            {
                index=i;
                var color=i%6==0?Gold:Ice;
                var gradient=new LinearGradientBrush(Alpha(color,73),Alpha(color,0),0){
                    MappingMode=BrushMappingMode.Absolute,StartPoint=new Point(),EndPoint=new Point(Length,0)};
                var geometry=new PathGeometry();var figure=new PathFigure{StartPoint=new Point(),IsFilled=false};
                curve=new QuadraticBezierSegment(new Point(Length*.5,0),new Point(Length,0),true);
                figure.Segments.Add(curve);geometry.Figures.Add(figure);
                group.Children.Add(new GeometryDrawing(null,PenFor(gradient,.65+(i%4)*.17),geometry));
                group.Transform=transform;parent.Children.Add(group);
            }
            internal void Update(double time)
            {
                double a=2.399963*index+.15*Math.Sin(time+index*.84);
                double fan=.5+.5*Math.Cos(2*(a+.66));
                double end=13+(7+16*fan)*(1+.16*Math.Sin(time+index*1.7));
                double turn=.18*Math.Sin(index+time);
                var start=new Point(5*Math.Cos(a),5*Math.Sin(a));
                var tip=new Point(end*Math.Cos(a+turn),end*Math.Sin(a+turn));
                Vector along=tip-start;
                double length=along.Length;
                var normal=new Vector(-along.Y/length,along.X/length);
                double mid=(end+5)*.5;
                var midpoint=new Point(mid*Math.Cos(a+turn*.5),mid*Math.Sin(a+turn*.5));
                double bend=2*Vector.Multiply(midpoint-start,normal);
                curve.Point1=new Point(Length*.5,bend);
                transform.Matrix=new Matrix(along.X/Length,along.Y/Length,normal.X,normal.Y,start.X,start.Y);
                group.Opacity=.45+.55*(.5+.5*Math.Cos(time+index*.83));
            }
        }

        sealed class Loop
        {
            readonly DrawingGroup group=new DrawingGroup();
            readonly Filament[] lines=new Filament[7];
            readonly Halo apex;
            readonly double offset,direction,reach,intensity;
            internal Loop(DrawingGroup parent,Color color,double offset,double direction,double reach,double intensity)
            {
                this.offset=offset;this.direction=direction;this.reach=reach;this.intensity=intensity;
                for(int j=0;j<lines.Length;j++)lines[j]=new Filament(group,j,color);
                apex=new Halo(group,color==Gold?WarmLight:ColdLight);parent.Children.Add(group);
            }
            internal void Update(double phase)
            {
                double u=phase+offset;u-=Math.Floor(u);
                double sine=Math.Sin(Math.PI*u),energy=sine*sine;
                group.Opacity=energy<.002?0:energy*intensity;
                if(energy<.002)return;
                double amplitude=reach*Math.Pow(sine,1.32);
                for(int j=0;j<lines.Length;j++){
                    var curve=new FieldLine(u,sine,j/6.0,direction,amplitude);
                    lines[j].Update(curve,u);
                }
                var crown=new FieldLine(u,sine,.83,direction,amplitude);
                apex.Set(crown.At(.54),10,8,.16);
            }
        }

        // Sample the field-line equation once per stroke. A compact frozen path
        // is shared by its luminous edge and its softer outer stroke.
        struct FieldLine
        {
            readonly double cosAngle,sinAngle,baseWidth,shear,height,bend,offset;
            internal FieldLine(double u,double sine,double depth,double direction,double amplitude)
            {
                double angle=direction+.09*sine+.04*depth;
                cosAngle=Math.Cos(angle);sinAngle=Math.Sin(angle);
                baseWidth=8+5*depth;shear=.23*amplitude;height=4+amplitude*(.68+.33*depth);
                bend=7*sine;offset=.72*depth+u*3.1;
            }
            static readonly double[] Waves=Samples(false),Cosines=Samples(true);
            const int Steps=32;
            static double[] Samples(bool cosine){
                var values=new double[Steps+1];
                for(int i=0;i<=Steps;i++)values[i]=cosine?Math.Cos(Math.PI*i/Steps):Math.Sin(Math.PI*i/Steps);
                return values;
            }
            Point At(double wave,double cosine,double twist)
            {
                double tangent=-cosine*(baseWidth+shear*wave)+wave*wave*(bend+2.1*twist);
                double outward=8+wave*height+.90*wave*twist;
                return new Point(cosAngle*outward-sinAngle*tangent,sinAngle*outward+cosAngle*tangent);
            }
            internal Point At(double s){return At(Math.Sin(Math.PI*s),Math.Cos(Math.PI*s),Math.Sin(Tau*s+offset));}
            internal void Geometry(StreamGeometry result)
            {
                double sinOffset=Math.Sin(offset),cosOffset=Math.Cos(offset);
                using(var path=result.Open()){
                    path.BeginFigure(At(0,1,sinOffset),false,false);
                    for(int i=1;i<=Steps;i++){
                        double s=Waves[i],c=Cosines[i];
                        double twist=2*s*c*cosOffset+(c*c-s*s)*sinOffset;
                        path.LineTo(At(s,c,twist),true,true);
                    }
                }
            }
            internal void Highlight(StreamGeometry result,double head)
            {
                double start=Math.Max(0,head-.11),end=Math.Min(1,head+.05);
                using(var path=result.Open()){
                    path.BeginFigure(At(start),false,false);
                    for(int i=1;i<=6;i++)path.LineTo(At(start+(end-start)*i/6),true,true);
                }
            }
        }
        sealed class Filament
        {
            readonly GeometryDrawing edge,glow,highlight;
            readonly StreamGeometry body=new StreamGeometry(),crest=new StreamGeometry();
            readonly DrawingGroup current;
            readonly Halo tip;
            readonly double lead;
            internal Filament(DrawingGroup parent,int i,Color color)
            {
                double depth=i/6.0;
                if(i%3==0){
                    glow=new GeometryDrawing(null,PenFor(color,.11,4.4),null);parent.Children.Add(glow);
                }
                edge=new GeometryDrawing(null,PenFor(Mix(color,Pearl,.15+.18*depth),.15+.38*Math.Pow(depth,1.5),.48+.38*depth),null);
                parent.Children.Add(edge);
                if(i==2 || i==6){
                    lead=i==2?.14:0;current=new DrawingGroup();parent.Children.Add(current);
                    highlight=new GeometryDrawing(null,PenFor(Mix(color,Pearl,.61),.79,1.06),null);current.Children.Add(highlight);
                    tip=new Halo(current,WhiteLight);
                }
            }
            internal void Update(FieldLine curve,double u)
            {
                if(edge.Geometry!=body){edge.Geometry=body;if(glow!=null)glow.Geometry=body;}
                curve.Geometry(body);
                if(current==null)return;
                double head=Math.Min(.999,(u*1.07+lead)%1);
                current.Opacity=Math.Sin(Math.PI*head);
                if(highlight.Geometry!=crest)highlight.Geometry=crest;
                curve.Highlight(crest,head);
                tip.Set(curve.At(head),3,2.1,.57);
            }
        }
    }
}
