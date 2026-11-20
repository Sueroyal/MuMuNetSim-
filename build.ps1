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
& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$outExe" /r:System.Windows.Forms.dll /r:System.Drawing.dll $src
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
1. 双击 MuMuNetSim.exe（会请求管理员权限，这是为了让 clumsy 在需要时能静默启动/关闭）。
2. 上面下拉框选择环境：
     正常（不干预网络）
     弱网 · 延迟 200ms + 丢包 5%（clumsy，作用于整机出站）
     弱网 · 丢包 15%（仅该实例，走实例内 iptables）
     断网 · 完全不通（仅该实例）
   选完立即生效，再选「正常」即恢复。关闭窗口也会自动恢复。
3. 「目标实例」下拉框可切换 MuMu 实例（默认第一个运行中的实例），点「刷新」重新扫描。

目录
----
MuMuNetSim.exe      主程序（单文件，.NET Framework 4.x）
clumsy\             官方 clumsy 0.3（延迟/丢包/限速引擎，来自 github.com/jagt/clumsy）

命令行（便于脚本/自动化）
------------------------
MuMuNetSim.exe /apply normal|offline|weak|weak-guest [/instance 4] [/noelevate]
MuMuNetSim.exe /status
MuMuNetSim.exe /where

说明
----
· 「断网」「弱网（丢包）」是在模拟器实例内部下发 iptables 规则实现，只影响该实例，
  并且保留 adb 通道，所以随时可以切回「正常」；规则不落盘，重启实例即清空。
· 「弱网（延迟）」只能由宿主机的 clumsy 实现（Windows 上按进程做延迟需要未公开的驱动能力），
  因此它的过滤器是 outbound and ip：会同时影响宿主机其它程序的出站流量，
  本机回环 127.0.0.1（含桌面端/网页端工具）不受影响。clumsy 10 分钟后会自动退出。
· 日志：%TEMP%\MuMuNetSim.log
'@
Set-Content -Path (Join-Path $dist '使用说明.txt') -Value $txt -Encoding UTF8

Write-Host ""
Write-Host "构建完成：$dist"
Get-ChildItem $dist -Recurse -File | Select-Object @{ n = '文件'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length | Format-Table -Auto
