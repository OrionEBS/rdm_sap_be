<#
.SYNOPSIS
  Biên dịch toàn bộ solution (+ admin-ui) trên máy dev. Tắt trước các tiến trình RDM đang chạy (khóa file trong bin\).

.EXAMPLE
  scripts\Build.ps1                       # Debug: tắt tiến trình → dotnet build → admin-ui
  scripts\Build.ps1 -Test                 # + unit test
  scripts\Build.ps1 -Configuration Release -SkipUi
  scripts\Build.ps1 -Start -Port 5201     # build xong khởi động API ở port 5201
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$SkipUi,
    # Build xong thì khởi động API (Start.ps1).
    [switch]$Start,
    [int]$Port = 5200
)
. (Join-Path $PSScriptRoot '_Dev.ps1')
$started = Get-Date

Write-Step 'Tắt tiến trình RDM đang chạy'
Stop-RdmProcesses

Write-Step "dotnet build ($Configuration)"
Invoke-Checked dotnet @('build', 'Rdm.SapIntegration.sln', '-c', $Configuration, '--nologo', '-v', 'q', '-clp:ErrorsOnly;Summary')

if ($Test) {
    Write-Step 'Unit test'
    Invoke-Checked dotnet @('test', 'tests\Rdm.Tests', '-c', $Configuration, '--no-build', '--nologo', '-v', 'q')
}

if (-not $SkipUi) {
    Write-Step 'admin-ui (React) — npm run build'
    $ui = Join-Path $RepoRoot 'admin-ui'
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) { Invoke-Checked npm.cmd @('ci', '--no-audit', '--no-fund') $ui }
    Invoke-Checked npm.cmd @('run', 'build') $ui
}

Write-Host ("`nBuild xong ({0:N0} giây)." -f ((Get-Date) - $started).TotalSeconds) -ForegroundColor Green
if ($Start) { & (Join-Path $PSScriptRoot 'Start.ps1') -Port $Port -Configuration $Configuration -NoBuild }
