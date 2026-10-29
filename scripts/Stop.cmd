@echo off
rem Stop.ps1 - chay duoc bang double-click hoac: scripts\Stop.cmd [tham so .ps1, VD -Port 5201]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop.ps1" %*
set RC=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %RC%
