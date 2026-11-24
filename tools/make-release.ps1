<#
.SYNOPSIS
    等 gh 授权完成后，自动创建 GitHub Release 并上传发布包。

.DESCRIPTION
    1) 轮询 gh auth status，直到登录成功（默认最多等 20 分钟）；
    2) 计算两个 zip 的 SHA256，写进 Release 说明；
    3) gh release create（已存在则 upload --clobber）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-release.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-release.ps1 -Tag v1.4 -WaitSec 1800
#>
[CmdletBinding()]
param(
    [string]$Repo = 'Sueroyal/MuMuNetSim-',
    [string]$Tag = 'v1.4',
    [int]$WaitSec = 1200,
    [switch]$SkipAuthWait
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { throw 'gh 未安装（https://cli.github.com/）' }

function Log($s) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $s)
    Write-Host $line
    Add-Content -Path (Join-Path $env:TEMP 'mumunetsim-release.log') -Value $line -Encoding UTF8
}

if (-not $SkipAuthWait) {
    Log "等待 gh 授权（最多 $WaitSec 秒）…"
    $deadline = (Get-Date).AddSeconds($WaitSec)
    $ok = $false
    while ((Get-Date) -lt $deadline) {
        $st = (& $gh auth status 2>&1 | Out-String)
        if ($st -match 'Logged in to github\.com') { $ok = $true; break }
        Start-Sleep -Seconds 5
    }
    if (-not $ok) { Log '超时：仍未授权，已放弃。重新跑一次本脚本即可。'; exit 1 }
    Log '授权成功 ✓'
}

$assets = @(
    (Join-Path $root ("dist\MuMuNetSim-$Tag.zip")),
    (Join-Path $root ("dist\MuMuNetSim-$Tag-single.zip"))
)
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "缺少发布包：$a（先跑 build.ps1 -Package 与 build.ps1 -Single -Package）" } }

# 稳定别名资产：releases/latest/download/<固定名> 永远可下载（发版时同步上传）
$aliasDir = Join-Path $env:TEMP ('mumunetsim_alias_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $aliasDir | Out-Null
$aliasFiles = @()
foreach ($a in $assets) {
    $alias = if ($a -like '*-single.zip') { 'MuMuNetSim-single.zip' } else { 'MuMuNetSim.zip' }
    $dst = Join-Path $aliasDir $alias
    Copy-Item $a $dst -Force
    $aliasFiles += $dst
}
$allFiles = @($assets) + @($aliasFiles)

$hashLines = foreach ($a in $assets) {
    $h = (Get-FileHash $a -Algorithm SHA256).Hash
    "- ``{0}``  ({1:N1} MB)  SHA256: ``{2}``" -f (Split-Path $a -Leaf), ((Get-Item $a).Length / 1MB), $h
}

$notes = @"
## MuMuNetSim $Tag

MuMu 模拟器单实例 / 局域网手机的**弱网、断网、延迟**模拟工具。下载即用，零安装。

### 下载哪个
- **MuMuNetSim-$Tag-single.zip**（推荐）：解压得到**一个 exe**，首次运行自动解包内置的
  adb（platform-tools）与 clumsy/WinDivert。放到共享盘、IM 发同事都行。
- **MuMuNetSim-$Tag.zip**：目录版（exe + ``adb\`` + ``clumsy\`` + 使用说明 + 第三方许可）。

### 能做什么
| 目标 | 能力 |
|---|---|
| MuMu 实例（每个实例独立） | 断网 / 丢包 15%~100% / **自定义延迟 ms（上行、下行可分别设）** |
| 局域网手机（adb 无线调试） | 断网 / 丢包（需 root）/ **时延（免 root，走本机内置延迟代理）** |
| 全局 | clumsy 整机弱网（延迟+丢包） |

- 断网/丢包在设备内部下发 iptables，只影响目标实例/手机，秒级生效，重启即还原；
- 延迟用宿主侧 WinDivert 抓包引擎，按实例的 MuMuVMMHeadless 端口隔离，多实例可同时用不同延迟；
- 「刷新实例」= 一键环境自检（MuMu 路径来源、adb 版本、WinDivert 服务、权限、实例与端口）。

### 使用前提
- Windows 10/11 x64（.NET Framework 4.x 系统自带）
- 首次运行点 UAC「是」（延迟引擎需要 WinDivert 驱动）
- MuMu 装在哪都行（自动探测；找不到会让你手动指定）
- 未签名，SmartScreen 拦一下时点「更多信息 → 仍要运行」

### 固定下载链接（永远指向最新版，适合发群里）
- 单文件版：https://github.com/Sueroyal/MuMuNetSim-/releases/latest/download/MuMuNetSim-single.zip
- 目录版　：https://github.com/Sueroyal/MuMuNetSim-/releases/latest/download/MuMuNetSim.zip

### 校验
$($hashLines -join "`r`n")

日志：``%TEMP%\MuMuNetSim.log``　命令行自检：``MuMuNetSim.exe /selfcheck``
"@

$notesFile = Join-Path $env:TEMP 'mumunetsim_release_notes.md'
[System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))

Log "创建/更新 Release $Tag …"
$exists = (& $gh release view $Tag -R $Repo 2>&1 | Out-String)
if ($exists -match 'not found|release not found') {
    & $gh release create $Tag @allFiles -R $Repo --target main --title "MuMuNetSim $Tag 单文件版 + 目录版" --notes-file $notesFile 2>&1 | ForEach-Object { Log $_ }
}
else {
    Log "Release $Tag 已存在，改为上传/覆盖资产"
    & $gh release upload $Tag @allFiles -R $Repo --clobber 2>&1 | ForEach-Object { Log $_ }
    & $gh release edit $Tag -R $Repo --notes-file $notesFile 2>&1 | ForEach-Object { Log $_ }
}
$view = (& $gh release view $Tag -R $Repo --json url,assets 2>&1 | Out-String)
Log $view
