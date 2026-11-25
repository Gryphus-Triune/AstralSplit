using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AstralSplit
{
    internal enum MilestoneReward { None, Comet, Pulsar, IonizedNebula, Supernova, DiamondRing }

    internal static class StellarMilestone
    {
        internal const long Target=5000000000L, Limit=10000000000L;
        static readonly long[] thresholds={100000000L,500000000L,1000000000L,Target,Limit};
        static readonly string[] rewards={"双尾彗星","脉冲星","电离星云","超新星遗迹","钻石环日冕"};
        internal static int RewardCount => rewards.Length;
        internal static string RewardName(MilestoneReward reward) => rewards[(int)reward-1];
        internal static string RewardAmount(MilestoneReward reward) => Amount(thresholds[(int)reward-1]);
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

    internal sealed class MilestoneSky : MilestoneFilm
    {
        const double Duration=9.4;
        readonly Stopwatch clock=new Stopwatch();
        Rect chart=Rect.Empty,canvas=Rect.Empty;
        bool previewing;
        internal bool Earned {get;private set;}
        internal bool Bursting {get;private set;}
        internal MilestoneSky():base(FilmKind.Nova){}
        internal void SetChart(Rect bounds,Rect field){
            if(bounds==chart && field==canvas)return;
            chart=bounds;canvas=field;Paint();
        }
        internal void Unlock(bool animate){
            if(Earned)return;
            Earned=true;previewing=false;Play(animate);
        }
        internal void Preview(bool animate){
            if(Earned && Bursting && !previewing)return;
            previewing=true;Play(animate);
        }
        internal void ClearPreview(){
            if(!previewing)return;
            previewing=false;Settle();
        }
        void Play(bool animate){
            Stop();Bursting=animate && !chart.IsEmpty;
            if(Bursting){clock.Restart();UiFrames.Rendering+=FrameTick;}
            Paint();
        }
        void Stop(){
            if(!Bursting)return;
            UiFrames.Rendering-=FrameTick;clock.Stop();Bursting=false;
        }
        internal void Settle(){Stop();Paint();}
        void FrameTick(object sender,EventArgs e){
            if(clock.Elapsed.TotalSeconds>=Duration || !SystemParameters.ClientAreaAnimation)Settle();
            else Paint();
        }
        void Paint(){
            if(chart.IsEmpty || canvas.IsEmpty || (!previewing && !Earned)){Clear();return;}
            double unit=Math.Min(chart.Width*.39,Math.Max(140,chart.Height*.93));
            var center=new Point(chart.X+chart.Width*.28+.062*unit,chart.Y+chart.Height*.42+.012*unit);
            // Reduced motion previews use one representative shock front; an earned remnant is settled.
            double t=Bursting?clock.Elapsed.TotalSeconds:previewing && !SystemParameters.ClientAreaAnimation?4.9:Duration;
            Show(center,unit,canvas,t,1,Bursting);
        }
    }
}
