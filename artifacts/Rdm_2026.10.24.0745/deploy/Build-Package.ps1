<#
.SYNOPSIS
  Đóng gói Rdm.Api (IIS) + Rdm.Worker (Windows Service) thành 1 file zip để triển khai lên Windows Server 2019.

.DESCRIPTION
  Chạy trên máy build (có .NET 8 SDK, mã nguồn E:\rdmapi):
    1. dotnet test (bỏ qua bằng -SkipTests)
    2. dotnet publish Rdm.Api, Rdm.Worker: Release, win-x64, framework-dependent (máy chủ cài .NET 8 Hosting Bundle)
    3. Bỏ appsettings.Development.json (chứa mật khẩu DEV) — cấu hình production nằm trên máy chủ, không nằm trong gói
    4. Kiểm tra đủ SAP NCo 3.1 (sapnco.dll, sapnco_utils.dll, cpc4n.dll, ijwhost.dll) trong cả 2 ứng dụng
    5. Gói kèm script triển khai + mẫu cấu hình → artifacts\Rdm_<version>.zip (+ .sha256)

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File deploy\Build-Package.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File deploy\Build-Package.ps1 -SkipTests -Version 2026.10.23.1
#>
[CmdletBinding()]
param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [string]$OutputDir = '',   # mặc định: <repo>/artifacts
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path $PSScriptRoot) 'artifacts' }
$root = Split-Path $PSScriptRoot
$staging = Join-Path $OutputDir "Rdm_$Version"
$zip = Join-Path $OutputDir "Rdm_$Version.zip"

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments -join ' ') lỗi (exit $LASTEXITCODE)." }
}

Step "Đóng gói phiên bản $Version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

if (-not $SkipTests) {
    Step 'Unit test'
    Invoke-Dotnet @('test', (Join-Path $root 'tests\Rdm.Tests'), '-c', 'Release', '--nologo', '-v', 'q')
}

$apps = @(
    @{ Name = 'api';    Project = 'src\Rdm.Api\Rdm.Api.csproj';       Exe = 'Rdm.Api.exe' },
    @{ Name = 'worker'; Project = 'src\Rdm.Worker\Rdm.Worker.csproj'; Exe = 'Rdm.Worker.exe' }
)
foreach ($app in $apps) {
    Step "Publish $($app.Project)"
    $out = Join-Path $staging $app.Name
    Invoke-Dotnet @('publish', (Join-Path $root $app.Project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        "-p:InformationalVersion=$Version", '-o', $out, '--nologo', '-v', 'q')

    # Không bao giờ đưa cấu hình / log DEV vào gói.
    Get-ChildItem $out -Include 'appsettings.Development.json', 'appsettings.*.local.json', 'dev_nco_rfc.log' -Recurse -File |
        Remove-Item -Force
    if (Test-Path (Join-Path $out 'logs')) { Remove-Item (Join-Path $out 'logs') -Recurse -Force }

    $required = @($app.Exe, 'sapnco.dll', 'sapnco_utils.dll', 'cpc4n.dll', 'ijwhost.dll', 'System.Configuration.ConfigurationManager.dll', 'appsettings.json')
    if ($app.Name -eq 'api') { $required += 'web.config' }
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $out $_)) })
    if ($missing.Count -gt 0) { throw "$($app.Name): thiếu $($missing -join ', ') trong bản publish." }
    Write-Host "  OK: $($required -join ', ')"
}

Step 'Script triển khai + mẫu cấu hình'
New-Item -ItemType Directory -Path (Join-Path $staging 'deploy'), (Join-Path $staging 'config') | Out-Null
Copy-Item (Join-Path $PSScriptRoot '*.ps1') (Join-Path $staging 'deploy')
Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $staging 'deploy')
Copy-Item (Join-Path $PSScriptRoot 'config\appsettings.Production.example.json') (Join-Path $staging 'config')

$manifest = [ordered]@{
    product     = 'RDM SAP Integration (Rdm.Api + Rdm.Worker)'
    version     = $Version
    builtAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
    builtOn     = $env:COMPUTERNAME
    dotnet      = (& dotnet --version)
    runtime     = 'win-x64, framework-dependent (.NET 8 Hosting Bundle)'
    sapNco      = (Get-Item (Join-Path $staging 'api\sapnco.dll')).VersionInfo.FileVersion
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $staging 'manifest.json') -Encoding UTF8

Step "Nén $zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
. (Join-Path $PSScriptRoot '_Zip.ps1')
New-RdmZip $staging $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ASCII

Write-Host ("`nXong: {0} ({1:N1} MB)`nSHA256: {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash) -ForegroundColor Green
Write-Host "Triển khai: copy zip lên máy chủ, giải nén, chạy (Administrator):"
Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File <thư mục giải nén>\deploy\Deploy-Rdm.ps1"
