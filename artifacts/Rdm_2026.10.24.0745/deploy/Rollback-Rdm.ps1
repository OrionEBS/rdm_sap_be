<#
.SYNOPSIS
  Quay về bản sao lưu trước (do Deploy-Rdm.ps1 tạo trong <InstallRoot>\backups) cho Rdm.Api + Rdm.Worker.

.DESCRIPTION
  Giữ nguyên config\appsettings.Production.json và thư mục logs. KHÔNG hoàn tác thay đổi CSDL (EF migration của bản mới):
  migration của dự án chỉ THÊM cột / procedure nên bản cũ vẫn chạy được trên schema mới.

.EXAMPLE
  .\Rollback-Rdm.ps1                      # bản sao lưu mới nhất
  .\Rollback-Rdm.ps1 -Backup 20261023-0915
  .\Rollback-Rdm.ps1 -List
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\Apps\Rdm',
    [string]$Backup = '',
    [string]$SiteName = 'RdmApi',
    [string]$AppPoolName = 'RdmApi',
    [string]$ServiceName = 'RdmWorker',
    [int]$HttpPort = 8080,
    [string]$HostName = '',
    [switch]$List,
    [switch]$Force
)
. (Join-Path $PSScriptRoot '_Common.ps1')
Assert-Admin
$paths = Get-RdmPaths $InstallRoot

$backups = @(Get-ChildItem $paths.Backups -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
if ($List -or $backups.Count -eq 0) {
    if ($backups.Count -eq 0) { throw "Không có bản sao lưu trong $($paths.Backups)." }
    $backups | ForEach-Object { "{0}  api={1} worker={2}" -f $_.Name, (Test-Path (Join-Path $_.FullName 'api')), (Test-Path (Join-Path $_.FullName 'worker')) }
    return
}
$target = if ($Backup) { Join-Path $paths.Backups $Backup } else { $backups[0].FullName }
if (-not (Test-Path (Join-Path $target 'api\Rdm.Api.dll'))) { throw "Bản sao lưu $target không có api\Rdm.Api.dll." }
Write-Step "Rollback về $(Split-Path $target -Leaf)"

$config = Read-RdmConfig $paths.ConfigFile
$cs = $config.ConnectionStrings.SqlServer
$running = Get-RunningJobs $cs
if ($running -and -not $Force) { throw "Còn job hàng loạt chưa xong: $running. Chờ xong hoặc chạy lại với -Force." }

Stop-RdmApi $paths.Api $AppPoolName
Stop-RdmWorker $ServiceName

Invoke-Robocopy (Join-Path $target 'api') $paths.Api @('/MIR', '/XD', 'logs', '/XF', 'app_offline.htm', 'dev_nco_rfc.log')
if (Test-Path (Join-Path $target 'worker')) {
    Invoke-Robocopy (Join-Path $target 'worker') $paths.Worker @('/MIR', '/XD', 'logs', '/XF', 'dev_nco_rfc.log')
}
Write-Ok 'Đã khôi phục file'

Remove-Item (Join-Path $paths.Api 'app_offline.htm') -Force -ErrorAction SilentlyContinue
Start-WebAppPool $AppPoolName
$health = Wait-Health "http://localhost:$HttpPort/health" $HostName 180
Write-Ok "API: $health"
if (Test-Path (Join-Path $paths.Worker 'Rdm.Worker.exe')) {
    Start-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Ok "Worker: service $ServiceName Running"
}
Write-Host "`nRollback xong về $(Split-Path $target -Leaf)." -ForegroundColor Green
