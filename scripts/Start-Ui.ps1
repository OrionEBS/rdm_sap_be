<#
.SYNOPSIS
  Chạy giao diện quản trị (admin-ui, Vite dev server) gọi tới Rdm.Api ở port chỉ định — chạy nền.

.DESCRIPTION
  Vite proxy /api, /health, /swagger → http://localhost:<ApiPort> (không vướng CORS). Port UI đang có Vite của repo → khởi động lại.
  Log: .run\ui-<port>.log.

.EXAMPLE
  scripts\Start.ps1 -Port 5210 -Target dev ; scripts\Start-Ui.ps1 -ApiPort 5210 -Port 5174   # UI → API → DB Dev
  scripts\Start-Ui.ps1                                                                      # UI 5173 → API 5200 (DB local)
#>
[CmdletBinding()]
param(
    [int]$Port = 5173,
    [int]$ApiPort = 5200
)
. (Join-Path $PSScriptRoot '_Dev.ps1')
$ui = Join-Path $RepoRoot 'admin-ui'

$owner = Get-PortOwner $Port
if ($owner) {
    $mine = @(Get-RdmProcesses -IncludeUi | Where-Object { $_.ProcessId -eq $owner.ProcessId -and $_.Name -eq 'node.exe' })
    if ($mine.Count -eq 0) { throw "Port $Port đang bị ứng dụng khác giữ: $(Format-Proc $owner)." }
    Stop-Processes $mine " (Vite cũ ở port $Port)"
    Start-Sleep -Milliseconds 500
}
if (-not (Get-PortOwner $ApiPort)) { Write-Warn "Port API $ApiPort chưa có ai nghe — chạy scripts\Start.ps1 -Port $ApiPort trước." }
if (-not (Test-Path (Join-Path $ui 'node_modules'))) { Invoke-Checked npm.cmd @('ci', '--no-audit', '--no-fund') $ui }

New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
$log = Join-Path $RunDir "ui-$Port.log"
$env:VITE_API_PROXY = "http://localhost:$ApiPort"
Write-Step "Khởi động admin-ui http://localhost:$Port → API http://localhost:$ApiPort"
$proc = Start-Process cmd.exe -ArgumentList '/c', "npx vite --port $Port --strictPort --host 0.0.0.0 > `"$log`" 2>&1" `
    -WorkingDirectory $ui -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    try { if ((Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { break } } catch { }
    if ($proc.HasExited) { Get-Content $log -Tail 10 -ErrorAction SilentlyContinue; throw 'Vite không khởi động được.' }
    Start-Sleep -Milliseconds 500
}
Write-Ok "admin-ui: http://localhost:$Port   (LAN: http://$($env:COMPUTERNAME):$Port)"
Write-Host "  API qua proxy: http://localhost:$ApiPort · log $log · dừng: scripts\Stop.ps1 -IncludeUi"
