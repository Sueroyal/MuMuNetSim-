<#
.SYNOPSIS
    把当前仓库安全地推到 GitHub（规避两件事：工作区内 git push 失败；--mirror 会误删远端 tag）。

.DESCRIPTION
    1) 本机在工作区里直接 `git push` 会因 pack-objects 子进程找不到仓库而报
       「fatal: not a git repository」，因此先在 %TEMP% 做一次 mirror 克隆再从那里推；
    2) 但 mirror 克隆里没有 GitHub Release 依赖的 tag（那些是 gh / 网页建的），
       `git push --mirror` 会顺手把它们删掉 —— Release 会变成 untagged 打不开。
       所以这里只推分支 refspec，绝不使用 --mirror。
    3) 需要推 tag 时用 -Tags：先 fetch 远端 tag 再 push --tags（--tags 不会删除远端不存在于本地的 tag）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\push.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\push.ps1 -DryRun
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\push.ps1 -Tags
#>
[CmdletBinding()]
param(
    [string]$Repo = 'git@github.com:Sueroyal/MuMuNetSim-.git',
    [string]$Branch = 'main',
    [switch]$Tags,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$mirror = Join-Path $env:TEMP 'mumu-mirror.git'

Write-Host "1) 在 %TEMP% 做 mirror 克隆（绕开工作区 push 的限制）"
if (Test-Path $mirror) { Remove-Item $mirror -Recurse -Force }
git clone --mirror $root $mirror | Out-Null
git -C $mirror remote set-url origin $Repo

$refspec = "refs/heads/${Branch}:refs/heads/${Branch}"
if ($DryRun) {
    Write-Host "2) dry-run：$refspec"
    git -C $mirror push --dry-run origin $refspec
    exit 0
}

Write-Host "2) 只推分支：$refspec（不删远端任何 tag）"
git -C $mirror push origin $refspec

if ($Tags) {
    Write-Host "3) 同步 tag（先 fetch 远端 tag，再 push --tags，不会误删）"
    git -C $mirror fetch --tags origin
    git -C $mirror push origin --tags
}

Write-Host "完成。远端 refs："
git ls-remote $Repo "refs/heads/$Branch" 'refs/tags/*'
