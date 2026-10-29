@echo off
rem Start-Ui.ps1 - chay duoc bang double-click hoac: scripts\Start-Ui.cmd -ApiPort 5210 -Port 5174
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Ui.ps1" %*
set RC=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %RC%
