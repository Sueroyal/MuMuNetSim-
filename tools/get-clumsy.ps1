<#
.SYNOPSIS
    获取 clumsy 0.3 官方发行版二进制，放进 <Dest>（默认 dist\MuMuNetSim\clumsy）。

.DESCRIPTION
    1) 若目标已存在 clumsy.exe 且未指定 -Force，直接跳过；
    2) 从 GitHub 官方 release 下载 clumsy-0.3-win64-a.zip 并解包；
    3) 下载失败时回退：复制本机已有的 clumsy 安装目录；
    4) 校验 clumsy.exe 里是否含本工具依赖的命令行参数（--lag-time / --drop-chance /
       --bandwidth-bandwidth / --timeout / --filter），版本不匹配会给出警告。

.EXAMPLE
    .\tools\get-clumsy.ps1 -Dest .\dist\MuMuNetSim\clumsy
    .\tools\get-clumsy.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$Dest,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Dest) { $Dest = Join-Path $root 'dist\MuMuNetSim\clumsy' }

$exe = Join-Path $Dest 'clumsy.exe'
if ((Test-Path $exe) -and -not $Force) {
    Write-Host "clumsy 已就绪：$exe"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

$url = 'https://github.com/jagt/clumsy/releases/download/0.3/clumsy-0.3-win64-a.zip'
$zip = Join-Path $env:TEMP 'clumsy-0.3-win64-a.zip'
try {
    if (-not (Test-Path $zip)) {
        Write-Host "下载 $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    }
    Expand-Archive -Path $zip -DestinationPath $Dest -Force
    Write-Host "已解包官方 clumsy 0.3 -> $Dest"
}
catch {
    Write-Warning "下载/解包失败：$($_.Exception.Message)"
    $local = @('D:\clumsy', 'C:\clumsy', (Join-Path $env:ProgramFiles 'clumsy')) |
        Where-Object { Test-Path (Join-Path $_ 'clumsy.exe') } | Select-Object -First 1
    if (-not $local) {
        throw "没能获取 clumsy。请手动把 clumsy.exe / WinDivert.dll / WinDivert64.sys 放进 $Dest"
    }
    Copy-Item (Join-Path $local '*') $Dest -Recurse -Force
    Write-Host "已复制本机 clumsy：$local"
}

if (-not (Test-Path $exe)) { throw "$Dest 里没有 clumsy.exe" }
foreach ($f in @('WinDivert.dll', 'WinDivert64.sys')) {
    if (-not (Test-Path (Join-Path $Dest $f))) { Write-Warning "缺少 $f，clumsy 可能无法抓包" }
}

# 校验命令行参数（MuMuNetSim 依赖它们）
$ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($exe))
$need = @('lag-time', 'drop-chance', 'bandwidth-bandwidth', 'timeout', 'filter')
$missing = @($need | Where-Object { $ascii -notmatch [regex]::Escape($_) })
if ($missing.Count -gt 0) {
    Write-Warning ("clumsy 中未找到这些参数：" + ($missing -join ', ') + "（版本可能不匹配，弱网参数会不生效）")
}
else {
    Write-Host ("clumsy 参数校验通过：" + ($need -join ', '))
}
