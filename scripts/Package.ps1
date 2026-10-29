<#
.SYNOPSIS
  Đóng gói tất cả bản phát hành vào artifacts\ — tắt trước các tiến trình RDM đang chạy.

.DESCRIPTION
  1. Tắt tiến trình RDM của repo (API / worker / DbSync, kể cả gói console-test đang chạy trong artifacts\)
  2. deploy\Build-Package.ps1   → Rdm_<ver>.zip          (IIS + Worker + DbSync + ảnh dữ liệu, có unit test)
  3. deploy\Build-ConsoleTest.ps1 → Rdm_ConsoleTest_<ver>.zip (chạy console cho team kiểm thử)
  4. admin-ui                   → RdmAdminUi_<ver>.zip
  Cả 3 gói cùng 1 số phiên bản.

.EXAMPLE
  scripts\Package.ps1
  scripts\Package.ps1 -Only Deploy -SkipTests -NoData
  scripts\Package.ps1 -Only ConsoleTest,AdminUi
#>
[CmdletBinding()]
param(
    [ValidateSet('Deploy', 'ConsoleTest', 'AdminUi')][string[]]$Only = @('Deploy', 'ConsoleTest', 'AdminUi'),
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd.HHmm'),
    [switch]$SkipTests,
    # Gói Deploy không kèm ảnh dữ liệu DB.
    [switch]$NoData
)
. (Join-Path $PSScriptRoot '_Dev.ps1')
$started = Get-Date
$artifacts = Join-Path $RepoRoot 'artifacts'

Write-Step 'Tắt tiến trình RDM đang chạy'
Stop-RdmProcesses

$made = New-Object System.Collections.Generic.List[string]
if ('Deploy' -in $Only) {
    Write-Step "Gói triển khai IIS + Worker ($Version)"
    $p = @{ Version = $Version }
    if ($SkipTests) { $p.SkipTests = $true }
    if ($NoData) { $p.NoData = $true }
    & (Join-Path $RepoRoot 'deploy\Build-Package.ps1') @p
    $made.Add((Join-Path $artifacts "Rdm_$Version.zip"))
}
if ('ConsoleTest' -in $Only) {
    Write-Step "Gói console-test ($Version)"
    & (Join-Path $RepoRoot 'deploy\Build-ConsoleTest.ps1') -Version $Version
    $made.Add((Join-Path $artifacts "Rdm_ConsoleTest_$Version.zip"))
}
if ('AdminUi' -in $Only) {
    Write-Step "Gói admin-ui ($Version)"
    $ui = Join-Path $RepoRoot 'admin-ui'
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) { Invoke-Checked npm.cmd @('ci', '--no-audit', '--no-fund') $ui }
    Invoke-Checked npm.cmd @('run', 'build') $ui
    . (Join-Path $RepoRoot 'deploy\_Zip.ps1')
    $zip = Join-Path $artifacts "RdmAdminUi_$Version.zip"
    New-RdmZip (Join-Path $ui 'dist') $zip
    $made.Add($zip)
}

Write-Host ("`nĐóng gói xong ({0:N0} giây):" -f ((Get-Date) - $started).TotalSeconds) -ForegroundColor Green
foreach ($z in $made) {
    if (Test-Path $z) { Write-Host ("  {0}  ({1:N1} MB)" -f $z, ((Get-Item $z).Length / 1MB)) }
    else { Write-Warn "không thấy $z" }
}
Write-Host 'Khởi động lại API dev: scripts\Start.ps1 [-Port 5200]'
