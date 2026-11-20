#Requires -Version 5.1
<#
.SYNOPSIS
    MuMu 模拟器「单个实例」弱网 / 断网模拟工具。

.DESCRIPTION
    两种引擎，按需要选用：

    1) 模拟器内部引擎（推荐，精确到实例、免管理员、秒级生效/恢复）
       通过 MuMuManager.exe sh 在 Android 里下发 iptables 规则（实例内是 root），
       规则全部带 mumu-net 标记，recover 可一键清干净。
         offline : 断网（INPUT/OUTPUT 全丢，只放行 lo 和 adb 端口，保证控制通道不死）
         loss    : 随机丢包（statistic random，模拟弱网抖动/丢包）
         pps     : 限包速（limit N/s，粗糙限速/拥堵）
         flap    : 间歇断网（通/断循环，测重连逻辑）

    2) clumsy 引擎（宿主机侧，能模拟真正的延迟/抖动/限速，需要 UAC 提权）
         lag     : 用 clumsy 给「该实例」加延迟/丢包/限速
         stop    : 关掉 clumsy

    说明：默认 NAT 模式下，模拟器在宿主机上没有独立 IP（走 MuMuVMMHeadless.exe 的
    宿主套接字），所以 clumsy 无法用「模拟器 IP」来过滤，-Scope ports 会自动抓
    MuMuVMMHeadless 当前占用的端口来构造过滤器。

.EXAMPLE
    .\mumu-net.ps1 status
    .\mumu-net.ps1 offline
    .\mumu-net.ps1 loss -Percent 30
    .\mumu-net.ps1 flap -OnSeconds 15 -OffSeconds 10 -Cycles 3
    .\mumu-net.ps1 lag -Ms 300 -Jitter 0 -DropPercent 5 -Scope ports
    .\mumu-net.ps1 lag -Ms 200 -Scope global -ClumsyTimeoutSec 60 -DryRun
    .\mumu-net.ps1 recover
    .\mumu-net.ps1 shell -Command "getprop ro.product.model"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'offline', 'recover', 'online', 'loss', 'pps', 'flap', 'lag', 'stop', 'probe', 'shell')]
    [string]$Action = 'status',

    # 目标实例序号；-1 = 自动选第一个运行中的实例
    [int]$VmIndex = -1,

    # ---- 模拟器内部引擎参数 ----
    [int]$Percent = 30,                 # loss：丢包百分比
    [int]$Pps = 50,                     # pps：每秒放行的包数上限
    [ValidateSet('', 'out', 'both')]
    [string]$Direction = '',            # out=只卡出方向 / both=双向；留空按动作默认（offline、flap=both，loss、pps=out）
    [int]$AdbPort = 5555,               # 实例内 adbd 端口，断网时白名单放行

    # ---- flap 参数 ----
    [int]$OnSeconds = 15,
    [int]$OffSeconds = 10,
    [int]$Cycles = 3,

    # ---- clumsy 引擎参数 ----
    [int]$Ms = 300,                     # 延迟毫秒
    [int]$DropPercent = 0,              # clumsy 丢包百分比
    [int]$LimitKB = 0,                  # clumsy 限速 KB/s
    [ValidateSet('ports', 'guestip', 'global')]
    [string]$Scope = 'ports',
    [string]$GuestIp = '10.0.2.15',     # 模拟器内部网卡 IP（guestip 模式用）
    [string]$Filter = '',               # 直接指定 WinDivert 过滤器，优先级最高
    [int]$ClumsyTimeoutSec = 60,        # clumsy 自动退出秒数（0=不自动退出，手动 stop）
    [int]$ClumsyWaitSec = 30,           # 等待 UAC 确认 / clumsy 启动的秒数
    [switch]$Refresh,                   # ports 模式：端口变化时自动重启 clumsy（有短暂空档）
    [switch]$DryRun,                    # 只打印将执行的 clumsy 命令行，不启动
    [switch]$Verify,                    # lag：启动后实测实例内 TCP 握手耗时，判断过滤器是否真的命中该实例

    # ---- 路径 / 其它 ----
    [string]$MuMuManager = '',
    [string]$ClumsyPath = 'D:\clumsy\clumsy.exe',
    [string]$Command = ''               # shell：在实例内执行的命令
)

$ErrorActionPreference = 'Stop'
$script:Tag = 'mumu-net'
$script:MuMuManager = $MuMuManager

# ---------------------------------------------------------------- 基础设施

function Write-Head([string]$Text) { Write-Host ""; Write-Host "== $Text ==" -ForegroundColor Cyan }

function Resolve-Direction {
    if ($Direction) { return $Direction }
    switch ($Action) {
        'offline' { return 'both' }
        'flap' { return 'both' }
        default { return 'out' }
    }
}
function Write-Ok([string]$Text)   { Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Warn2([string]$Text){ Write-Host "  [!]  $Text" -ForegroundColor Yellow }
function Write-Err2([string]$Text) { Write-Host "  [x]  $Text" -ForegroundColor Red }

function Find-MuMuManager {
    if ($script:MuMuManager -and (Test-Path $script:MuMuManager)) { return $script:MuMuManager }
    $cands = @(
        'C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe',
        'C:\Program Files (x86)\Netease\MuMu\nx_main\MuMuManager.exe',
        'D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe'
    )
    foreach ($c in $cands) { if (Test-Path $c) { $script:MuMuManager = $c; return $c } }
    foreach ($root in @('C:\Program Files\Netease\MuMu', 'D:\Program Files\Netease\MuMu', 'C:\Program Files (x86)\Netease\MuMu')) {
        if (Test-Path $root) {
            $f = Get-ChildItem $root -Recurse -Depth 3 -Filter 'MuMuManager.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($f) { $script:MuMuManager = $f.FullName; return $f.FullName }
        }
    }
    throw 'MuMuManager.exe 未找到，请用 -MuMuManager <路径> 指定'
}

function Get-MuMuInstances {
    $mm = Find-MuMuManager
    $raw = (& $mm info -v all 2>&1 | Out-String)
    $start = $raw.IndexOf('{')
    if ($start -lt 0) { throw "读取实例列表失败：$raw" }
    return ($raw.Substring($start) | ConvertFrom-Json)
}

function Resolve-Instance {
    $all = Get-MuMuInstances
    if ($VmIndex -ge 0) {
        $inst = $all.PSObject.Properties | Where-Object { $_.Name -eq "$VmIndex" } | Select-Object -First 1
        if (-not $inst) { throw "没有找到实例 $VmIndex" }
        $inst = $inst.Value
        if (-not $inst.is_process_started) { throw "实例 $VmIndex（$($inst.name)）未运行，先启动它" }
        return $inst
    }
    $running = @()
    foreach ($p in $all.PSObject.Properties) { if ($p.Value.is_process_started) { $running += $p.Value } }
    if ($running.Count -eq 0) { throw '没有正在运行的 MuMu 实例' }
    if ($running.Count -gt 1) {
        Write-Warn2 ("检测到多个运行中的实例：" + (($running | ForEach-Object { "[$($_.index)]$($_.name)" }) -join '、') + "，使用第一个（可用 -VmIndex 指定）")
    }
    return $running[0]
}

function Invoke-Guest {
    [CmdletBinding()]
    param([int]$Index, [string]$Cmd, [int]$TimeoutSec = 15)
    $mm = Find-MuMuManager
    $job = Start-Job -ScriptBlock {
        param($exe, $idx, $c)
        & $exe sh -v $idx -c $c 2>&1 | Out-String
    } -ArgumentList $mm, $Index, $Cmd
    $done = Wait-Job $job -Timeout $TimeoutSec
    if (-not $done) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        throw "实例内执行超时（${TimeoutSec}s）：$Cmd"
    }
    $out = (Receive-Job $job) -join "`n"
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    return $out
}

function Test-GuestReachable {
    param([int]$Index)
    try { return ((Invoke-Guest -Index $Index -Cmd 'echo mu-mu-ok' -TimeoutSec 10) -match 'mu-mu-ok') }
    catch { return $false }
}

# ---------------------------------------------------------------- 实例内规则

function Set-OfflineRules {
    param([int]$Index, [int]$Port, [bool]$OutOnly)
    $t = $script:Tag
    $p = @()
    $p += "iptables -I OUTPUT 1 -o lo -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 2 -p tcp --sport $Port -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 3 -m comment --comment $t -j DROP"
    if (-not $OutOnly) {
        $p += "iptables -I INPUT 1 -i lo -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 2 -p tcp --dport $Port -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 3 -m comment --comment $t -j DROP"
    }
    Invoke-Guest -Index $Index -Cmd ($p -join '; ') | Out-Null
}

function Set-LossRules {
    param([int]$Index, [int]$Port, [double]$Probability, [bool]$OutOnly)
    $t = $script:Tag
    $prob = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.####}', $Probability)
    $p = @()
    $p += "iptables -I OUTPUT 1 -o lo -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 2 -p tcp --sport $Port -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 3 -m statistic --mode random --probability $prob -m comment --comment $t -j DROP"
    if (-not $OutOnly) {
        $p += "iptables -I INPUT 1 -i lo -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 2 -p tcp --dport $Port -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 3 -m statistic --mode random --probability $prob -m comment --comment $t -j DROP"
    }
    Invoke-Guest -Index $Index -Cmd ($p -join '; ') | Out-Null
}

function Set-PpsRules {
    param([int]$Index, [int]$Port, [int]$PerSecond, [bool]$OutOnly)
    $t = $script:Tag
    $p = @()
    $p += "iptables -I OUTPUT 1 -o lo -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 2 -p tcp --sport $Port -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 3 -m limit --limit ${PerSecond}/s -m comment --comment $t -j ACCEPT"
    $p += "iptables -I OUTPUT 4 -m comment --comment $t -j DROP"
    if (-not $OutOnly) {
        $p += "iptables -I INPUT 1 -i lo -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 2 -p tcp --dport $Port -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 3 -m limit --limit ${PerSecond}/s -m comment --comment $t -j ACCEPT"
        $p += "iptables -I INPUT 4 -m comment --comment $t -j DROP"
    }
    Invoke-Guest -Index $Index -Cmd ($p -join '; ') | Out-Null
}

function Get-GuestRuleNumbers {
    param([int]$Index, [string]$Chain)
    $out = Invoke-Guest -Index $Index -Cmd "iptables -L $Chain -n --line-numbers"
    $nums = New-Object System.Collections.Generic.List[int]
    foreach ($line in ($out -split "`n")) {
        if ($line -match ('^\s*(\d+)\s+.*' + [regex]::Escape($script:Tag))) { $nums.Add([int]$Matches[1]) }
    }
    return $nums
}

function Clear-GuestRules {
    param([int]$Index)
    $removed = 0
    foreach ($chain in @('INPUT', 'OUTPUT')) {
        for ($pass = 0; $pass -lt 6; $pass++) {
            $nums = Get-GuestRuleNumbers -Index $Index -Chain $chain
            if ($nums.Count -eq 0) { break }
            foreach ($n in ($nums | Sort-Object -Descending)) {
                Invoke-Guest -Index $Index -Cmd "iptables -D $chain $n" | Out-Null
                $removed++
            }
        }
    }
    return $removed
}

function Show-GuestRules {
    param([int]$Index)
    $out = Invoke-Guest -Index $Index -Cmd "iptables -S INPUT; echo __OUT__; iptables -S OUTPUT"
    $lines = @()
    foreach ($line in ($out -split "`n")) {
        $l = $line.Trim()
        if ($l -match [regex]::Escape($script:Tag)) { $lines += ("    " + $l) }
    }
    if ($lines.Count -eq 0) { Write-Host "    （无 mumu-net 规则，网络未受本工具影响）" } else { $lines | ForEach-Object { Write-Host $_ } }
}

function Show-GuestNet {
    param([int]$Index, [int]$Count = 4)
    try {
        $o = Invoke-Guest -Index $Index -Cmd "timeout 30 ping -c $Count -W 2 8.8.8.8 | tail -2" -TimeoutSec 40
        Write-Host ("    实例内 ping 8.8.8.8：" + (($o -split "`n") -join ' ').Trim())
    } catch { Write-Warn2 "实例内 ping 失败（实例可能正处于完全断网，或 adb 通道异常）" }
}

# ---------------------------------------------------------------- clumsy

function Get-ClumsyProcesses { Get-Process -Name 'clumsy' -ErrorAction SilentlyContinue }

function Stop-Clumsy {
    $procs = Get-ClumsyProcesses
    if (-not $procs) { return 0 }
    $procs | ForEach-Object { try { $_.Kill() } catch { } }
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 200
        if (-not (Get-ClumsyProcesses)) { break }
    }
    return $procs.Count
}

function Resolve-ClumsyPath {
    if ($ClumsyPath -and (Test-Path $ClumsyPath)) { return $ClumsyPath }
    foreach ($p in @('D:\clumsy\clumsy.exe', 'C:\clumsy\clumsy.exe', 'D:\tools\clumsy\clumsy.exe')) {
        if (Test-Path $p) { return $p }
    }
    foreach ($root in @('D:\', 'C:\')) {
        $f = Get-ChildItem $root -Recurse -Depth 3 -Filter 'clumsy.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    throw 'clumsy.exe 未找到，请用 -ClumsyPath <路径> 指定'
}

function Get-MuMuVmmPorts {
    $pids = @((Get-Process -Name 'MuMuVMMHeadless' -ErrorAction SilentlyContinue).Id)
    if ($pids.Count -eq 0) { return [pscustomobject]@{ Tcp = @(); Udp = @() } }
    $tcp = New-Object System.Collections.Generic.List[int]
    $udp = New-Object System.Collections.Generic.List[int]
    Get-NetTCPConnection -ErrorAction SilentlyContinue |
        Where-Object { $pids -contains $_.OwningProcess -and $_.LocalPort -ge 1024 -and $_.LocalAddress -ne '127.0.0.1' } |
        ForEach-Object { $tcp.Add([int]$_.LocalPort) }
    Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $pids -contains $_.OwningProcess -and $_.LocalPort -ge 1024 -and $_.LocalAddress -ne '127.0.0.1' } |
        ForEach-Object { $udp.Add([int]$_.LocalPort) }
    return [pscustomobject]@{
        Tcp = @($tcp | Sort-Object -Unique | Select-Object -First 24)
        Udp = @($udp | Sort-Object -Unique | Select-Object -First 24)
    }
}

function New-ClumsyFilter {
    if ($Filter) { return $Filter }
    switch ($Scope) {
        'global'  { return 'outbound and ip' }
        'guestip' { return "ip.SrcAddr == $GuestIp or ip.DstAddr == $GuestIp" }
        'ports' {
            $sets = Get-MuMuVmmPorts
            $parts = @()
            if ($sets.Tcp.Count -gt 0) { $parts += "(tcp and (" + ((@($sets.Tcp) | ForEach-Object { "tcp.SrcPort == $_" }) -join ' or ') + "))" }
            if ($sets.Udp.Count -gt 0) { $parts += "(udp and (" + ((@($sets.Udp) | ForEach-Object { "udp.SrcPort == $_" }) -join ' or ') + "))" }
            if ($parts.Count -eq 0) {
                Write-Warn2 '未抓到 MuMuVMMHeadless 的端口（实例可能没在跑），退回全局 outbound and ip'
                return 'outbound and ip'
            }
            return ($parts -join ' or ')
        }
    }
}

function Get-ClumsyArgs {
    param([string]$FilterExpr)
    $a = New-Object System.Collections.Generic.List[string]
    $a.Add('--filter'); $a.Add('"' + $FilterExpr + '"')
    if ($Ms -gt 0)          { $a.Add('--lag');       $a.Add('on'); $a.Add('--lag-time');           $a.Add("$Ms") }
    if ($DropPercent -gt 0) { $a.Add('--drop');      $a.Add('on'); $a.Add('--drop-chance');        $a.Add("$DropPercent") }
    if ($LimitKB -gt 0)     { $a.Add('--bandwidth'); $a.Add('on'); $a.Add('--bandwidth-bandwidth');$a.Add("$LimitKB") }
    if ($ClumsyTimeoutSec -gt 0) { $a.Add('--timeout'); $a.Add("$ClumsyTimeoutSec") }
    return $a
}

function Start-ClumsyOnce {
    param([string]$FilterExpr)
    $exe = Resolve-ClumsyPath
    $argList = Get-ClumsyArgs -FilterExpr $FilterExpr
    $flat = ($argList | ForEach-Object { $_ }) -join ' '
    Write-Host ("    " + (Split-Path $exe -Leaf) + " " + $flat) -ForegroundColor DarkGray
    if ($DryRun) { Write-Warn2 '-DryRun：未真正启动'; return $true }
    if (Get-ClumsyProcesses) {
        Write-Warn2 '已有 clumsy 在运行，先关闭它（clumsy 同一时间只允许一个实例）'
        Stop-Clumsy | Out-Null
    }
    # clumsy 自己会请求 UAC 提权，这里不要 -Verb RunAs（避免二次提权）
    Start-Process -FilePath $exe -ArgumentList $argList | Out-Null
    Write-Host ("    等待 clumsy 启动（屏幕上会弹 UAC，请点「是」，最多等 $ClumsyWaitSec 秒）...") -ForegroundColor DarkGray
    $procs = $null
    $loops = [int]($ClumsyWaitSec * 2)
    for ($i = 0; $i -lt $loops; $i++) {
        Start-Sleep -Milliseconds 500
        $procs = Get-ClumsyProcesses
        if ($procs) { break }
    }
    if (-not $procs) {
        Write-Err2 '没有检测到 clumsy 进程：UAC 被拒绝/超时，或 clumsy 启动即退出（过滤器语法错误会弹窗提示）'
        return $false
    }
    Start-Sleep -Seconds 2
    Write-Ok ("clumsy 已运行（PID " + (($procs | ForEach-Object { $_.Id }) -join ',') + "）")
    return $true
}

# ---------------------------------------------------------------- 动作

function Do-Status {
    $inst = Resolve-Instance
    Write-Head "目标实例"
    Write-Host ("    序号 $($inst.index)  名称 $($inst.name)  Android $($inst.android_version)")
    Write-Host ("    adb $($inst.adb_host_ip):$($inst.adb_port)   MuMuManager: $(Find-MuMuManager)")

    Write-Head "实例内 mumu-net 规则"
    Show-GuestRules -Index ([int]$inst.index)

    Write-Head "实例网络实况"
    Show-GuestNet -Index ([int]$inst.index) -Count 3

    Write-Head "宿主机侧"
    $cl = Get-ClumsyProcesses
    if ($cl) { Write-Host ("    clumsy 正在运行：PID " + (($cl | ForEach-Object { $_.Id }) -join ',')) } else { Write-Host "    clumsy 未运行" }
    $ports = Get-MuMuVmmPorts
    Write-Host ("    MuMuVMMHeadless 当前占用本机端口：TCP $($ports.Tcp.Count) 个 / UDP $($ports.Udp.Count) 个")
    if ($ports.Tcp.Count -gt 0) { Write-Host ("      TCP: " + (($ports.Tcp | Select-Object -First 12) -join ', ')) }
    if ($ports.Udp.Count -gt 0) { Write-Host ("      UDP: " + (($ports.Udp | Select-Object -First 12) -join ', ')) }
    Write-Host ("    本机 IP：" + ((Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceAlias -notmatch 'Loopback' -and $_.IPAddress -notmatch '^169\.254\.' } |
        Select-Object -ExpandProperty IPAddress) -join ', '))
}

function Do-Probe {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    Write-Head "adb 通道 / root"
    $o = Invoke-Guest -Index $idx -Cmd 'id; toybox netstat -ltn | grep 5555'
    Write-Host ($o.Trim() -replace "`n", "`n    ")
    Write-Head "实例内网络（应正常）"
    Show-GuestNet -Index $idx -Count 5
    Write-Head "建议"
    Write-Host "    · 断网/丢包：.\mumu-net.ps1 offline | loss -Percent 30（精确到实例，免管理员）"
    Write-Host "    · 延迟/限速：.\mumu-net.ps1 lag -Ms 300 -DropPercent 5（会弹 UAC，clumsy 需要管理员）"
    Write-Host "    · clumsy 过滤器三选一："
    Write-Host "        ports   ：(tcp and (tcp.SrcPort == <MuMuVMMHeadless 端口>)) or (udp and (...))  ← 本工具自动生成"
    Write-Host "        guestip ：ip.SrcAddr == $GuestIp or ip.DstAddr == $GuestIp                          ← 仅当 MuMu 以原始帧转发时有效"
    Write-Host "        global  ：outbound and ip                                                          ← 一定生效，但会影响宿主其它程序（不含回环）"
}

function Do-Recover {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    Write-Head "清理实例内规则"
    $n = Clear-GuestRules -Index $idx
    Write-Ok "已删除 $n 条 mumu-net 规则"
    Show-GuestRules -Index $idx
    Write-Head "关闭 clumsy"
    $c = Stop-Clumsy
    if ($c -gt 0) { Write-Ok "已关闭 $c 个 clumsy 进程" } else { Write-Ok "clumsy 本来就没在跑" }
    Write-Ok "已恢复"
    Show-GuestNet -Index $idx -Count 3
}

function Do-Offline {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    $both = ((Resolve-Direction) -eq 'both')
    Write-Head "断网：实例 $($inst.index) $($inst.name)"
    Set-OfflineRules -Index $idx -Port $AdbPort -OutOnly (-not $both)
    Write-Ok ("已下发 DROP 规则（" + $(if ($both) { '双向' } else { '仅出方向' }) + "，放行 lo + tcp/$AdbPort 保住 adb）")
    Show-GuestRules -Index $idx
    Show-GuestNet -Index $idx -Count 3
    Write-Host "    恢复：.\mumu-net.ps1 recover"
}

function Do-Loss {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    $both = ((Resolve-Direction) -eq 'both')
    Write-Head "丢包：实例 $($inst.index) $($inst.name)  $Percent% / $(if ($both) { '双向' } else { '仅出方向' })"
    Set-LossRules -Index $idx -Port $AdbPort -Probability ($Percent / 100.0) -OutOnly (-not $both)
    Write-Ok "已下发随机丢包规则"
    Show-GuestRules -Index $idx
    Show-GuestNet -Index $idx -Count 10
    Write-Host "    恢复：.\mumu-net.ps1 recover"
}

function Do-Pps {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    $both = ((Resolve-Direction) -eq 'both')
    Write-Head "限包速：实例 $($inst.index) $($inst.name)  $Pps 包/秒 / $(if ($both) { '双向' } else { '仅出方向' })"
    Set-PpsRules -Index $idx -Port $AdbPort -PerSecond $Pps -OutOnly (-not $both)
    Write-Ok "已下发 limit 规则"
    Show-GuestRules -Index $idx
    Show-GuestNet -Index $idx -Count 6
    Write-Host "    恢复：.\mumu-net.ps1 recover"
}

function Do-Flap {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    $both = ((Resolve-Direction) -eq 'both')
    Write-Head "间歇断网：$OnSeconds s 断 / $OffSeconds s 通，共 $Cycles 轮（$(if ($both) { '双向' } else { '仅出方向' })）"
    for ($i = 1; $i -le $Cycles; $i++) {
        $ts = Get-Date -Format 'HH:mm:ss'
        Write-Host "  [$ts] 第 $i/$Cycles 轮 -> 断网" -ForegroundColor Yellow
        Clear-GuestRules -Index $idx | Out-Null
        Set-OfflineRules -Index $idx -Port $AdbPort -OutOnly (-not $both)
        Start-Sleep -Seconds $OnSeconds
        $ts = Get-Date -Format 'HH:mm:ss'
        Write-Host "  [$ts] 第 $i/$Cycles 轮 -> 恢复" -ForegroundColor Green
        Clear-GuestRules -Index $idx | Out-Null
        if ($i -lt $Cycles) { Start-Sleep -Seconds $OffSeconds }
    }
    Write-Ok '间歇断网结束，网络已恢复'
    Show-GuestNet -Index $idx -Count 3
}

function Measure-GuestNet {
    param([int]$Index)
    $r = [pscustomobject]@{ Rtt = -1.0; Tcp = -1.0 }
    try {
        $o = Invoke-Guest -Index $Index -Cmd 'timeout 15 ping -c 3 -W 2 8.8.8.8 | tail -1' -TimeoutSec 25
        if ($o -match '=\s*([\d.]+)/([\d.]+)/([\d.]+)') { $r.Rtt = [double]$Matches[2] }
    } catch { }
    try {
        $o = Invoke-Guest -Index $Index -Cmd "curl -m 8 -o /dev/null -s -w '%{time_connect}\n' https://www.baidu.com" -TimeoutSec 25
        $v = 0.0
        if ([double]::TryParse($o.Trim(), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$v)) { $r.Tcp = $v }
    } catch { }
    return $r
}

function Do-Lag {
    $inst = Resolve-Instance
    $idx = [int]$inst.index
    Write-Head "clumsy 弱网：实例 $($inst.index) $($inst.name)"
    if ($Scope -eq 'guestip') {
        Write-Warn2 "guestip 模式依赖 MuMu 以原始帧转发（$GuestIp 在宿主机可见）；建议加 -Verify 实测，不生效就换 -Scope global"
    }
    if ($Scope -eq 'ports') {
        Write-Warn2 'ports 模式只能覆盖「启动时已存在的连接/端口」（MuMu 走宿主 NAT，新连接会换端口）'
        Write-Warn2 '如果模拟器里的延迟没变化：改用 -Scope global（一定生效，回环 127.0.0.1 不受影响）'
    }
    if ($Scope -eq 'global') {
        Write-Warn2 'global 模式 = outbound and ip，会同时影响宿主机其它程序（本机回环不受影响，DSH 界面安全）'
    }
    $expr = New-ClumsyFilter
    Write-Host "    Scope=$Scope  过滤器：$expr" -ForegroundColor DarkGray
    Write-Host "    延迟 ${Ms}ms  丢包 ${DropPercent}%  限速 ${LimitKB}KB/s  自动结束 ${ClumsyTimeoutSec}s"

    $base = $null
    if ($Verify -and -not $DryRun -and $Ms -gt 0) {
        if (Get-ClumsyProcesses) { Write-Warn2 '已有 clumsy 在跑，基线会被污染（建议先 stop）' }
        Write-Host '    先测实例内基线（ping RTT / TCP 握手）...' -ForegroundColor DarkGray
        $base = Measure-GuestNet -Index $idx
        Write-Host ("    基线：RTT $($base.Rtt) ms，TCP 握手 $($base.Tcp) s") -ForegroundColor DarkGray
    }

    Write-Warn2 'clumsy 需要管理员权限，接下来会弹 UAC，请点「是」'
    $ok = Start-ClumsyOnce -FilterExpr $expr
    if (-not $ok) { return }

    if ($Verify -and -not $DryRun -and $base) {
        $after = Measure-GuestNet -Index $idx
        Write-Host ("    加扰后：RTT $($after.Rtt) ms，TCP 握手 $($after.Tcp) s")
        $dRtt = -1.0; $dTcp = -1.0
        if ($base.Rtt -gt 0 -and $after.Rtt -gt 0) { $dRtt = [math]::Round($after.Rtt - $base.Rtt, 0) }
        if ($base.Tcp -ge 0 -and $after.Tcp -ge 0) { $dTcp = [math]::Round(($after.Tcp - $base.Tcp) * 1000, 0) }
        Write-Host "    增量：RTT ${dRtt} ms，TCP 握手 ${dTcp} ms（期望约 ${Ms} ms）"
        if (($dRtt -ge ($Ms * 0.5)) -or ($dTcp -ge ($Ms * 0.5))) {
            Write-Ok "过滤器命中该实例（Scope=$Scope）"
        } else {
            Write-Err2 "过滤器没抓到该实例的流量：请改用 -Scope global（一定生效）"
        }
    }

    if (-not $DryRun) {
        if ($ClumsyTimeoutSec -gt 0) { Write-Ok "clumsy 将在 $ClumsyTimeoutSec 秒后自动退出并恢复网络" }
        else { Write-Host "    停止：.\mumu-net.ps1 stop" }
    }
    if ($Refresh -and $Scope -eq 'ports' -and -not $DryRun) {
        Write-Head "端口刷新监控（-Refresh）"
        Write-Warn2 '每次重启 clumsy 会有约 1 秒不生效的空档，Ctrl+C 结束'
        $last = ((Get-MuMuVmmPorts).Tcp + (Get-MuMuVmmPorts).Udp) -join ','
        while ($true) {
            Start-Sleep -Seconds 5
            $now = ((Get-MuMuVmmPorts).Tcp + (Get-MuMuVmmPorts).Udp) -join ','
            if ($now -ne $last) {
                $last = $now
                Write-Host ("    [" + (Get-Date -Format 'HH:mm:ss') + "] 端口变化，重启 clumsy 以覆盖新连接") -ForegroundColor Yellow
                $e2 = New-ClumsyFilter
                Start-ClumsyOnce -FilterExpr $e2 | Out-Null
            }
            if (-not (Get-ClumsyProcesses)) { Write-Warn2 'clumsy 已退出（可能是 --timeout 到期），监控结束'; break }
        }
    }
}

function Do-Stop {
    Write-Head "关闭 clumsy"
    $c = Stop-Clumsy
    if ($c -gt 0) { Write-Ok "已关闭 $c 个 clumsy 进程" } else { Write-Ok "clumsy 未在运行" }
}

function Do-Shell {
    $inst = Resolve-Instance
    if (-not $Command) { throw '请用 -Command "..." 指定要在实例内执行的命令' }
    Write-Head "实例 $($inst.index) 内执行"
    $o = Invoke-Guest -Index ([int]$inst.index) -Cmd $Command -TimeoutSec 60
    Write-Host $o
}

# ---------------------------------------------------------------- 分发

switch ($Action) {
    'status'  { Do-Status }
    'probe'   { Do-Probe }
    'offline' { Do-Offline }
    'loss'    { Do-Loss }
    'pps'     { Do-Pps }
    'flap'    { Do-Flap }
    'lag'     { Do-Lag }
    'stop'    { Do-Stop }
    'recover' { Do-Recover }
    'online'  { Do-Recover }
    'shell'   { Do-Shell }
}
