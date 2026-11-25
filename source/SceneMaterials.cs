using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace AstralSplit
{
    internal sealed partial class SceneGraph
    {
        static readonly Dictionary<Type,Dictionary<string,DependencyProperty>> properties=new Dictionary<Type,Dictionary<string,DependencyProperty>>();
        static readonly string[] glassParameters={"Shape","Light","Finish","Optics","Viewport","Aperture","BevelShape"};
        static object EffectValue(ShaderEffect effect,string name){
            var type=effect.GetType();
            if(!properties.TryGetValue(type,out var table)){
                properties.Add(type,table=new Dictionary<string,DependencyProperty>());
                foreach(var f in type.GetFields(BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public))
                    if(f.FieldType==typeof(DependencyProperty) && f.GetValue(null) is DependencyProperty dp)table[dp.Name]=dp;
            }
            return table.TryGetValue(name,out var key)?effect.GetValue(key):null;
        }
        static void Parameter(ref SceneDevice.Command q,int slot,object value){
            if(value is Point pt)q.Constant(slot,pt.X,pt.Y);
            else if(value is Point3D pt3)q.Constant(slot,pt3.X,pt3.Y,pt3.Z);
            else if(value is Point4D pt4)q.Constant(slot,pt4.X,pt4.Y,pt4.Z,pt4.W);
            else if(value is double f)q.Constant(slot,f);
        }
        int Sampler(ShaderEffect fx,string name,Rect area,double density){
            var brush=EffectValue(fx,name) as Brush;if(brush==null || ReferenceEquals(brush,Effect.ImplicitInput))return 0;
            if(brush is ImageBrush ib && ib.ImageSource is System.Windows.Media.Imaging.BitmapSource bm && ib.Viewbox==Unit && ib.Viewport==Unit)
                return resources.Image(bm);
            return Layer(brush,area,density,pass=>BrushRect(brush,area,pass,Matrix.Identity,area,1,0));
        }
        void Isolate(object owner,SceneDevice.Pass target,Matrix m,Rect clip,double alpha,Rect bounds,Brush mask,Geometry shape,Effect fx,Action<SceneDevice.Pass> paint){
            double density=Pixels(target,m);Rect area=Visible(bounds,m,clip);
            if(shape!=null)area.Intersect(shape.Bounds);
            if(!Valid(area))return;
            // Shader coordinates and relative opacity masks reference the unclipped input.
            // Keep their domain even when the parent crops a thin optical strip.
            if(fx!=null || mask!=null)area=bounds;
            var stage=fx is AccretionProjection?SceneDevice.Stage.Projection:SceneDevice.Stage.Layers;
            int image=Layer(null,area,density,paint,PassKey(owner,LayerRole.EffectInput),stage);
            if(image==0)return;
            if(fx is AccretionProjection plane && mask==null && shape==null){
                var quad=SceneDevice.Quad(target,area,m,clip,image,Unit,alpha,17);
                var f=plane.NativeForward;if(f.M44==0)f=Matrix3D.Identity;
                quad.Constant(0,f.M11,f.M21,0);quad.Constant(1,f.M12,f.M22,0);quad.Constant(2,f.M14,f.M24,1);
                quad.Auxiliary=plane.NativeMasked?Sampler(plane,"Exposure",area,density):0;quad.Constant(3,quad.Auxiliary!=0?1:0);
                target.Draws.Add(quad);return;
            }
            if(fx is ShaderEffect shader){
                var input=image;image=Layer(null,area,density,pass=>{
                    var q=SceneDevice.Quad(pass,area,Matrix.Identity,area,input,Unit,1,0);
                    if(shader is AccretionProjection projection){
                        q.Material=6;Parameter(ref q,0,EffectValue(shader,"First"));Parameter(ref q,1,EffectValue(shader,"Second"));
                        Parameter(ref q,2,EffectValue(shader,"Divisor"));
                        q.Auxiliary=projection.NativeMasked?Sampler(shader,"Exposure",area,density):0;q.Constant(3,q.Auxiliary!=0?1:0);
                    }else if(shader is GlassEffect glass){
                        q.Auxiliary=Sampler(shader,"Profile",area,density);q.Atlas=Sampler(shader,"Atlas",area,density);
                        q.Material=glass.NativeProjected?4:3;
                        for(int i=0;i<glassParameters.Length;i++)Parameter(ref q,i,EffectValue(shader,glassParameters[i]));
                    }else if(shader is OpticalSurface){
                        q.Material=5;Parameter(ref q,0,EffectValue(shader,"Shape"));Parameter(ref q,1,EffectValue(shader,"Focus"));
                        Parameter(ref q,3,EffectValue(shader,"Wave"));
                    }else if(shader is GateOptics optics){
                        q.Material=2;SetLensParameters(ref q,optics);
                    }else if(shader is CaptionGlassShader){
                        q.Material=10;q.Auxiliary=Sampler(shader,"Atlas",area,density);Parameter(ref q,0,EffectValue(shader,"Shape"));
                        Parameter(ref q,1,EffectValue(shader,"Region"));
                    }else if(shader is DustExposure){
                        q.Material=11;q.Auxiliary=Sampler(shader,"Sectors",area,density);q.Atlas=Sampler(shader,"Rim",area,density);
                    }else throw new NotSupportedException("Unmapped WPF shader "+shader.GetType().Name);
                    pass.Draws.Add(q);
                },PassKey(owner,LayerRole.EffectOutput),stage);
            }
            if(mask!=null){
                int maskImage=Layer(mask,area,density,pass=>BrushRect(mask,area,pass,Matrix.Identity,area,1,0));
                var input=image;image=Layer(null,area,density,pass=>{var q=SceneDevice.Quad(pass,area,Matrix.Identity,area,input,Unit,1,8);
                    q.Auxiliary=maskImage;pass.Draws.Add(q);},PassKey(owner,LayerRole.Masked));
            }
            int shapeImage=0;
            if(shape!=null){
                var coverage=new GeometryDrawing(Brushes.White,null,shape);
                if(LargeTile(area,density)){
                    shapeImage=Layer(null,area,density,pass=>VisibleTiles(coverage,pass,Matrix.Identity,area,1,area,shape,density),PassKey(owner,LayerRole.ShapeMask));
                }else shapeImage=resources.Tile(coverage,area,Size(area.Width*density),Size(area.Height*density),shape);
            }
            var result=SceneDevice.Quad(target,area,m,clip,image,Unit,alpha,shapeImage==0?0:8);
            result.Auxiliary=shapeImage;target.Draws.Add(result);
        }

        void BrushRect(Brush brush,Rect rect,SceneDevice.Pass p,Matrix m,Rect clip,double alpha,double spin){
            if(brush==null || brush.Opacity<=0 || !Valid(rect))return;
            if(brush is VisualBrush light && sky!=null && (light.Visual==sky.MilestoneArtwork || light.Visual==sky.CoronaArtwork)){
                // These brushes use identical client-space view and destination rectangles.
                // Direct drawing retains the panel scissor and avoids an extra full-screen texture.
                var glassClip=Rect.Intersect(clip,Rect.Transform(rect,m));
                if(liveFrost && light.Visual==sky.MilestoneArtwork){
                    // Live frost already scatters the entire field. Transmit a small, sharp
                    // component of the passing shock without doubling the settled gas cloud.
                    if(sky.Milestone.Bursting)VisualNode(sky.Milestone,p,m,glassClip,alpha*light.Opacity*.28);
                }else VisualNode(light.Visual,p,m,glassClip,alpha*light.Opacity,false);
                return;
            }
            if(brush is SolidColorBrush solid){
                var color=solid.Color;if(color.A==0)return;
                double a=color.A/255d;var q=SceneDevice.Quad(p,rect,m,clip,0,Unit,alpha*brush.Opacity,1);
                q.Constant(0,color.R/255d*a,color.G/255d*a,color.B/255d*a,a);p.Draws.Add(q);return;
            }
            if(brush is BitmapCacheBrush cache && cache.Target!=null){
                var area=Bounds(cache.Target);if(!Valid(area))return;
                var tile=VisualLayer(cache.Target,area,Pixels(p,m));
                p.Draws.Add(SceneDevice.Quad(p,rect,m,clip,tile,Unit,alpha*brush.Opacity,0,spin));return;
            }
            if(brush is ImageBrush tiled && tiled.TileMode!=TileMode.None && tiled.ImageSource is System.Windows.Media.Imaging.BitmapSource tiledImage &&
                tiled.Stretch==Stretch.Fill && tiled.Viewbox==Unit && tiled.ViewboxUnits==BrushMappingMode.RelativeToBoundingBox &&
                (tiled.Transform==null || tiled.Transform.Value.IsIdentity) && (tiled.RelativeTransform==null || tiled.RelativeTransform.Value.IsIdentity)){
                var tile=tiled.ViewportUnits==BrushMappingMode.Absolute?tiled.Viewport:Fraction(tiled.Viewport,rect);
                if(!Valid(tile))return;
                var texels=new Rect((rect.X-tile.X)/tile.Width,(rect.Y-tile.Y)/tile.Height,rect.Width/tile.Width,rect.Height/tile.Height);
                var q=SceneDevice.Quad(p,rect,m,clip,resources.Image(tiledImage),texels,alpha*tiled.Opacity,12);
                q.Constant(0,tiled.TileMode==TileMode.FlipX || tiled.TileMode==TileMode.FlipXY?1:0,tiled.TileMode==TileMode.FlipY || tiled.TileMode==TileMode.FlipXY?1:0);
                p.Draws.Add(q);return;
            }
            if(brush is TileBrush tileBrush && tileBrush.TileMode==TileMode.None){
                var ib=brush as ImageBrush;var vb=brush as VisualBrush;var db=brush as DrawingBrush;
                var drawing=db?.Drawing??(ib?.ImageSource as DrawingImage)?.Drawing;
                Rect space=Rect.Empty;
                if(drawing!=null)space=drawing.Bounds;
                else if(vb?.Visual!=null)space=Bounds(vb.Visual);
                else if(ib?.ImageSource!=null)space=new Rect(0,0,ib.ImageSource.Width,ib.ImageSource.Height);
                var view=tileBrush.Viewbox;
                if(tileBrush.ViewboxUnits==BrushMappingMode.RelativeToBoundingBox){
                    if(!Valid(space))return;view=Fraction(view,space);
                }
                if(!Valid(view))return;
                var face=tileBrush.ViewportUnits==BrushMappingMode.RelativeToBoundingBox?Fraction(tileBrush.Viewport,rect):tileBrush.Viewport;
                if(!Valid(face))return;
                var transform=brush.Transform?.Value??Matrix.Identity;
                if(!transform.IsIdentity)m=Join(transform,m);
                if(brush.RelativeTransform!=null && !brush.RelativeTransform.Value.IsIdentity){
                    var b=Map(rect,Unit);b.Append(brush.RelativeTransform.Value);b.Append(Map(Unit,rect));m=Join(b,m);
                }
                Rect localClip=Rect.Intersect(rect,face);
                if(!Valid(localClip))return;
                if(Axis(m))clip.Intersect(Rect.Transform(localClip,m));if(!Valid(clip))return;
                var destination=face;
                if(tileBrush.Stretch!=Stretch.Fill){
                    double sx=face.Width/view.Width,sy=face.Height/view.Height;
                    double scale=tileBrush.Stretch==Stretch.Uniform?Math.Min(sx,sy):tileBrush.Stretch==Stretch.UniformToFill?Math.Max(sx,sy):1;
                    double w=view.Width*scale,h=view.Height*scale;
                    double dx=tileBrush.AlignmentX==AlignmentX.Left?0:tileBrush.AlignmentX==AlignmentX.Center?(face.Width-w)/2:face.Width-w;
                    double dy=tileBrush.AlignmentY==AlignmentY.Top?0:tileBrush.AlignmentY==AlignmentY.Center?(face.Height-h)/2:face.Height-h;
                    destination=new Rect(face.X+dx,face.Y+dy,w,h);
                }
                double density=Pixels(p,m)*Math.Max(destination.Width/view.Width,destination.Height/view.Height);
                int id=0;Rect uv=Unit;
                if(drawing!=null){
                    id=Layer(drawing,view,density,pass=>Draw(drawing,pass,Matrix.Identity,view,1,0));
                }else if(vb?.Visual!=null){
                    if(vb.Visual==sky?.NativeTransmission){
                        var full=new Rect(sky.RenderSize);if(!Valid(full))return;
                        id=NativeSky(full,device.Width/full.Width);
                        uv=new Rect((view.X-full.X)/full.Width,(view.Y-full.Y)/full.Height,view.Width/full.Width,view.Height/full.Height);
                    }else id=VisualLayer(vb.Visual,view,density);
                }else if(ib?.ImageSource!=null && Valid(space)){
                    id=resources.Image(ib.ImageSource);
                    uv=new Rect((view.X-space.X)/space.Width,(view.Y-space.Y)/space.Height,view.Width/space.Width,view.Height/space.Height);
                }
                if(id!=0)p.Draws.Add(SceneDevice.Quad(p,destination,m,clip,id,uv,alpha*brush.Opacity,0,spin));
                return;
            }
            var gd=new GeometryDrawing(brush,null,new RectangleGeometry(rect));
            Bitmap(gd,p,m,clip,alpha,rect,null);
        }
        int VisualLayer(Visual v,Rect view,double density){
            if(!traversing.Add(v))throw new NotSupportedException("Cyclic visual brush graph at "+v.GetType().Name);
            try{return Layer(v,view,density,pass=>VisualNode(v,pass,Matrix.Identity,view,1,false));}
            finally{traversing.Remove(v);}
        }
    }
}
