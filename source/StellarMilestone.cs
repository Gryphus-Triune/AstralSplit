using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    internal static class StellarMilestone
    {
        internal const long Target=5000000000L, Limit=10000000000L;
        static readonly long[] thresholds={100000000L,500000000L,1000000000L,Target,Limit};
        static readonly string[] rewards={"双尾彗星","脉冲星","电离星云","超新星遗迹","钻石环日冕"};
        internal static int Stage(long bytes){
            int stage=0;while(stage<thresholds.Length && bytes>=thresholds[stage])stage++;return stage;
        }
        internal sealed class TransferRecord {
            public string Run {get;set;}
            public long Bytes {get;set;}
        }
        internal static string RunId(int pid,long start){
            return pid.ToString("X",System.Globalization.CultureInfo.InvariantCulture)+"-"+start.ToString("X",System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static string CurrentRun(){
            using(var p=Process.GetCurrentProcess())return RunId(p.Id,p.StartTime.ToUniversalTime().Ticks);
        }
        static string Counter(string home){return Path.Combine(home,"stellar-run-transfer.json");}
        internal static long Read(string home,string run){
            try {
                var record=Files.Read<TransferRecord>(Counter(home));
                return record!=null && record.Run==run?Math.Max(0,record.Bytes):0;
            }catch(FileNotFoundException){return 0;}catch(DirectoryNotFoundException){return 0;}
        }
        internal static void Write(string home,string run,long bytes){
            Files.Write(Counter(home),new TransferRecord{Run=run,Bytes=bytes});
        }
        internal static string Label(long bytes)
        {
            int stage=Stage(bytes);
            return stage==thresholds.Length?Amount(bytes)+" · "+rewards[stage-1]:
                Amount(bytes)+" / "+Amount(thresholds[stage])+" · "+rewards[stage];
        }
        static string Amount(long bytes){
            if(bytes>=1000000000)return (bytes/1e9).ToString("0.##")+" GB";
            if(bytes>=1000000)return (bytes/1e6).ToString("0.#")+" MB";
            return (bytes/1000d).ToString("N0")+" KB";
        }
    }

    internal sealed class MilestoneSky : DrawingVisual
    {
        static readonly Brush blue=Glow(122,183,255),amethyst=Glow(177,133,230),coral=Glow(255,155,112),
            pearl=Glow(255,245,216),white=Glow(250,252,255);
        static readonly Drawing cloud=MakeCloud();
        readonly Stopwatch clock=new Stopwatch();
        Rect chart=Rect.Empty;
        internal bool Earned {get;private set;}
        internal bool Bursting {get;private set;}
        static Brush Glow(byte r,byte g,byte b){
            var v=new RadialGradientBrush();
            v.GradientStops.Add(new GradientStop(Color.FromRgb(r,g,b),0));
            v.GradientStops.Add(new GradientStop(Color.FromArgb(124,r,g,b),.33));
            v.GradientStops.Add(new GradientStop(Color.FromArgb(0,r,g,b),1));v.Freeze();return v;
        }
        static Drawing MakeCloud(){
            var art=new DrawingGroup();var r=new Random(1001);
            using(var d=art.Open()){
                // Two ragged, overlapping shells leave a dark cavity and a bright stellar remnant.
                for(int i=0;i<105;i++){
                    double a=r.NextDouble()*Math.PI*2,b=.67+r.NextDouble()*.4;
                    double x=43*Math.Cos(a)*b,y=17*Math.Sin(a)*b-.17*x+3*Math.Sin(3*a);
                    double z=4+12*r.NextDouble();
                    d.PushOpacity(.11+.25*r.NextDouble());
                    d.DrawEllipse(i%6==0?pearl:x>0?blue:i%3==0?coral:amethyst,null,new Point(x,y),1.3*z,z);d.Pop();
                }
                for(int i=0;i<19;i++){
                    double x=(r.NextDouble()-.5)*93,y=(r.NextDouble()-.5)*37-.15*x,z=.5+1.4*r.NextDouble();
                    d.DrawEllipse(pearl,null,new Point(x,y),z*2,z*2);
                }
                d.DrawEllipse(blue,null,new Point(-1,0),7,7);d.DrawEllipse(white,null,new Point(-1,0),2.3,2.3);
            }
            art.Freeze();return art;
        }
        internal Size Surface {get;set;}
        internal readonly DrawingGroup ForegroundBlast=new DrawingGroup();
        internal void SetChart(Rect value){chart=value;if(Earned)Draw();}
        internal void Unlock(bool animate){
            if(Earned)return;Earned=true;
            if(animate && !chart.IsEmpty){Bursting=true;clock.Restart();UiFrames.Rendering+=Frame;}
            Draw();
        }
        internal void Settle(){if(!Bursting)return;Bursting=false;clock.Stop();UiFrames.Rendering-=Frame;Draw();}
        void Frame(object sender,EventArgs e){if(clock.Elapsed.TotalMilliseconds>=1580)Settle();else Draw();}
        void Draw(){
            using(var shell=ForegroundBlast.Open())using(var d=RenderOpen()){
                if(!Earned || chart.IsEmpty)return;
                var at=new Point(chart.X+chart.Width*.28,chart.Y+chart.Height*.42);
                if(Bursting && Surface.Width>0){
                    double wave=UiMotion.Unit(clock.Elapsed.TotalMilliseconds/1300);
                    double radius=Math.Max(1,Math.Sqrt(Surface.Width*Surface.Width+Surface.Height*Surface.Height)*(1-Math.Pow(1-wave,2)));
                    shell.PushOpacity(.62*Math.Sin(Math.PI*wave)*(1-wave));
                    shell.DrawEllipse(null,new Pen(Brushes.PapayaWhip,1.1+3*(1-wave)),at,radius,radius);
                    shell.Pop();
                }
                double s=Math.Min(1.23,Math.Min(chart.Width/570,chart.Height/55));
                d.PushTransform(new TranslateTransform(at.X,at.Y));d.PushTransform(new ScaleTransform(s,s));
                double t=Bursting?UiMotion.Unit(clock.Elapsed.TotalMilliseconds/1580):1;
                d.PushOpacity(UiMotion.Unit((t-.16)/.68));d.DrawDrawing(cloud);d.Pop();
                if(Bursting){
                    double flash=t<.105?t/.105:Math.Exp(-5.1*(t-.105));
                    d.PushOpacity(flash);d.DrawEllipse(coral,null,new Point(),92,46);
                    d.DrawEllipse(pearl,null,new Point(),55,53);d.DrawEllipse(white,null,new Point(),78,3.4);
                    d.DrawEllipse(white,null,new Point(),3.2,42);d.DrawEllipse(white,null,new Point(),11,11);d.Pop();
                    double wave=UiMotion.Unit((t-.09)/.91);
                    for(int i=0;i<2;i++){
                        double u=UiMotion.Unit(wave*1.32-i*.16),rad=3+106*(1-Math.Pow(1-u,2));
                        d.PushOpacity(Math.Sin(Math.PI*u)*Math.Pow(1-u,.85));
                        d.DrawEllipse(null,new Pen(i==0?Brushes.Bisque:Brushes.LightSkyBlue,2.3-1.55*u),new Point(),rad,rad*(.80+i*.08));d.Pop();
                    }
                    for(int i=0;i<40;i++){
                        double a=i*2.39996,r=(10+((i*37)%68))*(1-Math.Pow(1-wave,2));
                        var p=new Point(Math.Cos(a)*r,Math.Sin(a)*r*.82);
                        d.PushOpacity((1-wave)*Math.Min(1,wave*9));d.DrawEllipse(i%3==0?blue:pearl,null,p,2.4,2.4);d.Pop();
                    }
                }
                d.Pop();d.Pop();
            }
        }
    }
}
