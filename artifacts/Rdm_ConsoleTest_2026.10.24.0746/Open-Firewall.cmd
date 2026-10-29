@echo off
rem Mo cong cho may khac truy cap API (chay bang Run as administrator).
rem   Open-Firewall.cmd            mo cong 5200
rem   Open-Firewall.cmd 8443       mo cong 8443
set PORT=%1
if "%PORT%"=="" set PORT=5200
netsh advfirewall firewall add rule name="RDM API test %PORT%" dir=in action=allow protocol=TCP localport=%PORT%
pause
