using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    internal sealed class SceneVectorData
    {
        [StructLayout(LayoutKind.Sequential)]internal struct Ink {internal int Kind,Count,Gamma,Spread;internal float X0,Y0,X1,Y1,Rx,Ry,Alpha;}
        [StructLayout(LayoutKind.Sequential)]internal struct Stop {internal float Offset;internal uint Color;}
        [StructLayout(LayoutKind.Sequential)]internal struct Segment {
            internal int Kind,Flags;internal float X1,Y1,X2,Y2,X3,Y3;
            internal Segment(int k,int flags,Point a,Point b=default(Point),Point c=default(Point)){
                Kind=k;Flags=flags;X1=(float)a.X;Y1=(float)a.Y;X2=(float)b.X;Y2=(float)b.Y;X3=(float)c.X;Y3=(float)c.Y;
            }
        }
        [StructLayout(LayoutKind.Sequential)]internal struct Info {internal float X,Y,W,H,Stroke;internal int FillRule,FirstCap,LastCap,Join;internal float Miter;internal Ink Fill,Edge;}
        internal Info Style;
        internal readonly List<Segment> Path=new List<Segment>();
        internal readonly List<Stop> Stops=new List<Stop>();
        static uint Argb(Color c){return (uint)(c.A<<24|c.R<<16|c.G<<8|c.B);}
        bool Brush(Brush brush,Rect bounds,out Ink ink){
            ink=new Ink();if(brush==null)return true;
            if((brush.Transform!=null&&!brush.Transform.Value.IsIdentity)||(brush.RelativeTransform!=null&&!brush.RelativeTransform.Value.IsIdentity))return false;
            ink.Alpha=(float)brush.Opacity;
            if(brush is SolidColorBrush solid){
                ink.Kind=1;ink.Count=1;Stops.Add(new Stop{Color=Argb(solid.Color)});return true;
            }
            if(!(brush is GradientBrush g)||g.GradientStops.Count==0||g.GradientStops.Count>48)return false;
            bool relative=g.MappingMode==BrushMappingMode.RelativeToBoundingBox;
            if(relative && !SceneGraph.Valid(bounds))return false;
            Point Coord(Point p){return relative?new Point(bounds.X+p.X*bounds.Width,bounds.Y+p.Y*bounds.Height):p;}
            Point start,end;
            if(brush is LinearGradientBrush linear){ink.Kind=2;start=Coord(linear.StartPoint);end=Coord(linear.EndPoint);}
            else if(brush is RadialGradientBrush radial){ink.Kind=3;start=Coord(radial.Center);end=Coord(radial.GradientOrigin);
                ink.Rx=(float)(radial.RadiusX*(relative?bounds.Width:1));ink.Ry=(float)(radial.RadiusY*(relative?bounds.Height:1));
                if(ink.Rx<=0 || ink.Ry<=0)return false;}
            else return false;
            ink.X0=(float)start.X;ink.Y0=(float)start.Y;ink.X1=(float)end.X;ink.Y1=(float)end.Y;
            ink.Gamma=g.ColorInterpolationMode==ColorInterpolationMode.ScRgbLinearInterpolation?1:0;ink.Spread=(int)g.SpreadMethod;
            var list=new List<Stop>();foreach(var s in g.GradientStops){
                if(s.Offset<0||s.Offset>1)return false;list.Add(new Stop{Offset=(float)s.Offset,Color=Argb(s.Color)});
            }
            // Preserve the authored order of coincident hard stops.
            for(int i=1;i<list.Count;i++){var item=list[i];int j=i;while(j>0 && list[j-1].Offset>item.Offset){list[j]=list[j-1];j--;}list[j]=item;}
            ink.Count=list.Count;Stops.AddRange(list);return true;
        }
        internal bool Encode(GeometryDrawing drawing,Rect area){
            var g=drawing.Geometry;var pen=drawing.Pen;
            if(g==null || drawing.Brush==null && (pen==null || pen.Brush==null) ||
               pen?.DashStyle?.Dashes!=null && pen.DashStyle.Dashes.Count!=0)return false;
            Style.X=(float)area.X;Style.Y=(float)area.Y;Style.W=(float)area.Width;Style.H=(float)area.Height;
            if(pen!=null){Style.Stroke=(float)pen.Thickness;Style.FirstCap=(int)pen.StartLineCap;Style.LastCap=(int)pen.EndLineCap;
                Style.Miter=(float)pen.MiterLimit;Style.Join=(int)pen.LineJoin;}
            if(!Brush(drawing.Brush,g.Bounds,out Style.Fill)||!Brush(pen?.Brush,g.GetRenderBounds(pen),out Style.Edge)||Stops.Count==0)return false;
            var path=PathGeometry.CreateFromGeometry(g);
            var matrix=path.Transform?.Value??Matrix.Identity;
            Style.FillRule=(int)path.FillRule;
            foreach(var f in path.Figures){
                Path.Add(new Segment(0,f.IsFilled?1:0,matrix.Transform(f.StartPoint)));
                foreach(var seg in f.Segments){
                    int flags=(seg.IsStroked?0:1)|(seg.IsSmoothJoin?2:0);
                    if(seg is LineSegment line)Path.Add(new Segment(1,flags,matrix.Transform(line.Point)));
                    else if(seg is QuadraticBezierSegment quad)Path.Add(new Segment(2,flags,matrix.Transform(quad.Point1),matrix.Transform(quad.Point2)));
                    else if(seg is BezierSegment curve)Path.Add(new Segment(3,flags,matrix.Transform(curve.Point1),matrix.Transform(curve.Point2),matrix.Transform(curve.Point3)));
                    else if(seg is PolyLineSegment lines){foreach(var point in lines.Points)Path.Add(new Segment(1,flags,matrix.Transform(point)));}
                    else if(seg is PolyBezierSegment curves){
                        var points=curves.Points;for(int i=0;i+2<points.Count;i+=3)Path.Add(new Segment(3,flags,matrix.Transform(points[i]),matrix.Transform(points[i+1]),matrix.Transform(points[i+2])));
                    }else if(seg is PolyQuadraticBezierSegment quads){
                        var points=quads.Points;for(int i=0;i+1<points.Count;i+=2)Path.Add(new Segment(2,flags,matrix.Transform(points[i]),matrix.Transform(points[i+1])));
                    }else if(seg is ArcSegment arc && matrix.IsIdentity){
                        Path.Add(new Segment(5,flags,arc.Point,new Point(arc.Size.Width,arc.Size.Height),
                            new Point(arc.RotationAngle,(arc.SweepDirection==SweepDirection.Clockwise?2:0)+(arc.IsLargeArc?1:0))));
                    }else return false;
                }
                Path.Add(new Segment(4,f.IsClosed?1:0,new Point()));
            }
            return Path.Count>1 && Path.Count<130000;
        }
    }
}