@echo off
rem Start.ps1 - chay duoc bang double-click hoac: scripts\Start.cmd [tham so .ps1, VD -Port 5201]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1" %*
set RC=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %RC%
