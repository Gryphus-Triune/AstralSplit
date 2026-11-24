using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        readonly Dictionary<string, AppRow> starIcons = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> starIconRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Threading.SemaphoreSlim pigmentSlots=new System.Threading.SemaphoreSlim(3);
        TrafficMonitor trafficMonitor = new TrafficMonitor();
        StellarTrafficView trafficView;
        Border mapViewport;
        Grid statusCard;
        GateGlassPanel statusGlass,listGlass;
        TextBlock trafficDetail;
        string recentStellarPath;
        bool mapLayoutDirty, samplingTraffic, awaitingMapLayout;
        double stellarCoreScale, restingChartHeight, expandedChartHeight;
        DateTime nextTraffic;
        DateTime nextMilestone;
        bool checkingMilestone;
        readonly string milestoneRun=StellarMilestone.CurrentRun();
        long milestoneBytes;

        void InitializeStellar()
        {
            trafficView = Find<StellarTrafficView>("StellarTraffic");
            trafficDetail = Find<TextBlock>("TrafficDetail");
            mapViewport = Find<Border>("StellarViewport");
            statusCard = Find<Grid>("StatusCard");
            statusGlass=Find<GateGlassPanel>("StatusGlass");listGlass=Find<GateGlassPanel>("ListGlass");
            BindObservatory();
            Find<Image>("StellarInteraction").Source = new DrawingImage(sky.InteractionDrawing);
            sky.ChartRevealChanged += ApplyStellarHeight;
            surface.SizeChanged += (s,e) => SizeStellarViewport();
            statusCard.SizeChanged += (s,e) => SizeStellarViewport();
            noticeReveal.SizeChanged += (s,e) => SizeStellarViewport();
            mapViewport.SizeChanged += (s,e) => InvalidateStellarBounds();
            SizeStellarViewport();
            PresentTraffic(null);
            Window.IsVisibleChanged += (s,e) => { if (!Window.IsVisible){ResetStellarTraffic();sky.Milestone.Settle();} else nextMilestone=DateTime.MinValue; };
            Window.StateChanged += (s,e) => { if (Window.WindowState == WindowState.Minimized && !ProjectingMinimizedWindow) ResetStellarTraffic(); };
            Window.Closed += (s,e) => { StopMapLayout(); sky.ChartRevealChanged -= ApplyStellarHeight; };
        }

        void SizeStellarViewport()
        {
            if(zenTravel || zenChangingHost)return;
            InvalidateStellarBounds();
            double h = surface.ActualHeight, w = surface.ActualWidth;
            if (w < 1 || h < 1) return;
            bool compact = w < 690;
            trafficView.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            mode.FontSize = compact ? 23 : 28;
            double available = h - 52 - windowBody.Margin.Top - windowBody.Margin.Bottom -
                statusCard.ActualHeight - noticeReveal.ActualHeight;
            double preferred = Math.Min(212, Math.Max(108, 110 + (h - 555) * 0.48));
            restingChartHeight = Math.Max(0, Math.Floor(Math.Min(available - 210, preferred)));
            double chartWidth = w - windowBody.Margin.Left - windowBody.Margin.Right;
            stellarCoreScale = StarfieldLayer.FitCore(chartWidth, restingChartHeight);
            double diameter = Math.Ceiling(2 * StarfieldLayer.OuterRingRadius * stellarCoreScale) + 4;
            double desired = available - diameter >= 230 ? diameter + 18 : diameter;
            // Leave the search, sort controls and at least one complete application row available.
            expandedChartHeight = stellarCoreScale == 0 ? restingChartHeight :
                Math.Max(restingChartHeight, Math.Min(desired, Math.Floor(available - 158)));
            ApplyStellarHeight();
        }

        void ApplyStellarHeight()
        {
            double height = restingChartHeight + (expandedChartHeight - restingChartHeight) * sky.ChartReveal;
            if (mapViewport.Height == height) return;
            InvalidateStellarBounds();
            mapViewport.Height = height;
        }

        void InvalidateStellarBounds(){
            mapLayoutDirty=true;
            if(awaitingMapLayout || surface==null || closed)return;
            awaitingMapLayout=true;
            surface.LayoutUpdated+=UpdateStellarBounds;
        }
        void StopMapLayout(){
            if(!awaitingMapLayout)return;
            awaitingMapLayout=false;surface.LayoutUpdated-=UpdateStellarBounds;
        }
        void UpdateStellarBounds(object sender, EventArgs e)
        {
            if (zenTravel || zenChangingHost || !mapLayoutDirty || !mapViewport.IsArrangeValid || !sky.IsArrangeValid ||
                mapViewport.FindCommonVisualAncestor(sky) == null) return;
            mapLayoutDirty = false;StopMapLayout();
            var bounds = mapViewport.TransformToVisual(sky).TransformBounds(new Rect(mapViewport.RenderSize));
            double travel=expandedChartHeight-restingChartHeight;
            double expansion=travel<1?sky.ChartReveal:UiMotion.Unit((bounds.Height-restingChartHeight)/travel);
            if(travel>=1 && Math.Abs(bounds.Height-expandedChartHeight)<.6)expansion=1;
            else if(travel>=1 && Math.Abs(bounds.Height-restingChartHeight)<.6)expansion=0;
            sky.SetStellarViewport(bounds,stellarCoreScale,expansion,expandedChartHeight);
        }

        static bool PreferenceFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                ex is ArgumentException || ex is InvalidOperationException || ex is FormatException;
        }
        void UpdateStellarPhase(bool running, bool recovering)
        {
            string trialPhase=TrialWorkerPhase;
            string state = trialPhase!=null ? (trialPhase=="finishing"?"restoring":trialPhase=="reconnecting"?"waiting":trialPhase) :
                recovering || restoring || stopping || phase == "finishing" ? "restoring" :
                promoting ? "starting" : running ? "active" :
                phase == "waiting" || phase == "reconnecting" ? "waiting" :
                phase == "starting" ? "starting" : phase == "unknown" ? "unknown" : "stopped";
            sky.SetStellarPhase(state);
            ShowEclipses(state=="active");
        }

        IEnumerable<string> VisibleStellarPaths()
        {
            return chosen.OrderByDescending(p => string.Equals(p,recentStellarPath,StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p,StringComparer.OrdinalIgnoreCase).Take(StarfieldLayer.MaximumNamedStars);
        }

        void UpdateStellarApplications()
        {
            if (sky == null) return;
            var selectedRows = rows.Where(r => chosen.Contains(r.Path)).ToDictionary(r=>r.Path,StringComparer.OrdinalIgnoreCase);
            sky.SetObservationCatalog(chosen.Select(p=>new KeyValuePair<string,string>(p,selectedRows.TryGetValue(p,out var item)?item.Name:Path.GetFileNameWithoutExtension(p))));
            sky.SetSurveyCatalog(rows.Where(r=>r.Count>0 || chosen.Contains(r.Path)).Select(r=>new KeyValuePair<string,string>(r.Path,r.Name)));
            var values = choicesLoaded ? VisibleStellarPaths()
                .Select(p => new KeyValuePair<string,string>(p, selectedRows.TryGetValue(p, out var r) ? r.Name : Path.GetFileNameWithoutExtension(p))).ToArray()
                : Array.Empty<KeyValuePair<string,string>>();
            var direct = rows.Where(r=>choicesLoaded && r.Count>0 && !chosen.Contains(r.Path)).OrderByDescending(r=>r.Path==sky.returningDirect).ThenByDescending(r=>r.VisibleWindow)
                .ThenBy(r=>r.Path,StringComparer.OrdinalIgnoreCase).Take(5).Select(r=>new KeyValuePair<string,string>(r.Path,r.Name)).ToArray();
            sky.SetApplications(values,direct);
            foreach (string old in starIcons.Keys.Where(p=>!chosen.Contains(p)).ToArray()) starIcons.Remove(old);
            foreach (var row in selectedRows.Values){
                if(starIcons.TryGetValue(row.Path,out var prior)){
                    if(prior!=row){row.TintStar(prior.StarColor);starIcons[row.Path]=row;}
                }else if(starIconRequests.Add(row.Path))LoadStarColor(row);
            }
        }

        async void LoadStarColor(AppRow row)
        {
            await pigmentSlots.WaitAsync();
            try
            {
                if(closed || !chosen.Contains(row.Path))return;
                var icon = row.Icon ?? await row.IconAt(32);
                if (closed || !chosen.Contains(row.Path)) return;
                if (icon != null && StellarIconPalette.TryGetColor(icon, out var color)){sky.SetStarColor(row.Path,color);row.TintStar(color);}
                starIcons[row.Path] = row;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException ||
                ex is NotSupportedException || ex is System.Runtime.InteropServices.ExternalException || ex is UnauthorizedAccessException) { }
            finally { pigmentSlots.Release();starIconRequests.Remove(row.Path); }
        }

        void SignalStellarApp(AppRow row,bool value)
        {
            if(!value && row.Count>0)sky.returningDirect=row.Path;
            if (!UiMotion.CanAnimate(sky) || flightContentVisible == false || contrastTheme) return;
            var container = applications.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
            if (container == null || !ReferenceEquals(container.DataContext,row)) return;
            var icon = Descendants<ApplicationIcon>(container).FirstOrDefault();
            if (icon == null || !icon.IsVisible || icon.FindCommonVisualAncestor(sky)==null) return;
            sky.SignalApplication(row.Path,value,icon.TranslatePoint(new Point(icon.ActualWidth/2,icon.ActualHeight/2),sky));
        }

        void ResetStellarTraffic()
        {
            // An in-flight native read can complete after the window is shown again.
            trafficMonitor = new TrafficMonitor();
            nextTraffic = DateTime.MinValue;
            trafficView.Clear();
            PresentTraffic(null);
        }
        async void SampleStellarTraffic()
        {
            if (samplingTraffic || closed || !Window.IsLoaded || DateTime.UtcNow < nextTraffic) return;
            samplingTraffic = true;
            nextTraffic = DateTime.UtcNow.AddSeconds(1.1);
            var monitor = trafficMonitor;
            var net = network;
            string observedSession = DiagnosticSession;
            var paths = rowsActive && observedSession==null ? chosen.ToArray() : Array.Empty<string>();
            TrafficSample sample=null;
            RouteObservationSnapshot evidence=null;
            try { sample=await Task.Run(()=>{
                if(observedSession!=null)evidence=RouteDiagnostics.Read(observedSession);
                return monitor.Sample(net,paths);
            }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is System.Net.NetworkInformation.NetworkInformationException ||
                ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException) { }
            finally { samplingTraffic=false; }
            if (!closed && ReferenceEquals(monitor,trafficMonitor) && ReferenceEquals(net,network) &&
                observedSession==DiagnosticSession && Window.IsVisible && Window.WindowState!=WindowState.Minimized) {
                UpdateObservation(observedSession,evidence);
                PresentTraffic(sample);
            }
        }
        void PresentTraffic(TrafficSample sample)
        {
            var tun=sample?.TunBytesPerSecond; var uplink=sample?.UplinkBytesPerSecond;
            var id=DiagnosticSession;
            bool observing=id!=null;
            bool ratesReady=observing && CurrentObservation(id) && observedRatesReady;
            sky.MeasuredAppStreams=observing;
            var rates=observing?(ratesReady?observedRates:null):sample?.AppBytesPerSecond;
            trafficView.SetDirections(sample?.TunUpload,sample?.TunDownload,sample?.UplinkUpload,sample?.UplinkDownload);
            trafficView.Push(tun,uplink);
            foreach(var row in rows){
                double rate=0;
                if(rowsActive && row.Selected && rates!=null)rates.TryGetValue(row.Path,out rate);
                row.SetVisualRate(rate);
            }
            trafficDetail.Text = TunnelName + "网卡  " + StellarTrafficView.Format(tun) + "    上联网卡  " + StellarTrafficView.Format(uplink) +
                "（含隧道和其他流量）" + Environment.NewLine +
                "右侧蓝光 · 上传  " + StellarTrafficView.Format(sample?.TunUpload) +
                "    左侧红光 · 下载  " + StellarTrafficView.Format(sample?.TunDownload) + Environment.NewLine +
                "本次运行分流  " + StellarMilestone.Label(milestoneBytes);
            if(observing)
                trafficDetail.Text += Environment.NewLine + (ratesReady
                    ? "点击应用查看实测流量及 TCP / UDP 连接"
                    : "等待第一笔逐应用 TUN 流量读数");
            else if(rowsActive && sample?.HasTcpCoverage==true)
                trafficDetail.Text += Environment.NewLine + "已选应用 TCP 采样  " +
                    (sample.ObservedTcp==0 ? "暂无可比较的连接" : sample.ComparableTcp+" / "+sample.ObservedTcp+" 条连接可比较") +
                    (sample.IncompleteTcpTable ? "（部分连接表不可用）" : "");
            sky.SetTraffic(tun ?? -1,uplink ?? -1);
            sky.SetDiskTraffic(sample?.TunUpload,sample?.TunDownload);
            sky.SetAppTraffic(rates);
            if (!observing && sample?.NewTcpConnectionPaths != null && rowsActive)
                foreach (string path in sample.NewTcpConnectionPaths)
                    if (chosen.Contains(path)) sky.PulseConnection(path);
        }

        async void CheckMilestone()
        {
            if(closed || checkingMilestone || DateTime.UtcNow<nextMilestone)return;
            checkingMilestone=true;nextMilestone=DateTime.UtcNow.AddSeconds(4);
            try{
                long count=await Task.Run(()=>StellarMilestone.Read(Files.Home,milestoneRun));
                if(closed)return;
                milestoneBytes=count;
                sky.SetDeepStage(StellarMilestone.Stage(count));
                if(sky.Milestone.Earned || count<StellarMilestone.Target || !Window.IsVisible || Window.WindowState==WindowState.Minimized)return;
                if(windowFlight!=null || hidingToTray){nextMilestone=DateTime.UtcNow.AddMilliseconds(450);return;}
                sky.Milestone.Unlock(UiMotion.CanAnimate(sky) && !contrastTheme);
            }catch(Exception ex) when(PreferenceFailure(ex) || ex is InvalidDataException || ex is OverflowException){ }
            finally{checkingMilestone=false;}
        }
    }
}
