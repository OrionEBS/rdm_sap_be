# Hàm dùng chung cho Deploy-Rdm.ps1 / Rollback-Rdm.ps1 / Uninstall-Rdm.ps1 (Windows PowerShell 5.1, Windows Server 2019).
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "  [!]  $Text" -ForegroundColor Yellow }

function Assert-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Cần chạy PowerShell bằng quyền Administrator.'
    }
    if (-not [Environment]::Is64BitProcess) { throw 'Cần chạy PowerShell 64-bit (NCo và IIS app pool là x64).' }
}

# Đường dẫn chuẩn dưới InstallRoot
function Get-RdmPaths([string]$InstallRoot) {
    [pscustomobject]@{
        Root       = $InstallRoot
        Api        = Join-Path $InstallRoot 'api'
        Worker     = Join-Path $InstallRoot 'worker'
        ConfigDir  = Join-Path $InstallRoot 'config'
        ConfigFile = Join-Path $InstallRoot 'config\appsettings.Production.json'
        Backups    = Join-Path $InstallRoot 'backups'
    }
}

function Read-RdmConfig([string]$ConfigFile) {
    if (-not (Test-Path $ConfigFile)) { return $null }
    try { return (Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { throw "File cấu hình $ConfigFile không phải JSON hợp lệ: $($_.Exception.Message)" }
}

# Truy vấn 1 giá trị (System.Data.SqlClient có sẵn trong Windows PowerShell). Trả $null nếu lỗi kết nối và -Quiet.
function Invoke-SqlScalar([string]$ConnectionString, [string]$Sql, [switch]$Quiet) {
    $conn = New-Object System.Data.SqlClient.SqlConnection
    try {
        $conn.ConnectionString = $ConnectionString
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = 30
        return $cmd.ExecuteScalar()
    }
    catch {
        if ($Quiet) { return $null }
        throw
    }
    finally { $conn.Dispose() }
}

# Job bảng giá hàng loạt đang chờ / đang chạy (rdm.SapPostings) — triển khai lúc này sẽ cắt ngang job tạo / sửa → Unknown.
function Get-RunningJobs([string]$ConnectionString) {
    $sql = @"
IF COL_LENGTH('rdm.SapPostings', 'IsBackgroundJob') IS NULL SELECT CAST(N'' AS nvarchar(max))
ELSE SELECT STRING_AGG(CONCAT(Operation, N'/', RequestId, N' (', ISNULL(ProgressDone, 0), N'/', ISNULL(ProgressTotal, 0), N')'), N', ')
     FROM rdm.SapPostings WHERE IsBackgroundJob = 1 AND Status = N'Pending'
"@
    $r = Invoke-SqlScalar $ConnectionString $sql
    if ($r -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$r)) { return '' }
    return [string]$r
}

# Robocopy: mã thoát >= 8 là lỗi.
function Invoke-Robocopy([string]$Source, [string]$Destination, [string[]]$Options) {
    $rcArgs = @($Source, $Destination) + $Options + @('/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NP')
    & robocopy.exe @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $Source -> $Destination lỗi (exit $LASTEXITCODE)." }
}

function Grant-Rights([string]$Path, [string]$Identity, [string]$Rights) {
    # Rights theo icacls: (OI)(CI)RX, (OI)(CI)M, R ...
    & icacls.exe $Path /grant "${Identity}:$Rights" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $Path /grant $Identity lỗi (exit $LASTEXITCODE)." }
}

# File cấu hình chứa mật khẩu: chỉ Administrators, SYSTEM (toàn quyền) và 2 tài khoản chạy ứng dụng (đọc).
function Protect-ConfigFile([string]$File, [string[]]$Readers) {
    & icacls.exe $File /inheritance:r /Q | Out-Null
    & icacls.exe $File /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' /Q | Out-Null   # Administrators, SYSTEM
    foreach ($r in $Readers) { Grant-Rights $File $r 'R' }
}

function Stop-RdmApi([string]$ApiDir, [string]$AppPoolName, [int]$TimeoutSec = 90) {
    Import-Module WebAdministration
    if (Test-Path $ApiDir) {
        # app_offline.htm: ANCM dừng ứng dụng nhẹ nhàng, trả 503 cho request mới trong lúc cập nhật.
        Set-Content (Join-Path $ApiDir 'app_offline.htm') '<html><body>RDM API đang cập nhật — thử lại sau ít phút.</body></html>' -Encoding UTF8
    }
    if (Test-Path "IIS:\AppPools\$AppPoolName") {
        if ((Get-WebAppPoolState $AppPoolName).Value -ne 'Stopped') { Stop-WebAppPool $AppPoolName }
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-WebAppPoolState $AppPoolName).Value -ne 'Stopped') {
            if ((Get-Date) -gt $deadline) { throw "App pool $AppPoolName không dừng sau $TimeoutSec giây." }
            Start-Sleep -Seconds 2
        }
        Write-Ok "Đã dừng app pool $AppPoolName"
    }
}

function Stop-RdmWorker([string]$ServiceName, [int]$TimeoutSec = 180) {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc -or $svc.Status -eq 'Stopped') { return }
    Stop-Service $ServiceName -NoWait
    try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSec)) }
    catch { throw "Service $ServiceName không dừng sau $TimeoutSec giây (job đang chạy?). Kiểm tra rồi chạy lại." }
    Write-Ok "Đã dừng service $ServiceName"
}

function Wait-Health([string]$Url, [string]$HostHeader, [int]$TimeoutSec = 180) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = ''
    do {
        # HttpWebRequest: đặt được header Host (binding theo host name) — Invoke-WebRequest của PowerShell 5.1 không cho.
        $resp = $null
        try {
            $req = [Net.HttpWebRequest]::Create($Url)
            $req.Timeout = 30000
            if ($HostHeader) { $req.Host = $HostHeader }
            $resp = $req.GetResponse()
            $body = (New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd()
            if ([int]$resp.StatusCode -eq 200) { return $body }
            $last = "HTTP $([int]$resp.StatusCode)"
        }
        catch [Net.WebException] {
            $last = $_.Exception.Message
            if ($_.Exception.Response) { $last = "HTTP $([int]$_.Exception.Response.StatusCode): $last" }
        }
        finally { if ($resp) { $resp.Close() } }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "Health check $Url không đạt sau $TimeoutSec giây: $last"
}

# Hangfire server của worker còn heartbeat trong 2 phút gần nhất.
function Test-WorkerHeartbeat([string]$ConnectionString) {
    $sql = "IF OBJECT_ID('hangfire.Server') IS NULL SELECT 0 ELSE SELECT COUNT(*) FROM hangfire.Server " +
           "WHERE Id LIKE '%rdm-worker%' AND LastHeartbeat > DATEADD(MINUTE, -2, SYSUTCDATETIME())"
    $n = Invoke-SqlScalar $ConnectionString $sql -Quiet
    return ($null -ne $n -and [int]$n -gt 0)
}

# web.config của Rdm.Api: hosting model, biến môi trường production, Hangfire server tắt (job chạy ở worker), preload /health.
function Update-ApiWebConfig([string]$Path, [string]$HostingModel, [string]$ConfigFile) {
    [xml]$wc = Get-Content $Path -Raw -Encoding UTF8
    $ws = $wc.SelectSingleNode('/configuration/location/system.webServer')
    if ($null -eq $ws) { $ws = $wc.SelectSingleNode('/configuration/system.webServer') }
    $anc = $ws.SelectSingleNode('aspNetCore')
    if ($null -eq $anc) { throw "$Path không có phần tử aspNetCore." }
    $anc.SetAttribute('processPath', '.\Rdm.Api.exe')
    $anc.RemoveAttribute('arguments')
    $anc.SetAttribute('hostingModel', $HostingModel.ToLowerInvariant())
    $anc.SetAttribute('stdoutLogEnabled', 'false')
    $anc.SetAttribute('stdoutLogFile', '.\logs\stdout')
    $anc.SetAttribute('shutdownTimeLimit', '60')
    if ($HostingModel -eq 'OutOfProcess') { $anc.SetAttribute('requestTimeout', '00:10:00') } else { $anc.RemoveAttribute('requestTimeout') }

    $envNode = $anc.SelectSingleNode('environmentVariables')
    if ($null -eq $envNode) { $envNode = $anc.AppendChild($wc.CreateElement('environmentVariables')) }
    $envNode.RemoveAll()
    foreach ($pair in @(
            @('ASPNETCORE_ENVIRONMENT', 'Production'),
            @('RDM_CONFIG_FILE', $ConfigFile),
            @('Hangfire__ServerEnabled', 'false'))) {   # job chạy ở Rdm.Worker
        $e = $wc.CreateElement('environmentVariable')
        $e.SetAttribute('name', $pair[0]); $e.SetAttribute('value', $pair[1])
        [void]$envNode.AppendChild($e)
    }

    # Application Initialization: IIS gọi /health ngay khi app pool khởi động (migration chạy trước request đầu tiên)
    $init = $ws.SelectSingleNode('applicationInitialization')
    if ($null -eq $init) { $init = $ws.AppendChild($wc.CreateElement('applicationInitialization')) }
    $init.RemoveAll()
    $init.SetAttribute('doAppInitAfterRestart', 'true')
    $page = $wc.CreateElement('add'); $page.SetAttribute('initializationPage', '/health')
    [void]$init.AppendChild($page)
    $wc.Save($Path)
}

function Show-LogTail([string]$Dir, [int]$Lines = 30) {
    $log = Get-ChildItem (Join-Path $Dir 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Write-Host "  --- $($log.FullName) (cuối) ---" -ForegroundColor DarkGray
        Get-Content $log.FullName -Tail $Lines -Encoding UTF8 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    }
}
