<#
.SYNOPSIS
    构建 MuMuNetSim。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1                # 目录版：dist\MuMuNetSim\（exe + clumsy\）
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Package       # 目录版 + dist\MuMuNetSim-<版本>.zip
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Single        # 单文件版：dist\single\MuMuNetSim.exe（内置 clumsy/WinDivert，运行时自解包）
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Single -Package
#>
[CmdletBinding()]
param(
    [switch]$SkipClumsy,
    [switch]$ForceClumsy,
    [switch]$Package,
    [switch]$Single
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist\MuMuNetSim'
$src = Join-Path $root 'src\MuMuNetSim.cs'
$outExe = Join-Path $dist 'MuMuNetSim.exe'
$singleDir = Join-Path $root 'dist\single'
$singleExe = Join-Path $singleDir 'MuMuNetSim.exe'

# 版本号从源码标题抓，避免两处不一致
$ver = 'v0.0'
$m = Select-String -Path $src -Pattern 'TITLE\s*=\s*"[^"]*?(v[\d.]+)"' | Select-Object -First 1
if ($m -and $m.Matches.Count -gt 0) { $ver = $m.Matches[0].Groups[1].Value }

function Find-Csc {
    foreach ($c in @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'))) {
        if (Test-Path $c) { return $c }
    }
    throw '找不到 csc.exe（需要 .NET Framework 4.x）'
}
$csc = Find-Csc

# ---------------------------------------------------------------- 使用说明
$readmeText = @'
MuMu 弱网 / 断网模拟器 (MuMuNetSim)
=====================================

用法
----
1. 双击 MuMuNetSim.exe（首次会请求管理员权限：延迟引擎用 WinDivert 驱动，必须提权）。
2. 两个页签：
   [MuMu 实例] 表格里每个实例一行，可分别预设：预设环境（正常/丢包档位/断网）、延迟ms（自己填）、
              上行/下行勾选；实例没启动也能先设好，启动后自动套用。行尾按钮可一键启停实例。
   [手机 / 局域网设备] 填 设备IP:端口（adb 无线调试）→ 连接 → 显示型号/Android/root →
              预设环境：正常 / 丢包 / 时延 / 时延+丢包 / 断网，丢包% 与时延ms 可填任意值，
              上行/下行可分别勾选 → 应用。关窗口或点「恢复正常」都会清干净。
3. 「全部恢复正常」清空所有实例的网络干预。

三种干预怎么做的
----------------
· 断网 / 丢包（MuMu 实例）：在模拟器实例内部下发 iptables，只影响该实例，秒级生效，
  规则不落盘（重启实例即清空），并始终放行 adb 通道，所以随时能恢复。
· 延迟（MuMu 实例）：宿主机侧用 WinDivert（clumsy 同款驱动）抓包后再延时重发，每个实例一个
  独立引擎，按该实例 MuMuVMMHeadless 进程占用的 TCP/UDP 端口过滤，多实例可同时设不同延迟。
· 手机：丢包/断网走手机内部 iptables（独立链，需要 root）；时延走「本机 HTTP(S) 延迟代理」，
  免 root，通过 adb 设置手机全局代理指向本机。注意只有遵守系统代理的流量受影响
  （浏览器/WebView/多数 App 网络库；游戏原始 UDP 可能不生效）。

分发说明
--------
· 目录版：整个 MuMuNetSim 文件夹拷给对方，双击 exe 即可；clumsy 与 WinDivert 在 clumsy\ 子目录。
· 单文件版：只有一个 MuMuNetSim.exe，首次运行会把内置的 clumsy/WinDivert 解包到同目录 clumsy\；
  如果 exe 放在只读位置（如网络共享、Program Files），会自动改解包到
  %LOCALAPPDATA%\MuMuNetSim\，配置也写在那里。
· 无需安装任何运行时：.NET Framework 4.x 是 Win10/11 自带。
· 对方机器上需要有 MuMu Player 12（程序会自动找 MuMuManager.exe）；手机功能只依赖 adb 与手机调试授权。
· 每个实例/手机的预设都存在 IniPath 里（默认 exe 同目录 MuMuNetSim.ini，只读时在 %LOCALAPPDATA%），
  可以把这份 ini 拷给同事，等于把「一套网络配方」发出去。
· 未签名 exe 首次运行可能被 SmartScreen 拦一下：点「更多信息」→「仍要运行」。

命令行
------
MuMuNetSim.exe /apply normal|offline|loss15|loss45|loss80|loss100 [/instance 4] [/noelevate]
MuMuNetSim.exe /delay /instance 4 /ms 300 /dir both|up|down [/seconds 15]
MuMuNetSim.exe /phone /ip 192.168.1.50:5555 /preset delay|loss|delayloss|offline|normal [/ms 300] [/loss 30] [/dir both]
MuMuNetSim.exe /ports      # 打印每个实例构造出来的 WinDivert 过滤器（免提权）
MuMuNetSim.exe /status /where

日志：%TEMP%\MuMuNetSim.log
'@

function Get-ClumsyAssets([string]$Dest) {
    if (-not (Test-Path (Join-Path $Dest 'clumsy.exe'))) {
        & (Join-Path $root 'tools\get-clumsy.ps1') -Dest $Dest
    }
    foreach ($f in @('clumsy.exe', 'WinDivert.dll', 'WinDivert64.sys', 'config.txt', 'License.txt')) {
        if (-not (Test-Path (Join-Path $Dest $f))) { throw "缺少 $f（$Dest）" }
    }
}

function Get-AdbAssets {
    $dest = Join-Path $root 'third_party\adb'
    & (Join-Path $root 'tools\get-adb.ps1') -Dest $dest
    foreach ($f in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
        if (-not (Test-Path (Join-Path $dest $f))) { throw "缺少 $f（$dest）" }
    }
    return $dest
}

# 第三方组件许可（随包分发，必须保留）
$thirdPartyText = @'
本程序（MuMuNetSim）自身：MIT License，见仓库 LICENSE。

随包分发的第三方组件
--------------------
1) clumsy 0.3
   https://github.com/jagt/clumsy
   MIT License, Copyright (c) 2013-2023 James Chen (jagt)
   用途：全局弱网（clumsy.exe）；本程序的每实例延迟引擎复用其中的 WinDivert。
   文件：clumsy\clumsy.exe、clumsy\config.txt、clumsy\License.txt

2) WinDivert 2.2
   https://github.com/basil00/Divert
   LGPL v3 / GPL v2 双许可
   用途：宿主侧抓包/延迟引擎（WinDivert.dll 提供 API，WinDivert64.sys 为内核驱动，
        首次以管理员身份开启延迟时由 WinDivert.dll 自动安装为服务）。
   文件：clumsy\WinDivert.dll、clumsy\WinDivert64.sys

3) Android platform-tools (adb)
   https://developer.android.com/tools/releases/platform-tools
   Apache License 2.0（Android SDK Terms）
   用途：手机页 adb over TCP 连接与调试授权。为 Google 官方 platform-tools 的原样副本。
   文件：adb\adb.exe、adb\AdbWinApi.dll、adb\AdbWinUsbApi.dll
'@

# 生成 exe 图标（借用 clumsy 的图标，避免仓库里再放一份二进制）
$iconPath = Join-Path $env:TEMP 'mumunetsim_build.ico'
function Get-IconArgs {
    try {
        Add-Type -AssemblyName System.Drawing
        $clumsyExe = Join-Path $dist 'clumsy\clumsy.exe'
        if (Test-Path $clumsyExe) {
            $ic = [System.Drawing.Icon]::ExtractAssociatedIcon($clumsyExe)
            if ($ic) {
                $fs = [System.IO.File]::Create($iconPath)
                $ic.Save($fs)
                $fs.Close()
                return @("/win32icon:$iconPath")
            }
        }
    }
    catch { Write-Warning "图标生成失败（不影响功能）：$($_.Exception.Message)" }
    return @()
}

# ---------------------------------------------------------------- 单文件版
if ($Single) {
    Write-Host "[单文件版] 准备内置资产（clumsy + WinDivert + adb + 文档）"
    $assetSrc = Join-Path $dist 'clumsy'
    Get-ClumsyAssets $assetSrc
    $adbSrc = Get-AdbAssets
    $readmeTmp = Join-Path $env:TEMP ('mumunetsim_readme_' + [guid]::NewGuid().ToString('N') + '.txt')
    Set-Content -Path $readmeTmp -Value $readmeText -Encoding UTF8
    $tpTmp = Join-Path $env:TEMP ('mumunetsim_thirdparty_' + [guid]::NewGuid().ToString('N') + '.txt')
    Set-Content -Path $tpTmp -Value $thirdPartyText -Encoding UTF8

    New-Item -ItemType Directory -Force -Path $singleDir | Out-Null
    if (Test-Path $singleExe) { Remove-Item $singleExe -Force }
    Write-Host "[单文件版] 编译 $singleExe"
    $iconArgs = Get-IconArgs
    & $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$singleExe" `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
        "/resource:$assetSrc\clumsy.exe,asset.clumsy.exe" `
        "/resource:$assetSrc\WinDivert.dll,asset.WinDivert.dll" `
        "/resource:$assetSrc\WinDivert64.sys,asset.WinDivert64.sys" `
        "/resource:$assetSrc\config.txt,asset.config.txt" `
        "/resource:$assetSrc\License.txt,asset.clumsy-license.txt" `
        "/resource:$adbSrc\adb.exe,asset.adb.exe" `
        "/resource:$adbSrc\AdbWinApi.dll,asset.AdbWinApi.dll" `
        "/resource:$adbSrc\AdbWinUsbApi.dll,asset.AdbWinUsbApi.dll" `
        "/resource:$tpTmp,asset.thirdparty.txt" `
        "/resource:$readmeTmp,asset.readme.txt" `
        $iconArgs $src
    Remove-Item $readmeTmp, $tpTmp -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
    $size = [math]::Round((Get-Item $singleExe).Length / 1KB, 1)
    Write-Host "      -> $singleExe  $size KB"

    if ($Package) {
        $zip = Join-Path $root ('dist\MuMuNetSim-' + $ver + '-单文件版.zip')
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path $singleExe -DestinationPath $zip -CompressionLevel Optimal
        Write-Host ("      -> 已打包 " + $zip + "  " + [math]::Round((Get-Item $zip).Length / 1KB, 1) + " KB")
    }
    Write-Host ""
    Write-Host "构建完成（$ver 单文件版）：$singleExe"
    return
}

# ---------------------------------------------------------------- 目录版
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $SkipClumsy) {
    Write-Host "[1/4] 组装 clumsy + adb"
    $cargs = @{ Dest = (Join-Path $dist 'clumsy') }
    if ($ForceClumsy) { $cargs['Force'] = $true }
    & (Join-Path $root 'tools\get-clumsy.ps1') @cargs
    $adbSrc = Get-AdbAssets
    $adbDst = Join-Path $dist 'adb'
    New-Item -ItemType Directory -Force -Path $adbDst | Out-Null
    Copy-Item (Join-Path $adbSrc 'adb.exe') $adbDst -Force
    Copy-Item (Join-Path $adbSrc 'AdbWinApi.dll') $adbDst -Force
    Copy-Item (Join-Path $adbSrc 'AdbWinUsbApi.dll') $adbDst -Force
}
else {
    Write-Host "[1/4] 跳过 clumsy/adb（-SkipClumsy）"
}

Write-Host "[2/4] 编译 $src"
$iconArgs = Get-IconArgs
& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$outExe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll $iconArgs $src
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Write-Host ("      -> " + $outExe + "  " + (Get-Item $outExe).Length + " bytes")

Write-Host '[3/4] 生成使用说明与第三方许可'
Set-Content -Path (Join-Path $dist '使用说明.txt') -Value $readmeText -Encoding UTF8
Set-Content -Path (Join-Path $dist '第三方组件与许可.txt') -Value $thirdPartyText -Encoding UTF8

if ($Package) {
    Write-Host "[4/4] 打包发布 zip（不含本机 MuMuNetSim.ini）"
    $stage = Join-Path $root 'dist\pkg\MuMuNetSim'
    if (Test-Path (Join-Path $root 'dist\pkg')) { Remove-Item (Join-Path $root 'dist\pkg') -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $outExe $stage -Force
    Copy-Item (Join-Path $dist '使用说明.txt') $stage -Force
    Copy-Item (Join-Path $dist '第三方组件与许可.txt') $stage -Force
    Copy-Item (Join-Path $dist 'clumsy') (Join-Path $stage 'clumsy') -Recurse -Force
    if (Test-Path (Join-Path $dist 'adb')) { Copy-Item (Join-Path $dist 'adb') (Join-Path $stage 'adb') -Recurse -Force }
    $zip = Join-Path $root ('dist\MuMuNetSim-' + $ver + '.zip')
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("      -> " + $zip + "  " + [math]::Round((Get-Item $zip).Length / 1KB, 1) + " KB")
}

Write-Host ""
Write-Host "构建完成（$ver）：$dist"
Get-ChildItem $dist -Recurse -File | Select-Object @{ n = '文件'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length | Format-Table -Auto
