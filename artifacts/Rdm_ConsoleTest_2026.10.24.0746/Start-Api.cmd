@echo off
rem RDM SAP API - ban kiem thu chay console. Tham so chuyen tiep cho Start-Api.ps1:
rem   Start-Api.cmd                                 HTTPS cong 5200, chung chi tu ky (tu sinh lan dau)
rem   Start-Api.cmd -Port 8443                      HTTPS cong 8443
rem   Start-Api.cmd -Port 8443 -HttpPort 8080       HTTPS 8443 + HTTP 8080
rem   Start-Api.cmd -Port 8443 -PfxPath D:\cert\api.pfx -PfxPassword "***"
rem   Start-Api.cmd -Port 5200 -Http                chi HTTP
rem Log moi lan chay: logs\start-api-*.log (gui file nay khi bao loi)
setlocal
if not exist "%~dp0logs" mkdir "%~dp0logs"
where powershell.exe >nul 2>&1
if errorlevel 1 (
  echo [!] Khong tim thay powershell.exe tren may nay.
  goto :end
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Api.ps1" -NoPause %*
set RC=%errorlevel%
if "%RC%"=="0" goto :end
echo.
echo [!] Ket thuc voi ma loi %RC%. Log: %~dp0logs  va  %~dp0api\logs
if not exist "%~dp0logs\start-api-*.log" (
  echo     Khong co file start-api-*.log: PowerShell khong chay duoc script.
  echo     Neu thay thong bao "running scripts is disabled" / "not digitally signed": may bi Group Policy chan
  echo     PowerShell script - nho IT cho phep, hoac chay truc tiep: api\Rdm.Api.exe ^(xem HUONG-DAN-TEST.txt muc 7^).
)
:end
echo.
pause
