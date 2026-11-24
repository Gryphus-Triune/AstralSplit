using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    internal sealed class SceneRibbonData
    {
        internal FlowBatch.RibbonVertex[] Points;
        internal float[] View,Widths,Angles,Table;
        internal uint[] Colors;
        internal int W,H,Stars;
        internal static SceneRibbonData Curves(FlowBatch.RibbonVertex[] vertices,int count,uint[] colors,float[] widths,Rect b,int w,int h,Point blue,Point red){
            var points=new FlowBatch.RibbonVertex[count];Array.Copy(vertices,points,count);
            return new SceneRibbonData{Points=points,W=w,H=h,Colors=(uint[])colors.Clone(),Widths=(float[])widths.Clone(),
                View=new[]{(float)b.X,(float)b.Y,(float)(w/b.Width),(float)(h/b.Height),(float)blue.X,(float)blue.Y,(float)red.X,(float)red.Y}};
        }
        internal static SceneRibbonData Orbit(float[] table,float[] angles,int stars,float[] view,uint[] colors,float[] widths,int w,int h){
            return new SceneRibbonData{W=w,H=h,Table=table,Angles=(float[])angles.Clone(),Stars=stars,View=(float[])view.Clone(),
                Colors=(uint[])colors.Clone(),Widths=(float[])widths.Clone()};
        }
    }
    internal sealed class SceneStreamData
    {
        internal int Kind,W,H;internal Rect Region;
        internal readonly List<float> Values=new List<float>();
        internal readonly List<uint> Colors=new List<uint>();
        internal int Count{get{return Values.Count/(Kind==0?36:12);}}
        internal void Row(double a,double b,double c,double d){Values.Add((float)a);Values.Add((float)b);Values.Add((float)c);Values.Add((float)d);}
        internal void Ink(Color c){Colors.Add((uint)(c.A<<24|c.R<<16|c.G<<8|c.B));}
    }
    public sealed partial class StarfieldLayer
    {
        SceneStreamData nativeStreamScratch;
        internal SceneStreamData StreamFrame(FlowBatch source){
            if(source!=flowBatch)return source.SceneSprites();
            if(!source.SceneRegion(out var area,out var w,out var h))return null;
            // SceneDevice copies this scratch packet before the visual walk goes on.
            var frame=nativeStreamScratch??(nativeStreamScratch=new SceneStreamData());
            frame.W=w;frame.H=h;frame.Region=area;
            frame.Values.Clear();frame.Colors.Clear();
            foreach(var stream in appStreams.Values)stream.NativeParticles(frame,streamTime);
            return frame;
        }
    }
}
