param(
    [ValidateSet('Start','Arm','Guard','Peer','CheckPredicate')][string]$Action = 'Start',
    [ValidatePattern('^[a-f0-9]{32}$')][string]$RunId = ([guid]::NewGuid().ToString('N')),
    [string]$Exe, [string]$ExpectedSha256, [ValidateRange(2,300)][int]$Seconds = 60,
    [string]$ExpectedSid, [int]$ExpectedSession = -1, [int]$PeerId, [long]$PeerTicks, [int]$ArmId, [long]$ArmTicks, [switch]$TimerOnly,
    [switch]$PreparedSelection
)
$ErrorActionPreference = 'Stop'
$toolRoot = $PSScriptRoot
$shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$localStore = Join-Path $env:LOCALAPPDATA 'AstralSplit'
$runDir = Join-Path (Join-Path $localStore 'timed-tests') $RunId
$sessionDir = Join-Path (Join-Path $localStore 'sessions') $RunId
$machineData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$machineJournal = Join-Path $machineData 'AstralSplit.Network.v2\network-recovery.json'
$process = [Diagnostics.Process]::GetCurrentProcess()
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
function Save-Record($name, $value) {
    if ($isAdmin -and !$reportIdentity) {throw 'Unprivileged report token not verified.'}
    $context=if ($reportIdentity){$reportIdentity.Impersonate()}
    try {
    $path = Join-Path $runDir $name
    $temp = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, (ConvertTo-Json -InputObject $value -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($path)) { [IO.File]::Replace($temp, $path, [NullString]::Value) } else { [IO.File]::Move($temp, $path) }
    } finally {if ($context){$context.Undo();$context.Dispose()}}
}
function Read-Json($path) { ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path)) }
function Quote-Arg([string]$value) {
    if (!$value -or $value.Contains('"') -or $value.EndsWith('\')) { throw 'Invalid command line argument.' }
    '"' + $value + '"'
}
function Check-Exe {
    $installed=[IO.Path]::GetFullPath((Join-Path (Split-Path -LiteralPath $toolRoot) 'AstralSplit.exe'))
    if (!$Exe -or $Exe -ine $installed -or $ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Specify the exact AstralSplit.exe and its expected SHA256.' }
    if ((Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Executable hash changed.' }
}
function Find-Workers {
    foreach ($entry in @(Get-CimInstance Win32_Process -Filter "Name='AstralSplit.exe'")) {
        if (!$entry.CommandLine -or $entry.CommandLine -match '(?:^|\s)--(?:session|restore)(?:\s|$)') { $entry.ProcessId }
    }
}
function Snapshot {
    $adapters = @(Get-NetAdapter -IncludeHidden)
    $astral = @($adapters | Where-Object { $_.InterfaceDescription -like 'astral-tun*' -and $_.Status -eq 'Up' })
    if ($astral.Count -ne 1) { throw 'Expected precisely one connected Astral TUN.' }
    $iface = Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex $astral[0].ifIndex
    $routes = @(Get-NetRoute -PolicyStore ActiveStore)
    $defaults = @($routes | Where-Object { $_.InterfaceIndex -eq $iface.InterfaceIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' } | ForEach-Object { '{0}|{1}|{2}' -f $_.NextHop,$_.RouteMetric,$_.Protocol } | Sort-Object)
    $capture = @($routes | Where-Object { $_.InterfaceAlias -like 'ASplit-*' })
    [uint64]$luid=0
    if ([AstralQaStop]::ConvertInterfaceIndexToLuid($iface.InterfaceIndex,[ref]$luid) -ne 0 -or !$luid) {throw 'Cannot verify Astral interface LUID.'}
    [pscustomobject]@{ Guid=[string]$astral[0].InterfaceGuid; Luid=[string]$luid; Index=[int]$iface.InterfaceIndex; Metric=[int]$iface.InterfaceMetric;
        Automatic=[string]$iface.AutomaticMetric; Forwarding=[string]$iface.Forwarding; Defaults=($defaults -join ',');
        AdapterCount=@($adapters | Where-Object { $_.Name -like 'ASplit-*' -and $_.Status -eq 'Up' }).Count;
        CaptureCount=$capture.Count; JournalCount=[int]([IO.File]::Exists($machineJournal)) }
}
function Is-Clean($baseline,$after,[bool]$workerExited,[bool]$engineExited) {
    if (!$workerExited -or !$engineExited -or !$baseline -or !$after) { return $false }
    foreach ($key in 'Guid','Luid','Index','Metric','Automatic','Forwarding','Defaults') { if ($baseline.$key -cne $after.$key) { return $false } }
    return $after.AdapterCount -eq 0 -and $after.CaptureCount -eq 0 -and $after.JournalCount -eq 0
}
if ($Action -eq 'CheckPredicate') {
    $sample = [pscustomobject]@{Guid='mock';Luid='mock-luid';Index=42;Metric=12;Automatic='Enabled';Forwarding='Enabled';Defaults='mock-route';AdapterCount=0;CaptureCount=0;JournalCount=0}
    if (!(Is-Clean $sample $sample $true $true)) { throw 'False negative.' }
    foreach ($key in 'Guid','Luid','Index','Metric','Automatic','Forwarding','Defaults','AdapterCount','CaptureCount','JournalCount') {
        $other=$sample.PSObject.Copy(); $other.$key=1; if (Is-Clean $sample $other $true $true) { throw "False positive: $key" }
    }
    if ((Is-Clean $sample $sample $false $true) -or (Is-Clean $sample $sample $true $false)) {throw 'Live process ignored.'}
    'PASS baseline, ten state differences, live worker and live engine'; return
}
function Register-Detached {
    if ('AstralDetached' -as [type]) {return}
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class AstralDetached {
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SI {
        public uint cb; public string reserved,desktop,title; public int x,y,cx,cy,xc,yc,fill,flags;
        public short show,bytes; public IntPtr extra,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr process,thread; public uint pid,tid; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
        public long processTime,jobTime;public uint flags;public UIntPtr minSet,maxSet;public uint active;
        public UIntPtr affinity;public uint priority,scheduling;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref SI si,out PI pi);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool inside);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out BasicLimits info,int length,out int returned);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr obj);
    public static uint CheckCurrentLifetime() {
        bool inside;
        if(!IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out inside)) throw new Win32Exception();
        if(!inside) return 0;
        // A breakaway child can still belong to a system-wide, non-terminating job.
        // Querying NULL resolves the actual immediate job of this calling child.
        BasicLimits info;int length;
        if(!QueryInformationJobObject(IntPtr.Zero,2,out info,Marshal.SizeOf(typeof(BasicLimits)),out length)) throw new Win32Exception();
        if((info.flags & 0x2000)!=0) throw new InvalidOperationException("Detached process inherited a kill-on-job-close policy; no session started.");
        return info.flags;
    }
    public static uint Start(string exe,string args) {
        var si=new SI { cb=(uint)Marshal.SizeOf(typeof(SI)) }; PI pi;
        if(!CreateProcess(exe,new StringBuilder(args),IntPtr.Zero,IntPtr.Zero,false,0x08000000|0x01000000|0x00000200|4,IntPtr.Zero,null,ref si,out pi))
            throw new Win32Exception();
        try {
            if(ResumeThread(pi.thread)==uint.MaxValue) throw new Win32Exception();
            return pi.pid;
        } catch { TerminateProcess(pi.process,120); throw; }
        finally { CloseHandle(pi.thread); CloseHandle(pi.process); }
    }
}
'@
}
function Get-Peer {
    if ($PeerId -lt 1 -or $PeerTicks -le 0 -or $identity.User.Value -ne $ExpectedSid -or $process.SessionId -ne $ExpectedSession) {throw 'Use the same Windows account and session for authorization.'}
    $candidate=[Diagnostics.Process]::GetProcessById($PeerId)
    if ($candidate.HasExited -or $candidate.SessionId -ne $ExpectedSession -or $candidate.StartTime.ToUniversalTime().Ticks -ne $PeerTicks -or $candidate.MainModule.FileName -ine $shell) {throw 'Test owner identity changed.'}
    if (!('AstralTestToken' -as [type])) {
        Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;using System.Security.Principal;public static class AstralTestToken{[DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr h,uint a,out IntPtr t);[DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);public static WindowsIdentity Identity(IntPtr h){IntPtr t;if(!OpenProcessToken(h,0x000E,out t))throw new System.ComponentModel.Win32Exception();try{return new WindowsIdentity(t);}finally{CloseHandle(t);}}}'
    }
    $token=[AstralTestToken]::Identity($candidate.Handle)
    if ($token.User.Value -ne $ExpectedSid -or ([Security.Principal.WindowsPrincipal]::new($token)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Test owner must be the original non-elevated user.'}
    $script:reportIdentity=$token
    return $candidate
}
if ($Action -eq 'Peer') {
    if ($isAdmin) {throw 'Unprivileged test owner required.'}
    Register-Detached
    try { $flags=[AstralDetached]::CheckCurrentLifetime() }
    catch { Save-Record 'peer-error.json' @{error=$_.Exception.GetBaseException().Message}; throw }
    Save-Record 'peer-ready.json' @{pid=$PID;jobFlags=$flags}
    $limit=[Diagnostics.Stopwatch]::StartNew()
    while ($limit.Elapsed.TotalSeconds -lt ($Seconds+300) -and ![IO.File]::Exists((Join-Path $runDir 'finished.json')) -and ![IO.File]::Exists((Join-Path $runDir 'cancel.owner'))) {Start-Sleep -Milliseconds 350}
    return
}
if ($Action -eq 'Arm') {
    if (!$TimerOnly) {if (!$isAdmin) {throw 'Windows authorization is required.'}; Check-Exe}
    $peer=Get-Peer
    Register-Detached
    $command = (Quote-Arg $shell) + ' -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ' +
        (Quote-Arg $PSCommandPath) + " -Action Guard -RunId $RunId -ExpectedSid " + (Quote-Arg $ExpectedSid) +
        " -ExpectedSession $ExpectedSession -Seconds $Seconds -PeerId $PeerId -PeerTicks $PeerTicks -ArmId $PID -ArmTicks $($process.StartTime.ToUniversalTime().Ticks)"
    if ($TimerOnly) { $command += ' -TimerOnly' } else { $command += ' -Exe ' + (Quote-Arg $Exe) + ' -ExpectedSha256 ' + $ExpectedSha256 }
    $guardId=[AstralDetached]::Start($shell,$command)
    [pscustomobject]@{RunId=$RunId;GuardPID=$guardId;LauncherPID=$PID;Directory=$runDir}; return
}
function Start-Exact([string]$arguments) {
    Check-Exe
    $info=[Diagnostics.ProcessStartInfo]::new($Exe,$arguments)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WorkingDirectory=Split-Path -LiteralPath $Exe
    [Diagnostics.Process]::Start($info)
}
function Get-OwnJournal {
    if (![IO.File]::Exists($machineJournal)) { return $null }
    $saved=Read-Json $machineJournal
    if ($saved.SessionId -cne $RunId) {throw 'Machine journal has another session ID.'}
    $configDir=Join-Path (Split-Path -LiteralPath $machineJournal) $RunId
    $configFile=Join-Path $configDir 'router.json'
    if ($saved.Name -notmatch '^ASplit-[0-9a-f]{8}$' -or !$saved.AuxiliaryId -or !$saved.AuxiliaryLuid -or
        !$saved.AstralId -or ([string]$saved.AstralId).Trim('{}') -ine $baseline.Guid.Trim('{}') -or [string]$saved.AstralLuid -ne $baseline.Luid) { throw 'Journal does not match original adapter identity.' }
    if ([IO.File]::Exists($configFile)) {
        if ($saved.Name -cne (Read-Json $configFile).inbounds[0].interface_name) {throw 'Generated config conflicts with journal.'}
    } elseif (!$worker -or !$worker.HasExited -or !(Test-Path -LiteralPath (Join-Path $sessionDir 'status.json'))) {
        throw 'Missing config without positive post-exit machine-session ownership.'
    }
    return $machineJournal
}
function Write-Rules {
    $paths=@()
    $pref=Join-Path $localStore 'apps.json'
    if ($PreparedSelection) {
        $pref=Join-Path (Join-Path $localStore 'trial-plans') ($RunId+'.json')
        if (![IO.File]::Exists($pref)) {throw 'Trial selection snapshot is missing.'}
    }
    if ([IO.File]::Exists($pref)) {
        $saved = Read-Json $pref
        foreach ($one in $saved) {
            if ($one -isnot [string]) { throw 'Invalid application list entry.' }
            $paths += $one
        }
    }
    $patterns=@(foreach ($path in @($paths | Sort-Object -Unique)) {
        if (!$path -or ![IO.Path]::IsPathRooted($path) -or [IO.Path]::GetExtension($path) -ine '.exe') { throw 'Invalid saved application path.' }
        $builder=[Text.StringBuilder]::new('(?i)^')
        foreach ($ch in $path.ToCharArray()) { if ('\.+*?()|[]{}^$'.Contains([string]$ch)) {[void]$builder.Append('\')};[void]$builder.Append($ch) }
        [void]$builder.Append('$'); $builder.ToString()
    })
    $rules=@(); if ($patterns.Count) {$rules=@(@{process_path_regex=$patterns})}
    foreach ($one in $paths) {
        if (!@($patterns | Where-Object { [regex]::IsMatch($one,$_) }).Count) {throw 'Saved application missing from test rules.'}
    }
    $data=@{version=3;rules=$rules} | ConvertTo-Json -Depth 5 -Compress
    if ([IO.Directory]::Exists($sessionDir)) {throw 'Session ID already exists.'}
    [void][IO.Directory]::CreateDirectory($sessionDir)
    [IO.File]::WriteAllText((Join-Path $sessionDir 'selection.json'),$data,[Text.UTF8Encoding]::new($false))
}
if ($Action -eq 'Start') {
    if ($isAdmin) {throw '请在普通桌面双击启动，授权时再允许 Windows 提示。'}
    if ([IO.Directory]::Exists($runDir)) {throw '测试编号已存在。'}
    $Exe=Join-Path (Split-Path -LiteralPath $toolRoot) 'AstralSplit.exe'
    if (!$TimerOnly) { $ExpectedSha256=(Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash; Check-Exe }
    [void][IO.Directory]::CreateDirectory($runDir)
    if (!$TimerOnly) {Write-Rules}
    Register-Detached
    $peerCmd=(Quote-Arg $shell)+' -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File '+(Quote-Arg $PSCommandPath)+" -Action Peer -RunId $RunId -Seconds $Seconds"
    $ownerProcess=[Diagnostics.Process]::GetProcessById([AstralDetached]::Start($shell,$peerCmd))
    try {
        $peerReady=Join-Path $runDir 'peer-ready.json'
        $waitPeer=[Diagnostics.Stopwatch]::StartNew()
        while (![IO.File]::Exists($peerReady) -and !$ownerProcess.HasExited -and $waitPeer.Elapsed.TotalSeconds -lt 12) {Start-Sleep -Milliseconds 80}
        if (![IO.File]::Exists($peerReady) -or $ownerProcess.HasExited) {
            $why=Join-Path $runDir 'peer-error.json'
            if ([IO.File]::Exists($why)) {throw ([string](Read-Json $why).error)}
            throw 'Independent test owner did not start.'
        }
        $params=@('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Quote-Arg $PSCommandPath),'-Action','Arm','-RunId',$RunId,'-Seconds',$Seconds,
            '-ExpectedSid',$identity.User.Value,'-ExpectedSession',$process.SessionId,'-PeerId',$ownerProcess.Id,'-PeerTicks',$ownerProcess.StartTime.ToUniversalTime().Ticks)
        if ($TimerOnly) {$params+='-TimerOnly'; $launcher=Start-Process -FilePath $shell -ArgumentList $params -WindowStyle Hidden -PassThru}
        else {
            Write-Host "即将申请 Windows 管理员授权。限时测试最多 $Seconds 秒，使用已保存的应用选择。"
            $params+=@('-Exe',(Quote-Arg $Exe),'-ExpectedSha256',$ExpectedSha256)
            $launcher=Start-Process -FilePath $shell -ArgumentList $params -Verb RunAs -WindowStyle Hidden -PassThru
        }
        try {$launcher.WaitForExit();if ($launcher.ExitCode -ne 0){throw '独立看门狗未能启动。请使用当前 Windows 账户授权；没有开启本次分流。'}}finally{$launcher.Dispose()}
    } catch { [IO.File]::WriteAllText((Join-Path $runDir 'cancel.owner'),''); throw }
    finally {$ownerProcess.Dispose()}
    $limit=[Diagnostics.Stopwatch]::StartNew();$armed=$false;$active=$false
    while (![IO.File]::Exists((Join-Path $runDir 'finished.json')) -and $limit.Elapsed.TotalSeconds -lt ($Seconds+110)) {
        if (!$armed -and [IO.File]::Exists((Join-Path $runDir 'armed.json'))){$armed=$true;Write-Host '独立计时器已就绪。'}
        if (!$active -and [IO.File]::Exists((Join-Path $runDir 'ready.json'))){$active=$true;Write-Host '测试分流已启用，现在可在已选应用中打开新连接。'}
        Start-Sleep -Milliseconds 250
    }
    Write-Host ('测试记录：'+$runDir)
    if (![IO.File]::Exists((Join-Path $runDir 'finished.json'))) {throw '还未收到结束记录。请检查 finished.json，再决定是否需要从主程序恢复网络。'}
    $result=Read-Json (Join-Path $runDir 'finished.json')
    if ($result.cancelledBeforeStart -and !$result.error) {Write-Host '试用已提前取消，本次未开启分流。';return}
    if ($TimerOnly) {if ((!$result.timerFired -and $result.trigger -ne 'requested') -or $result.error){throw ('离线计时验证失败。'+$result.error)};Write-Host '离线计时验证通过。';return}
    if ($result.baselineVerified) {Write-Host '已核对临时路由清理和星云网卡设置恢复。'}
    if ($result.error -or !$result.ready -or !$result.baselineVerified){throw ('本次测试未通过：'+$result.error+' '+$result.detail)}
    Write-Host '测试结束。此工具不检测网站的最终公网出口。';return
}
$worker=$null; $stopEvent=$null; $baseline=$null; $reason=$null; $trigger='none'
$stopRequest=Join-Path $runDir 'stop.request'; $cancelledBeforeStart=$false
$recoveryRun=$null; $ready=$false; $forced=$false; $verified=$false; $finalPhase=$null; $finalDetail=$null; $workerCode=$null
try {
    $peer=Get-Peer
    if (!$ExpectedSid -or $identity.User.Value -ne $ExpectedSid -or $ExpectedSession -ne $process.SessionId) { throw 'Detached identity/session changed; cannot safely run.' }
    Register-Detached
    $jobFlags=[AstralDetached]::CheckCurrentLifetime()
    if ($ArmId -le 0 -or $ArmTicks -le 0) { throw 'Missing independent launcher lifetime.' }
    $armProcess=$null
    try {
        try { $armProcess=[Diagnostics.Process]::GetProcessById($ArmId) } catch [ArgumentException] { }
        if ($armProcess) {
            try { $sameArm=(!$armProcess.HasExited -and $armProcess.StartTime.ToUniversalTime().Ticks -eq $ArmTicks) }
            catch { if (!$armProcess.HasExited) {throw}; $sameArm=$false }
            if ($sameArm -and !$armProcess.WaitForExit(8000)) {throw 'Independent launcher did not exit.'}
        }
    } finally {if($armProcess){$armProcess.Dispose()}}
    if ([IO.File]::Exists($stopRequest)) { $trigger='requested'; $cancelledBeforeStart=$true; return }
    if ($TimerOnly) {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        Save-Record 'armed.json' @{pid=$PID;session=$RunId;limitSeconds=$Seconds;jobFlags=$jobFlags;time=[DateTime]::UtcNow.ToString('o')}
        while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
            if ([IO.File]::Exists($stopRequest)) { $trigger='requested';break }
            Start-Sleep -Milliseconds 100
        }
        if ($trigger -eq 'none') {$trigger='deadline'}
        $verified=$true
    } else {
        if (!$isAdmin) { throw 'Detached watchdog did not receive the elevated token.' }
        Check-Exe
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class AstralQaStop { [DllImport("kernel32.dll",SetLastError=true)] public static extern bool TerminateProcess(IntPtr process,uint code); [DllImport("iphlpapi.dll")] public static extern uint ConvertInterfaceIndexToLuid(uint index,out ulong luid); }'
        $baseline=Snapshot
        if ($baseline.CaptureCount -or $baseline.AdapterCount -or $baseline.JournalCount -or @(Find-Workers).Count -or
            @(Get-Process -Name 'AstralSplit.Router' -ErrorAction SilentlyContinue).Count) { throw 'Another session or unrecovered network state is present.' }
        Save-Record 'baseline.json' $baseline
        if (![IO.File]::Exists((Join-Path $sessionDir 'selection.json'))) {throw 'Prepared application selection is missing.'}
        $created=$false
        $stopEvent=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,('Local\AstralSplit.Stop.'+$RunId),[ref]$created)
        if (!$created) { throw 'Stop event already exists.' }
        $clock=[Diagnostics.Stopwatch]::StartNew()
        Save-Record 'armed.json' @{pid=$PID;session=$RunId;limitSeconds=$Seconds;time=[DateTime]::UtcNow.ToString('o')}
        if ($clock.Elapsed.TotalSeconds -ge $Seconds -or $peer.HasExited) {throw 'Watchdog deadline or owner expired before session start.'}
        if ([IO.File]::Exists($stopRequest)) { $trigger='requested'; $cancelledBeforeStart=$true; return }
        $worker=Start-Exact ("--session {0} {1} {2}" -f $RunId,$PeerId,$PeerTicks)
        $workerHandle=$worker.Handle
        Save-Record 'worker.json' @{pid=$PID;worker=$worker.Id;session=$RunId;workerStart=$worker.StartTime.ToUniversalTime().Ticks;limitSeconds=$Seconds}
        $statusPath=Join-Path $sessionDir 'status.json'
        while (!$worker.HasExited -and $clock.Elapsed.TotalSeconds -lt $Seconds) {
            if ([IO.File]::Exists($stopRequest)) { $trigger='requested'; break }
            if (!$ready -and [IO.File]::Exists($statusPath) -and (Read-Json $statusPath).Phase -eq 'active') {
                if (!(Get-OwnJournal)) { throw 'Active but own journal not confirmed.' }
                Save-Record 'ready.json' @{session=$RunId;pid=$worker.Id;time=[DateTime]::UtcNow.ToString('o')}
                $ready=$true
            }
            Start-Sleep -Milliseconds 180
        }
        if ($trigger -eq 'none') { $trigger=if($worker.HasExited){'worker-exited'}else{'deadline'} }
    }
} catch { $reason=$_.Exception.Message }
finally {
    try {
        if ($worker) {
            [void]$stopEvent.Set()
            if (!$worker.WaitForExit(12000)) {
                if (!$worker.HasExited -and ![AstralQaStop]::TerminateProcess($workerHandle,119)) { if (!$worker.HasExited) { throw 'Exact retained worker termination failed.' } }
                $forced=$true
                if (!$worker.WaitForExit(5000)) { throw 'Exact worker did not exit.' }
            }
            $workerCode=$worker.ExitCode
            $statusFile=Join-Path $sessionDir 'status.json'
            if ([IO.File]::Exists($statusFile)) {
                try { $lastState=Read-Json $statusFile; $finalPhase=$lastState.Phase; $finalDetail=$lastState.Detail }
                catch { $reason=(@($reason,'Cannot read final worker status.') | Where-Object {$_}) -join '; ' }
            }
            if (!$ready -or $finalPhase -eq 'error' -or $trigger -eq 'worker-exited') {
                $reason=(@($reason,('Worker ended: activeReached={0}, phase={1}, detail={2}' -f $ready,$finalPhase,$finalDetail)) | Where-Object {$_}) -join '; '
            }
            $ownJournal=Get-OwnJournal
            if ($ownJournal) {
                if (@(Find-Workers).Count) { throw 'Another worker appeared; automatic recovery refused.' }
                $recoveryRun=[guid]::NewGuid().ToString('N')
                if ($peer.HasExited) {throw 'The independent test owner exited. Use the application recovery button.'}
                $repair=Start-Exact ("--restore {0} {1} {2}" -f $recoveryRun,$PeerId,$PeerTicks)
                try {
                    if (!$repair.WaitForExit(30000)) {throw 'Own restore helper still running; manual verification needed.'}
                    $repairState=Read-Json (Join-Path (Join-Path (Join-Path $localStore 'sessions') $recoveryRun) 'status.json')
                    if ($repairState.Phase -ne 'stopped') { throw 'Recovery helper did not report success.' }
                } finally { $repair.Dispose() }
            }
            $settle=[Diagnostics.Stopwatch]::StartNew()
            do {
                $after=Snapshot
                $engineGone=(@(Get-Process 'AstralSplit.Router' -ErrorAction SilentlyContinue).Count -eq 0)
                $verified=Is-Clean $baseline $after $worker.HasExited $engineGone
                if ($verified) {break}
                Start-Sleep -Milliseconds 400
            } while ($settle.Elapsed.TotalSeconds -lt 12)
            Save-Record 'after.json' $after
            if (!$verified) {throw 'Network recovery did not match the pre-test baseline.'}
        }
    } catch { $reason=(@($reason,$_.Exception.Message) | Where-Object {$_}) -join '; ' }
    if ($worker) {$worker.Dispose()}; if($stopEvent){$stopEvent.Dispose()}
    Save-Record 'finished.json' @{run=$RunId;timerOnly=[bool]$TimerOnly;trigger=$trigger;cancelledBeforeStart=$cancelledBeforeStart;ready=$ready;forced=$forced;workerExit=$workerCode;phase=$finalPhase;detail=$finalDetail;restoreSession=$recoveryRun;baselineVerified=($verified -and !$TimerOnly);timerFired=($verified -and $TimerOnly -and $trigger -eq 'deadline');exitProbes='external-not-assessed';error=$reason;time=[DateTime]::UtcNow.ToString('o')}
}
if ($reason -or !$verified) {exit 1}
