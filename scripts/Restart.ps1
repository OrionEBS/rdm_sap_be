<#
.SYNOPSIS
  Khởi động lại Rdm.Api trên máy dev: dừng API đang chạy ở port → (tùy chọn) build lại → khởi động ở port đó.

.EXAMPLE
  scripts\Restart.ps1                  # port 5200
  scripts\Restart.ps1 -Port 5201
  scripts\Restart.ps1 -Build           # sửa code xong: dừng MỌI tiến trình RDM (để nhả file bin\), build, khởi động
#>
[CmdletBinding()]
param(
    [int]$Port = 5200,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Build,
    [string]$BindAddress = '0.0.0.0',
    [switch]$Https,
    [switch]$NoHangfireServer,
    # Target (config\targets\<tên>.json). Bỏ trống = dùng lại target của lần chạy trước ở port này; 'none' = bỏ target.
    [string]$Target = ''
)
. (Join-Path $PSScriptRoot '_Dev.ps1')
$targetMemo = Join-Path $RunDir "api-$Port.target"
if (-not $Target -and (Test-Path $targetMemo)) { $Target = (Get-Content $targetMemo -Raw).Trim() }
if ($Target -eq 'none') { $Target = '' }

if ($Build) {
    # bin\ dùng chung cho mọi instance → phải dừng hết mới build được.
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -SkipUi
}
else {
    Write-Step "Dừng Rdm.Api ở port $Port"
    $running = Get-RdmApiOnPort $Port
    Stop-Processes $running " ở port $Port"
    Remove-Item (Join-Path $RunDir "api-$Port.pid") -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-PortOwner $Port) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
}

$p = @{ Port = $Port; Configuration = $Configuration; BindAddress = $BindAddress; NoBuild = $true }
if ($Https) { $p.Https = $true }
if ($NoHangfireServer) { $p.NoHangfireServer = $true }
if ($Target) { $p.Target = $Target }
& (Join-Path $PSScriptRoot 'Start.ps1') @p
