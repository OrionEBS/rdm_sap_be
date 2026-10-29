<#
.SYNOPSIS
  Dừng tiến trình RDM của repo trên máy dev (không đụng HRM.API, IIS hay ứng dụng khác).

.EXAMPLE
  scripts\Stop.ps1 -List           # chỉ liệt kê
  scripts\Stop.ps1 -Port 5201      # dừng API ở port 5201
  scripts\Stop.ps1                 # dừng mọi Rdm.Api / Rdm.Worker / Rdm.DbSync / dotnet run của repo
  scripts\Stop.ps1 -IncludeUi      # + Vite dev server của admin-ui
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$IncludeUi,
    [switch]$List
)
. (Join-Path $PSScriptRoot '_Dev.ps1')

if ($List) {
    $all = @(Get-RdmProcesses -IncludeUi)
    if ($all.Count -eq 0) { Write-Host 'Không có tiến trình RDM nào đang chạy.' }
    $all | ForEach-Object { Write-Host "  $(Format-Proc $_)" }
    exit 0
}
if ($Port -gt 0) {
    $running = Get-RdmApiOnPort $Port
    Stop-Processes $running " ở port $Port"
    Remove-Item (Join-Path $RunDir "api-$Port.pid") -Force -ErrorAction SilentlyContinue
}
else {
    Write-Step 'Dừng tiến trình RDM'
    Stop-RdmProcesses -IncludeUi:$IncludeUi
}
