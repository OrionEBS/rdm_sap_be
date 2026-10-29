<#
.SYNOPSIS
  Gỡ site / app pool IIS và Windows Service của RDM. Mặc định GIỮ file, cấu hình, log, bản sao lưu và CSDL.

.EXAMPLE
  .\Uninstall-Rdm.ps1
  .\Uninstall-Rdm.ps1 -RemoveFiles        # xóa cả api\, worker\ (vẫn giữ config\ và backups\)
#>
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = 'D:\Apps\Rdm',
    [string]$SiteName = 'RdmApi',
    [string]$AppPoolName = 'RdmApi',
    [string]$ServiceName = 'RdmWorker',
    [switch]$RemoveFiles
)
. (Join-Path $PSScriptRoot '_Common.ps1')
Assert-Admin
Import-Module WebAdministration
$paths = Get-RdmPaths $InstallRoot

Stop-RdmWorker $ServiceName
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Xóa Windows Service')) { & sc.exe delete $ServiceName | Out-Null; Write-Ok "Đã xóa service $ServiceName" }
}
if (Get-Website -Name $SiteName) {
    if ($PSCmdlet.ShouldProcess($SiteName, 'Xóa IIS site')) { Remove-Website -Name $SiteName; Write-Ok "Đã xóa site $SiteName" }
}
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    if ($PSCmdlet.ShouldProcess($AppPoolName, 'Xóa app pool')) { Remove-WebAppPool -Name $AppPoolName; Write-Ok "Đã xóa app pool $AppPoolName" }
}
if ($RemoveFiles) {
    foreach ($d in @($paths.Api, $paths.Worker)) {
        if ((Test-Path $d) -and $PSCmdlet.ShouldProcess($d, 'Xóa thư mục')) { Remove-Item $d -Recurse -Force; Write-Ok "Đã xóa $d" }
    }
}
Write-Host "`nĐã gỡ. Giữ lại: $($paths.ConfigDir), $($paths.Backups)$(if (-not $RemoveFiles) { ", $($paths.Api), $($paths.Worker)" }), CSDL." -ForegroundColor Green
