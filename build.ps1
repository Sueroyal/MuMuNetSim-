<#
.SYNOPSIS
    构建 MuMuNetSim：编译单文件 WinForms exe，并把 clumsy 一起组装到 dist\MuMuNetSim。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipClumsy
#>
[CmdletBinding()]
param(
    [switch]$SkipClumsy,
    [switch]$ForceClumsy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist\MuMuNetSim'
$src = Join-Path $root 'src\MuMuNetSim.cs'
$outExe = Join-Path $dist 'MuMuNetSim.exe'

function Find-Csc {
    $cands = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe')
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    throw '找不到 csc.exe（需要 .NET Framework 4.x）'
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "[1/3] 编译 $src"
$csc = Find-Csc
& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$outExe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll $src
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Write-Host ("      -> " + $outExe + "  " + (Get-Item $outExe).Length + " bytes")

if (-not $SkipClumsy) {
    Write-Host "[2/3] 组装 clumsy"
    $cargs = @{ Dest = (Join-Path $dist 'clumsy') }
    if ($ForceClumsy) { $cargs['Force'] = $true }
    & (Join-Path $root 'tools\get-clumsy.ps1') @cargs
}
else {
    Write-Host "[2/3] 跳过 clumsy（-SkipClumsy）"
}

Write-Host '[3/3] 生成使用说明'
$txt = @'
MuMu 弱网 / 断网模拟器 (MuMuNetSim)
=====================================

用法
----
1. 双击 MuMuNetSim.exe（首次会请求管理员权限：延迟引擎用 WinDivert 驱动，必须提权；
   提权一次后 clumsy 也能静默启动/关闭）。
2. 表格里每个实例一行，可分别预设：
     预设环境   ：正常 / 丢包 15%、25%、35%、45%、55%、65%、75%、80%、90%、100% / 断网
                  （100% 表示出方向全丢，效果接近断网但不拦入方向）
     延迟ms     ：自己填，0 表示不加延迟（1~10000）
     上行/下行  ：勾选延迟作用的方向（只勾一个就是单向延迟）
   改完立刻生效；实例没启动也没关系，启动后会自动套用。
3. 行尾「操作」列可以一键启动 / 关闭该实例；「全部恢复正常」清空所有实例的网络干预。
4. 关闭窗口会自动恢复正常（清 iptables 规则 + 关延迟引擎 + 关 clumsy）。

延迟是怎么做的（重要）
----------------------
· 断网 / 丢包：在模拟器实例内部下发 iptables 规则，只影响这一个实例，秒级生效，
  规则不落盘（重启实例即清空），并且始终放行 adb 通道，所以随时能恢复。
· 延迟：宿主机侧用 WinDivert（clumsy 同款驱动，随本程序分发）抓包再延时重发。
  每个实例一个独立引擎，按「该实例 VMM 进程(MuMuVMMHeadless)占用的 TCP/UDP 端口」过滤，
  所以多个实例可以同时设不同延迟；新出现的连接最多 3 秒后自动纳入。
  注意：模拟器内部的 ping(ICMP) 不走 NAT 端口，因此不会被延迟，游戏/应用的 TCP/UDP 会。
· 「全局弱网(clumsy)」是整机出站的粗粒度选项，和每实例延迟互斥（会互相顶掉）。

目录
----
MuMuNetSim.exe      主程序（单文件，.NET Framework 4.x）
clumsy\             官方 clumsy 0.3 + WinDivert 2.2（延迟引擎用这里的 WinDivert.dll/.sys）
MuMuNetSim.ini      你的预设（每个实例的 预设环境 / 延迟 / 上下行，自动保存）

命令行（便于脚本/自动化）
------------------------
MuMuNetSim.exe /apply normal|offline|loss15|loss45|loss80 [/instance 4] [/noelevate]
MuMuNetSim.exe /delay /instance 4 /ms 300 /dir both|up|down [/seconds 15]
MuMuNetSim.exe /delay /filter "icmp and ip.DstAddr == 8.8.8.8" /ms 800 /dir up /seconds 20
MuMuNetSim.exe /ports          # 只看每实例构造出来的 WinDivert 过滤器（不用提权）
MuMuNetSim.exe /status
MuMuNetSim.exe /where

日志：%TEMP%\MuMuNetSim.log
'@
Set-Content -Path (Join-Path $dist '使用说明.txt') -Value $txt -Encoding UTF8

Write-Host ""
Write-Host "构建完成：$dist"
Get-ChildItem $dist -Recurse -File | Select-Object @{ n = '文件'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length | Format-Table -Auto
