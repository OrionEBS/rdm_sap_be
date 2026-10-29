<#
.SYNOPSIS
  Đóng gói bản KIỂM THỬ chạy bằng cửa sổ console (không IIS, không Windows Service), GIỮ NGUYÊN cấu hình hiện tại.

.DESCRIPTION
  - Rdm.Api publish Release win-x64 framework-dependent, KÈM appsettings.Development.json hiện tại
    (SQL, SAP DEV, JWT, admin) — gói chứa mật khẩu: chỉ chuyển nội bộ cho team kiểm thử, không đưa lên PRD.
  - Chạy môi trường Development; Hangfire server chạy ngay trong process (job hàng loạt chạy luôn trong cửa sổ console).
  - Kèm Start-Api.cmd (kiểm tra .NET 8 / VC++, mở cổng 5200 cho mọi IP) và HUONG-DAN-TEST.txt.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File deploy\Build-ConsoleTest.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [string]$OutputDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts' }
$name = "Rdm_ConsoleTest_$Version"
$staging = Join-Path $OutputDir $name
$zip = "$staging.zip"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host "==> Publish Rdm.Api ($Version)" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\Rdm.Api\Rdm.Api.csproj') -c Release -r win-x64 --self-contained false `
    "-p:InformationalVersion=$Version-test" -o (Join-Path $staging 'api') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish lỗi (exit $LASTEXITCODE)." }

$api = Join-Path $staging 'api'
# Giữ nguyên cấu hình hiện tại (publish không luôn copy file Development) — copy trực tiếp từ mã nguồn.
Copy-Item (Join-Path $root 'src\Rdm.Api\appsettings.Development.json') $api -Force
Remove-Item (Join-Path $api 'web.config'), (Join-Path $api 'dev_nco_rfc.log') -Force -ErrorAction SilentlyContinue
foreach ($f in 'Rdm.Api.exe', 'appsettings.json', 'appsettings.Development.json', 'sapnco.dll', 'sapnco_utils.dll', 'cpc4n.dll', 'ijwhost.dll') {
    if (-not (Test-Path (Join-Path $api $f))) { throw "Thiếu $f trong bản publish." }
}

Copy-Item (Join-Path $PSScriptRoot 'console-test\*') $staging -Force
(Get-Content (Join-Path $staging 'HUONG-DAN-TEST.txt') -Raw -Encoding UTF8).Replace('{{VERSION}}', $Version) |
    Set-Content (Join-Path $staging 'HUONG-DAN-TEST.txt') -Encoding UTF8

. (Join-Path $PSScriptRoot '_Zip.ps1')
New-RdmZip $staging $zip -IncludeBaseDirectory
Write-Host ("`nXong: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
Write-Host 'Gói chứa mật khẩu SQL / SAP DEV / admin — chỉ gửi nội bộ cho team kiểm thử.' -ForegroundColor Yellow
