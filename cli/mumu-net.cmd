@echo off
rem MuMu weak-network / offline simulator launcher.
rem   Double-click  -> status
rem   mumu-net.cmd offline
rem   mumu-net.cmd loss -Percent 30
rem   mumu-net.cmd lag -Ms 300 -DropPercent 5
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mumu-net.ps1" %*
echo.
pause
