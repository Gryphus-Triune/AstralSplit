using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Border connectionCard;
        TextBlock cardTitle, cardSummary, cardBody;
        Button cardTrial, cardProbe, cardPage, trialEntry;
        Button endTrial;
        int connectionPage;
        string cardPath, observationSession, probeMessage;
        string DiagnosticSession => trialInProgress ? (TrialWorkerPhase=="active"?trialId:null) :
            worker != null && !restoring && !stopping ? sessionId : null;
        bool viewingTrial, probing, observedRatesReady;
        RouteObservationSnapshot observation;
        readonly Dictionary<string,double> observedRates = new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);

        void BindConnectionControls()
        {
            connectionCard=Find<Border>("ConnectionCard");
            cardTitle=Find<TextBlock>("ConnectionCardTitle"); cardSummary=Find<TextBlock>("ConnectionCardSummary");
            cardBody=Find<TextBlock>("ConnectionCardBody");
            cardTrial=Find<Button>("CardTrial");
            cardProbe=Find<Button>("CardExport"); trialEntry=Find<Button>("TrialEntry");
            endTrial=Find<Button>("EndTrial");
            endTrial.Click+=(s,e)=>RequestTrialStop();
            Find<Button>("CloseConnectionCard").Click+=(s,e)=>CloseCard();
            cardPage=Find<Button>("CardPage");
            cardPage.Click+=(s,e)=>{connectionPage++;PaintConnectionCard();};
            cardTrial.Click+=(s,e)=>{
                if(viewingTrial && trialInProgress)RequestTrialStop();
                else BeginSelectionTrial();
            };
            trialEntry.Click+=(s,e)=>{
                if(trialId!=null){ShowTrialCard();return;}
                BeginSelectionTrial();
            };
            cardProbe.Click+=(s,e)=>ChooseProbe();
            AppRow pressed=null;
            Point origin=new Point();
            applications.PreviewMouseLeftButtonDown+=(s,e)=>{
                pressed=ConnectionRow(e.OriginalSource as DependencyObject);
                origin=e.GetPosition(applications);
            };
            applications.AddHandler(UIElement.MouseLeftButtonUpEvent,new MouseButtonEventHandler((s,e)=>{
                var row=pressed; pressed=null;
                if(row==null || row!=ConnectionRow(e.OriginalSource as DependencyObject))return;
                var delta=e.GetPosition(applications)-origin;
                if(Math.Abs(delta.X)<=SystemParameters.MinimumHorizontalDragDistance &&
                   Math.Abs(delta.Y)<=SystemParameters.MinimumVerticalDragDistance)ShowAppConnections(row.Path);
            }),true);
            applications.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Enter && applications.SelectedItem is AppRow row &&
                    ConnectionRow(e.OriginalSource as DependencyObject)!=null){ShowAppConnections(row.Path);e.Handled=true;}
            };
            Window.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Escape && connectionCard.IsVisible){CloseCard();e.Handled=true;}
            };
            Window.IsVisibleChanged+=(s,e)=>{if(!Window.IsVisible)CloseCard();};
            if(App.BrowseOnly){
                Find<TextBlock>("CaptionTitle").Text="星云分流 · 预览";
                master.ToolTip="预览模式。退出后打开 AstralSplit.exe 即可使用分流。";
            }
            Window.Loaded+=(s,e)=>RestoreTrialMonitor();
        }

        AppRow ConnectionRow(DependencyObject source)
        {
            var item=source==null?null:ItemsControl.ContainerFromElement(applications,source) as ListBoxItem;
            if(item==null)return null;
            for(var v=source;v!=null && v!=item;v=v is Visual?VisualTreeHelper.GetParent(v):LogicalTreeHelper.GetParent(v))
                if(v is ButtonBase || v is Thumb || v is ScrollBar)return null;
            return item.DataContext as AppRow;
        }

        void ShowAppConnections(string path)
        {
            cardPath=path; viewingTrial=false;connectionPage=0;
            connectionCard.Visibility=Visibility.Visible;
            PaintConnectionCard();
        }
        void CloseCard()
        {
            if(connectionCard==null)return;
            connectionCard.Visibility=Visibility.Collapsed;
            viewingTrial=false; cardPath=null;
        }
        internal static bool FreshObservation(RouteObservationSnapshot sample,long now)
        {
            return sample!=null && sample.Available && sample.SampleUtcTicks>0 &&
                sample.SampleUtcTicks<=now && now-sample.SampleUtcTicks<=TimeSpan.TicksPerSecond*8;
        }
        bool CurrentObservation(string id)
        {
            return id!=null && observationSession==id && FreshObservation(observation,DateTime.UtcNow.Ticks);
        }

        void UpdateObservation(string id,RouteObservationSnapshot sample)
        {
            var before=observationSession==id?observation:null;
            observationSession=id;
            if(!FreshObservation(sample,DateTime.UtcNow.Ticks)){
                observation=null;observedRates.Clear();observedRatesReady=false;
            }
            else if(before==null || before.SampleUtcTicks!=sample.SampleUtcTicks){
                observation=sample;observedRates.Clear();observedRatesReady=false;
                if(before!=null){
                    var old=new Dictionary<string,RouteObservation>(StringComparer.Ordinal);
                    foreach(var p in before.Connections ?? Array.Empty<RouteObservation>())if(p.Id!=null)old[p.Id]=p;
                    double elapsed=TimeSpan.FromTicks(sample.SampleUtcTicks-before.SampleUtcTicks).TotalSeconds;
                    if(elapsed>0 && elapsed<=8){
                        observedRatesReady=true;
                        foreach(var flow in sample.Connections ?? Array.Empty<RouteObservation>()){
                            if(flow.Outlet!="astral4" || String.IsNullOrEmpty(flow.Path) || flow.Id==null)continue;
                            bool known=old.TryGetValue(flow.Id,out var prior);
                            if(!known && flow.StartedUtcTicks<before.SampleUtcTicks)continue;
                            if(known && (!String.Equals(prior.Path,flow.Path,StringComparison.OrdinalIgnoreCase) ||
                                prior.Outlet!=flow.Outlet || flow.UploadBytes<prior.UploadBytes || flow.DownloadBytes<prior.DownloadBytes))continue;
                            double up=(double)flow.UploadBytes-(known?prior.UploadBytes:0);
                            double down=(double)flow.DownloadBytes-(known?prior.DownloadBytes:0);
                            if(!known && up+down<=0)continue;
                            observedRates.TryGetValue(flow.Path,out var total);
                            observedRates[flow.Path]=total+(up+down)/elapsed;
                            if(!known && chosen.Contains(flow.Path))sky.PulseConnection(flow.Path);
                        }
                    }
                }
            }
            if(cardPath!=null && connectionCard.IsVisible && !viewingTrial)PaintConnectionCard();
        }

        string ObservedAppTraffic(string path)
        {
            if(!CurrentObservation(DiagnosticSession) || !observedRatesReady)return "等待读取";
            if(!observedRates.TryGetValue(path,out double rate))return "暂未观测到该应用的 TUN 流量";
            if(rate<=0)return "已测得 0 B/s";
            return "正在传输 · "+(rate<1?"<1 B/s":StellarTrafficView.Format(rate));
        }

        static string ClockTime(long utc)
        {
            return utc>0 && utc<=DateTime.MaxValue.Ticks?new DateTime(utc,DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"):"时间未知";
        }
        static string OutletName(string outlet)
        {
            switch(outlet){case "astral4":return "星云 TUN";case "direct4":return "直连 IPv4";case "direct6":return "直连 IPv6";default:return "出口未知";}
        }
        static string ByteLabel(long bytes)
        {
            if(bytes<1024)return bytes+" B";
            if(bytes<1048576)return (bytes/1024d).ToString("0.#")+" KiB";
            return (bytes/1048576d).ToString("0.#")+" MiB";
        }
        void PaintConnectionCard()
        {
            if(cardPath==null || viewingTrial)return;
            cardTitle.Text=byPath.TryGetValue(cardPath,out var row)?row.Name:Path.GetFileNameWithoutExtension(cardPath);
            var id=DiagnosticSession;
            bool fresh=CurrentObservation(id);
            var matches=fresh?(observation.Connections??Array.Empty<RouteObservation>()).Where(
                x=>String.Equals(x.Path,cardPath,StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x=>x.Active).ThenByDescending(x=>x.LastSeenUtcTicks).ToArray():Array.Empty<RouteObservation>();
            var body=new StringBuilder();
            bool saving=!pendingSave.IsCompleted;
            cardSummary.Text=matches.Length!=0?
                "近期观测到 "+matches.Length+" 条连接 · "+matches.Count(x=>x.Outlet=="astral4")+" 条交给星云":
                id==null?(trialInProgress?"等待试用分流就绪":
                    App.BrowseOnly?"预览模式 · 连接记录须在分流时查看":"分流已关闭 · 当前没有连接记录"):
                fresh?"等待该应用的新连接":"等待引擎提供连接记录";
            bool selected=committedChoices.Contains(cardPath,StringComparer.OrdinalIgnoreCase);
            body.AppendLine("应用选择　"+(saving?"正在保存":selected?"已保存为走星云":"未选中"));
            if(id!=null)body.AppendLine("实测 TUN 流量　"+ObservedAppTraffic(cardPath));
            if(trialInProgress && trialPlan!=null)
                body.AppendLine("本次连接按试用开始时的应用选择处理。");
            if(fresh && observation.RuleGeneration>0)
                body.AppendLine("会话规则　第 "+observation.RuleGeneration+" 版 · "+ClockTime(observation.RulesChangedUtcTicks)+" 写入");
            body.AppendLine("程序　"+cardPath);
            const int pageSize=6;
            int pages=Math.Max(1,(matches.Length+pageSize-1)/pageSize);
            connectionPage%=pages;
            cardPage.Visibility=pages>1?Visibility.Visible:Visibility.Collapsed;
            cardPage.Content=connectionPage+1<pages?"下一页":"回到首页";
            foreach(var flow in matches.Skip(connectionPage*pageSize).Take(pageSize)){
                string rule=flow.Rule=="selected"?"应用分流规则":flow.Rule=="final"?"默认规则":flow.Rule=="other"?"其他规则":"规则未知";
                body.AppendLine().Append((flow.Protocol??"").ToUpperInvariant())
                    .Append(" → ").Append(OutletName(flow.Outlet)).Append(flow.Active?" · 活动":" · 最后观测").AppendLine();
                body.Append(flow.Path).Append(" · ").Append(rule);
                if(flow.OlderThanRules)body.Append(" · 建于规则变更前");
                body.AppendLine().Append("建立 ").Append(ClockTime(flow.StartedUtcTicks)).Append(" · 最近 ").Append(ClockTime(flow.LastSeenUtcTicks));
                body.AppendLine().Append("↑ ").Append(ByteLabel(flow.UploadBytes)).Append("   ↓ ").AppendLine(ByteLabel(flow.DownloadBytes));
            }
            if(pages>1)body.AppendLine("第 "+(connectionPage+1)+" / "+pages+" 页");
            if(fresh && observation.Truncated)body.AppendLine("本次记录达到上限，仅显示部分连接。");
            if(matches.Length==0)body.AppendLine().AppendLine("短连接、共享代理、被拒绝或未被接管的连接可能没有记录。");
            body.AppendLine().Append(probeMessage??"本机出站记录只到网卡。最终公网出口可单独检测。");
            cardBody.Text=body.ToString();
            cardProbe.Visibility=Visibility.Visible;
            cardProbe.IsEnabled=!App.BrowseOnly && !probing;
            cardTrial.Visibility=Visibility.Collapsed;
        }

        void ChooseProbe()
        {
            var menu=new ContextMenu{PlacementTarget=cardProbe,Placement=PlacementMode.Top};
            foreach(bool tun in new[]{true,false}){
                var address=tun?network?.Astral?.V4:network?.Physical4?.V4;
                string label=tun?"星云 TUN":"直连网卡";
                var item=new MenuItem{Header="检测"+label,IsEnabled=address!=null};
                item.Click+=(s,e)=>ProbeOutlet(label,address);
                menu.Items.Add(item);
            }
            menu.IsOpen=true;
        }
        async void ProbeOutlet(string label,IPAddress address)
        {
            if(App.BrowseOnly || probing || address==null)return;
            if(MessageBox.Show(Window,"由 AstralSplit.exe 绑定"+label+"，向 api4.ipify.org 发送一次 HTTPS 请求以获取公网 IPv4。结果只代表此请求。",
                "检测公网出口",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
            probing=true;probeMessage="正在检测"+label+"…";PaintConnectionCard();
            try{
                var result=await Task.Run(()=>ReadProbe(address));
                probeMessage="公网探测　"+DateTime.Now.ToString("HH:mm:ss")+" · AstralSplit.exe · "+label+" → "+result;
            }catch(Exception ex) when(ex is WebException || ex is IOException || ex is InvalidOperationException || ex is SocketException){
                probeMessage="本次 "+label+" 检测未完成。"+ex.Message;
            }finally{probing=false;if(!closed)PaintConnectionCard();}
        }
        static string ReadProbe(IPAddress address)
        {
            var request=(HttpWebRequest)WebRequest.Create("https://api4.ipify.org/");
            request.Proxy=null;request.AllowAutoRedirect=false;request.KeepAlive=false;
            request.Timeout=6000;request.ReadWriteTimeout=6000;
            request.ConnectionGroupName=Guid.NewGuid().ToString("N");
            var service=request.ServicePoint;
            service.BindIPEndPointDelegate=(point,remote,retry)=>new IPEndPoint(address,0);
            try{
                using(var response=request.GetResponse())
                using(var input=new StreamReader(response.GetResponseStream())){
                    var buf=new char[65];int n=input.ReadBlock(buf,0,buf.Length);
                    if(n<1 || n>=buf.Length || !IPAddress.TryParse(new string(buf,0,n).Trim(),out var ip) ||
                       ip.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("收到的内容并非 IPv4 地址。");
                    return ip.ToString();
                }
            }finally{service.BindIPEndPointDelegate=null;service.CloseConnectionGroup(request.ConnectionGroupName);}
        }
    }
}
