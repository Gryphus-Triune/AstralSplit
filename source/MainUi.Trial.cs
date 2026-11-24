using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        sealed class TrialPlan
        {
            public string Id {get;set;}
            public long Created {get;set;}
            public int Launcher {get;set;}
            public long Started {get;set;}
            public int Count {get;set;}
        }
        sealed class TrialResult
        {
            public string run {get;set;}
            public bool ready {get;set;}
            public bool baselineVerified {get;set;}
            public bool cancelledBeforeStart {get;set;}
            public string error {get;set;}
            public string detail {get;set;}
        }
        sealed class ArmedRecord {public string time {get;set;}}
        sealed class TrialStatus
        {
            internal bool Finished;
            internal string Phase;
            internal string Text;
        }
        TrialPlan trialPlan;
        string trialId,trialText,trialPhase;
        bool trialInProgress,checkingTrial,trialStopSent,trialRecoveryCheckPending;
        string TrialWorkerPhase => trialInProgress?(trialStopSent?"finishing":trialPhase??"starting"):
            trialRecoveryCheckPending?"finishing":null;
        DispatcherTimer trialTimer;
        static string LastTrial => Path.Combine(Files.Home,"last-trial.json");
        static string TrialDir(string id) => Path.Combine(Files.Home,"timed-tests",id);
        static string TrialPaths(string id) => Path.Combine(Files.Home,"trial-plans",id+".json");

        void RestoreTrialMonitor()
        {
            if(trialPlan!=null)return;
            try{
                if(!File.Exists(LastTrial))return;
                var saved=Files.Read<TrialPlan>(LastTrial);
                if(saved==null || !Guid.TryParseExact(saved.Id,"N",out _) || saved.Created<=0 ||
                    saved.Created>DateTime.UtcNow.Ticks || saved.Count<0)return;
                trialPlan=saved;trialId=saved.Id;
                trialInProgress=!File.Exists(Path.Combine(TrialDir(trialId),"finished.json"));
                StartTrialMonitor();
            }catch(Exception ex) when(TrialFileIssue(ex)){}
        }
        static bool TrialFileIssue(Exception e) => e is IOException || e is UnauthorizedAccessException ||
            e is ArgumentException || e is InvalidOperationException || e is FormatException ||
            e is System.Security.SecurityException;

        async void BeginSelectionTrial()
        {
            if(closed || trialInProgress){if(!closed)ShowTrialCard();return;}
            ShowTrialCard();
            if(App.BrowseOnly){trialText="此窗口用于预览。退出后打开 AstralSplit.exe，即可进行真实试用。";PaintTrialCard();return;}
            if(worker!=null || promoting || stopping || restoring || recoveryPending || trialRecoveryCheckPending || !choicesLoaded){
                trialText="请在应用选择就绪、分流关闭且网络恢复完成后开始试用。";PaintTrialCard();return;
            }
            if(!await pendingSave || closed || worker!=null || promoting || TrialWorkerPhase!=null)return;
            var snapshot=committedChoices;
            if(snapshot.Length==0){trialText="请先在应用列表中勾选要试用的程序。";PaintTrialCard();return;}
            if(MessageBox.Show(Window,"对当前勾选的 "+snapshot.Length+" 个程序临时开启分流。Windows 会为独立计时器申请一次授权。60 秒后自动停止并核对网络恢复。",
                "试用分流",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK){CloseCard();return;}
            trialPlan=new TrialPlan{Id=Guid.NewGuid().ToString("N"),Count=snapshot.Length,Created=DateTime.UtcNow.Ticks};
            trialId=trialPlan.Id;trialText="正在准备独立计时器…";trialPhase="starting";
            trialStopSent=false;trialInProgress=true;PaintTrialCard();UpdateState();
            bool launched=false;
            try{
                string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","SessionWatchdog.ps1");
                string powershell=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
                if(!File.Exists(script))throw new FileNotFoundException("找不到随包的独立计时器。",script);
                Files.Write(TrialPaths(trialId),snapshot);
                Files.Write(LastTrial,trialPlan);
                var info=new ProcessStartInfo(powershell,"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \""+script+
                    "\" -Action Start -PreparedSelection -Seconds 60 -RunId "+trialId){UseShellExecute=false,CreateNoWindow=true};
                using(var launcher=Process.Start(info)){
                    launched=true;trialPlan.Launcher=launcher.Id;trialPlan.Started=launcher.StartTime.ToUniversalTime().Ticks;
                    Files.Write(LastTrial,trialPlan);
                }
            }catch(Exception ex) when(TrialFileIssue(ex)||ex is Win32Exception){
                trialText="无法启动试用。"+ex.Message;
                if(!launched){trialInProgress=false;RemoveTrialPaths(trialId);}
            }
            if(launched)StartTrialMonitor();else{PaintTrialCard();UpdateState();}
        }

        void StartTrialMonitor()
        {
            if(trialTimer==null){
                trialTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(800)};
                trialTimer.Tick+=(s,e)=>CheckTrial();
                Window.Closed+=(s,e)=>trialTimer.Stop();
            }
            trialTimer.Start();CheckTrial();
        }
        static bool LauncherAlive(TrialPlan plan)
        {
            if(plan.Launcher<=0)return false;
            try{using(var process=Process.GetProcessById(plan.Launcher))
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==plan.Started;
            }catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
        }
        static TrialStatus ReadTrial(TrialPlan plan)
        {
            string dir=TrialDir(plan.Id);
            string resultPath=Path.Combine(dir,"finished.json"),armedPath=Path.Combine(dir,"armed.json");
            if(File.Exists(resultPath)){
                var result=Files.Read<TrialResult>(resultPath);
                if(result==null || result.run!=plan.Id)throw new InvalidDataException("试用记录编号不一致。");
                if(result.cancelledBeforeStart)return new TrialStatus{Finished=true,Text="试用已提前取消，本次未开启分流。"};
                string text=result.baselineVerified?"已核对临时路由和网卡设置，网络已恢复。":
                    "尚未核实网络已恢复。请查看「连接详情」及「恢复网络」。";
                if(!result.ready)text="本次没有确认分流已启用。"+text;
                if(!String.IsNullOrWhiteSpace(result.error))text+="\n"+result.error;
                return new TrialStatus{Finished=true,Text=text};
            }
            bool ready=File.Exists(Path.Combine(dir,"ready.json"));
            if(File.Exists(armedPath)){
                var armed=Files.Read<ArmedRecord>(armedPath);
                int remaining=-1;
                if(armed!=null && DateTimeOffset.TryParse(armed.time,CultureInfo.InvariantCulture,
                    DateTimeStyles.None,out var start))remaining=Math.Max(0,60-(int)(DateTimeOffset.UtcNow-start).TotalSeconds);
                if(!LauncherAlive(plan) && DateTime.UtcNow.Ticks-plan.Created>TimeSpan.FromMinutes(4).Ticks){
                    return new TrialStatus{Finished=true,Text="试用结果未收到。请先核对网络状态；如有恢复记录，可使用「恢复网络」。"};
                }
                if(remaining<0)return new TrialStatus{Phase="unknown",Text="暂时无法读取试用倒计时。"};
                if(remaining==0)return new TrialStatus{Phase="finishing",Text="正在核对网络恢复…"};
                var path=RouterSession.StatusPath(plan.Id);
                var workerStatus=File.Exists(path)?Files.Read<SessionStatus>(path):null;
                string phase=workerStatus?.Phase;
                string text;
                switch(phase){
                    case "active" when ready: text="正在试用开始时的应用选择";break;
                    case "waiting": text="等待网络就绪";break;
                    case "reconnecting": text="正在重新连接试用线路";break;
                    case "finishing":
                    case "stopped":
                    case "error": return new TrialStatus{Phase="finishing",Text="正在核对网络恢复…"};
                    case "unknown": text="暂时无法确认试用线路";break;
                    default:
                        phase=ready?"unknown":"starting";
                        text=ready?"暂时读不到当前试用状态":"独立计时器已就绪，正在启动分流";
                        break;
                }
                return new TrialStatus{Phase=phase,Text=text+" · 剩余 "+remaining+" 秒"};
            }
            bool expired=DateTime.UtcNow.Ticks-plan.Created>TimeSpan.FromSeconds(5).Ticks;
            if(expired && !LauncherAlive(plan))
                return new TrialStatus{Finished=true,Text="试用未能启动。请检查是否取消了授权，或是否已有分流会话。"};
            return new TrialStatus{Phase="starting",Text="等待 Windows 授权和独立计时器。"};
        }
        async void CheckTrial()
        {
            if(checkingTrial || trialPlan==null || closed)return;
            checkingTrial=true;var plan=trialPlan;
            try{
                bool stopRequested=trialStopSent;
                var result=await Task.Run(()=>{
                    string folder=TrialDir(plan.Id), flag=Path.Combine(folder,"stop.request");
                    if(stopRequested && Directory.Exists(folder) && !File.Exists(flag))File.WriteAllText(flag,"");
                    return ReadTrial(plan);
                });
                if(closed || trialPlan!=plan)return;
                string nextText=trialStopSent && !result.Finished?"已请求提前结束，正在核对网络恢复…":result.Text;
                bool changed=trialPhase!=result.Phase || trialText!=nextText;
                trialPhase=result.Phase;trialText=nextText;
                if(result.Finished){
                    trialTimer.Stop();RemoveTrialPaths(plan.Id);
                    if(trialInProgress){
                        trialInProgress=false;trialRecoveryCheckPending=true;
                        // Discard any pending journal read started during the trial.
                        ++workerExitRevision;
                        nextNetwork=DateTime.MinValue;UpdateNetwork();
                    }
                }
                if(changed || result.Finished)UpdateState();
                trialEntry.Content=trialInProgress?"查看试用进度":"查看上次试用";
                if(viewingTrial)PaintTrialCard();
            }catch(Exception ex) when(TrialFileIssue(ex)||ex is Win32Exception){
                if(closed || trialPlan!=plan)return;
                trialPhase="unknown";trialText="暂时无法读取试用记录。";
                UpdateState();if(viewingTrial)PaintTrialCard();
            }finally{checkingTrial=false;}
        }
        static void RemoveTrialPaths(string id){try{File.Delete(TrialPaths(id));}catch(Exception ex) when(TrialFileIssue(ex)){}}
        void RequestTrialStop()
        {
            if(!trialInProgress || trialId==null || trialStopSent)return;
            try{
                string folder=TrialDir(trialId);
                if(Directory.Exists(folder))File.WriteAllText(Path.Combine(folder,"stop.request"),"");
                trialStopSent=true;trialText="已请求提前结束，正在核对网络恢复…";
                PaintTrialCard();UpdateState();CheckTrial();
            }catch(Exception ex) when(TrialFileIssue(ex)){trialText=ex.Message;PaintTrialCard();UpdateState();}
        }
        void ShowTrialCard(){viewingTrial=true;cardPath=null;connectionCard.Visibility=Visibility.Visible;PaintTrialCard();}
        void PaintTrialCard()
        {
            cardTitle.Text="60 秒分流试用";
            cardSummary.Text=trialText??"等待开始";
            cardBody.Text=trialPlan==null?"独立计时器到期后撤销本次临时路由。":
                trialPlan.Count+" 个已选程序\n"+ClockTime(trialPlan.Created)+" 发起\n关闭本窗口后，已就绪的独立计时器继续运行。";
            cardProbe.Visibility=cardPage.Visibility=Visibility.Collapsed;cardTrial.Visibility=Visibility.Visible;
            cardTrial.Content=trialInProgress?"提前结束":"试用当前选择";
            cardTrial.IsEnabled=!App.BrowseOnly && !(trialInProgress && trialStopSent);
        }
    }
}
