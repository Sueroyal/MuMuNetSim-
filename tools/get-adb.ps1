<#
.SYNOPSIS
    准备独立的 adb（Android platform-tools），放到 third_party\adb，供构建时打进包体。

.DESCRIPTION
    包体自包含：这样别人的机器上即使没装 MuMu / Android SDK，手机页也能用。
      1) 已存在且未指定 -Force 就跳过；
      2) 依次尝试腾讯镜像 → Google 官方（国内直连常常不通）；
      3) 网络不行时回退：本机 Android SDK / AdbTools / MuMu 自带的 adb（同一个 Google platform-tools）。

.EXAMPLE
    .\tools\get-adb.ps1
    .\tools\get-adb.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$Dest,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Dest) { $Dest = Join-Path $root 'third_party\adb' }

$exe = Join-Path $Dest 'adb.exe'
if ((Test-Path $exe) -and -not $Force) {
    Write-Host ("adb 已就绪：" + $exe + "  " + [math]::Round((Get-Item $exe).Length / 1MB, 2) + " MB")
    exit 0
}
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

$zip = Join-Path $env:TEMP 'platform-tools-latest-windows.zip'
$urls = @(
    'https://mirrors.cloud.tencent.com/AndroidSDK/platform-tools-latest-windows.zip',
    'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
)
foreach ($u in $urls) {
    if (Test-Path $zip) { break }
    try {
        Write-Host "下载 platform-tools：$u"
        Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing -TimeoutSec 120
    }
    catch { Write-Warning ("下载失败：" + $u + "  " + $_.Exception.Message) }
}

if (Test-Path $zip) {
    $tmp = Join-Path $env:TEMP ('pt_' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $src = Get-ChildItem $tmp -Recurse -Filter 'adb.exe' | Select-Object -First 1
    if ($src) {
        Copy-Item $src.FullName $Dest -Force
        foreach ($n in @('AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
            $d = Get-ChildItem $tmp -Recurse -Filter $n | Select-Object -First 1
            if ($d) { Copy-Item $d.FullName $Dest -Force }
        }
    }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $exe)) {
    Write-Warning '网络下载失败，回退用本机已有的 adb（Google platform-tools 的同一份）'
    $cand = @(
        (Join-Path ${env:ProgramFiles(x86)} 'AdbTools\adb\platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
        'C:\Program Files\Netease\MuMu\nx_main\adb.exe'
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $cand) {
        throw "没能获取 adb。请手动把 platform-tools 里的 adb.exe / AdbWinApi.dll / AdbWinUsbApi.dll 放到 $Dest"
    }
    Copy-Item $cand $Dest -Force
    foreach ($n in @('AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
        $d = Join-Path (Split-Path $cand) $n
        if (Test-Path $d) { Copy-Item $d $Dest -Force }
    }
}

if (-not (Test-Path $exe)) { throw "adb 准备失败：$Dest" }
foreach ($n in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
    if (-not (Test-Path (Join-Path $Dest $n))) { Write-Warning "缺少 $n（adb 连接手机可能失败）" }
}
Write-Host ("adb 就绪：" + $exe + "  " + [math]::Round((Get-Item $exe).Length / 1MB, 2) + " MB")
