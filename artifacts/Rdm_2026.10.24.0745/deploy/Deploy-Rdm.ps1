<#
.SYNOPSIS
  Triển khai / cập nhật RDM SAP Integration trên Windows Server 2019: Rdm.Api (IIS) + Rdm.Worker (Windows Service, Hangfire).

.DESCRIPTION
  Chạy bằng PowerShell 64-bit, quyền Administrator, trên máy chủ. Lần đầu: cài đặt; các lần sau: cập nhật (idempotent).

  Bố cục trên máy chủ (mặc định D:\Apps\Rdm):
    api\      Rdm.Api — IIS site, app pool "No Managed Code", ASP.NET Core Module V2
    worker\   Rdm.Worker — Windows Service chạy Hangfire (job bảng giá hàng loạt, đồng bộ master data, dọn log)
    config\appsettings.Production.json   cấu hình DÙNG CHUNG (mật khẩu SQL / SAP / JWT) — chỉ Administrators + 2 tài khoản chạy được đọc
    backups\<thời gian>\                 bản trước mỗi lần cập nhật (Rollback-Rdm.ps1), giữ -KeepBackups bản

  Các bước:
    1. Kiểm tra / cài điều kiện: IIS (+ Application Initialization), .NET 8 Hosting Bundle, VC++ 2015-2022 x64
    2. Kiểm tra gói (đủ NCo), cấu hình production (lần đầu: tạo từ mẫu + sinh khóa JWT rồi dừng để điền), SQL Server, SAP
    3. Chặn nếu còn job hàng loạt đang chạy (cắt ngang job tạo / sửa bảng giá → Unknown) — bỏ qua bằng -Force
    4. Dừng API (app_offline + dừng app pool) và worker → sao lưu → copy bản mới (giữ logs)
    5. Cấu hình web.config, app pool (AlwaysRunning, không idle timeout), site (+ preload /health), service (tự khởi động lại khi lỗi)
    6. Phân quyền thư mục / file cấu hình
    7. Khởi động API (chạy EF migration) → /health → khởi động worker → kiểm tra heartbeat Hangfire

.EXAMPLE
  # Lần đầu (máy chủ có internet hoặc truyền file cài offline)
  .\Deploy-Rdm.ps1 -InstallPrerequisites -HostingBundlePath D:\Setup\dotnet-hosting-8.0.x-win.exe -VcRedistPath D:\Setup\vc_redist.x64.exe

  # Cập nhật bản mới (chạy từ thư mục giải nén gói mới)
  .\Deploy-Rdm.ps1

  # Cổng / host / HTTPS tùy chỉnh
  .\Deploy-Rdm.ps1 -HttpPort 80 -HostName rdm-api.tafico.local -HttpsPort 443 -CertThumbprint 1A2B...
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    # Thư mục gói đã giải nén (mặc định: thư mục cha của script này) hoặc file .zip
    [string]$Package = '',   # mặc định: thư mục cha của script (gói đã giải nén)
    [string]$InstallRoot = 'D:\Apps\Rdm',
    [string]$SiteName = 'RdmApi',
    [string]$AppPoolName = 'RdmApi',
    [int]$HttpPort = 8080,
    [string]$HostName = '',
    [int]$HttpsPort = 0,
    [string]$CertThumbprint = '',
    [string]$ServiceName = 'RdmWorker',
    # Tài khoản chạy worker: mặc định virtual account NT SERVICE\<ServiceName>; hoặc tài khoản domain / gMSA
    [pscredential]$ServiceCredential,
    # OutOfProcess: NCo (native) chạy trong Rdm.Api.exe tách khỏi w3wp — lỗi native không làm sập IIS
    [ValidateSet('OutOfProcess', 'InProcess')][string]$HostingModel = 'OutOfProcess',
    [int]$KeepBackups = 5,
    [switch]$InstallPrerequisites,
    [string]$HostingBundlePath = '',
    [string]$VcRedistPath = '',
    [switch]$Force,
    [switch]$AutoRollback
)
. (Join-Path $PSScriptRoot '_Common.ps1')
if (-not $Package) { $Package = Split-Path $PSScriptRoot }
Assert-Admin
$paths = Get-RdmPaths $InstallRoot
$started = Get-Date
$workerIdentity = if ($ServiceCredential) { $ServiceCredential.UserName } else { "NT SERVICE\$ServiceName" }
$poolIdentity = "IIS AppPool\$AppPoolName"

# ===================================================================== 1. điều kiện máy chủ
Write-Step '1. Kiểm tra điều kiện máy chủ'
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "  $($os.Caption) build $($os.BuildNumber)"

$features = @('Web-Server', 'Web-WebServer', 'Web-Default-Doc', 'Web-Http-Errors', 'Web-Static-Content', 'Web-Http-Logging',
              'Web-Request-Monitor', 'Web-Filtering', 'Web-Stat-Compression', 'Web-AppInit', 'Web-Mgmt-Console')
$missingFeatures = @(Get-WindowsFeature $features | Where-Object { -not $_.Installed } | ForEach-Object { $_.Name })
if ($missingFeatures.Count -gt 0) {
    if (-not $InstallPrerequisites) { throw "Thiếu tính năng IIS: $($missingFeatures -join ', '). Chạy lại với -InstallPrerequisites." }
    Write-Host "  Cài tính năng IIS: $($missingFeatures -join ', ')"
    $r = Install-WindowsFeature $missingFeatures
    if (-not $r.Success) { throw 'Cài tính năng IIS thất bại.' }
    if ($r.RestartNeeded -eq 'Yes') { Write-Warn 'Windows yêu cầu khởi động lại sau khi cài IIS — khởi động lại rồi chạy lại script.'; exit 3 }
}
Write-Ok 'IIS + Application Initialization'

function Test-HostingBundle {
    $ancm = Test-Path "$env:windir\System32\inetsrv\aspnetcorev2.dll"
    $aspnet = @(Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App" -Directory -Filter '8.*' -ErrorAction SilentlyContinue).Count -gt 0
    $core = @(Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.NETCore.App" -Directory -Filter '8.*' -ErrorAction SilentlyContinue).Count -gt 0
    return ($ancm -and $aspnet -and $core)
}
if (-not (Test-HostingBundle)) {
    if ($InstallPrerequisites -and $HostingBundlePath) {
        Write-Host "  Cài .NET 8 Hosting Bundle: $HostingBundlePath"
        $p = Start-Process $HostingBundlePath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "Cài Hosting Bundle lỗi (exit $($p.ExitCode))." }
        & net stop was /y | Out-Null; & net start w3svc | Out-Null
    }
    if (-not (Test-HostingBundle)) {
        throw ('Thiếu .NET 8 Hosting Bundle (ASP.NET Core Module V2 + runtime). Tải "Hosting Bundle" tại ' +
               'https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy lại với -InstallPrerequisites -HostingBundlePath <file>. ' +
               'Cài SAU khi đã có IIS.')
    }
}
Write-Ok '.NET 8 Hosting Bundle (ASP.NET Core Module V2, Microsoft.AspNetCore.App 8, Microsoft.NETCore.App 8)'

# SAP NCo 3.1 (cpc4n.dll / ijwhost.dll native) cần Visual C++ 2015-2022 x64
function Test-VcRedist { (Test-Path "$env:windir\System32\vcruntime140.dll") -and (Test-Path "$env:windir\System32\vcruntime140_1.dll") }
if (-not (Test-VcRedist)) {
    if ($InstallPrerequisites -and $VcRedistPath) {
        Write-Host "  Cài Visual C++ x64: $VcRedistPath"
        $p = Start-Process $VcRedistPath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 1638, 3010) { throw "Cài VC++ lỗi (exit $($p.ExitCode))." }
    }
    if (-not (Test-VcRedist)) {
        throw 'Thiếu Visual C++ 2015-2022 x64 (SAP NCo cần). Tải https://aka.ms/vs/17/release/vc_redist.x64.exe rồi chạy lại với -InstallPrerequisites -VcRedistPath <file>.'
    }
}
Write-Ok 'Visual C++ 2015-2022 x64'

# ===================================================================== 2. gói + cấu hình
Write-Step '2. Kiểm tra gói và cấu hình'
$tempExtract = $null
if ($Package -like '*.zip') {
    $tempExtract = Join-Path $env:TEMP ("rdm_pkg_" + [guid]::NewGuid().ToString('N'))
    Expand-Archive $Package -DestinationPath $tempExtract
    $Package = $tempExtract
}
$manifest = Get-Content (Join-Path $Package 'manifest.json') -Raw | ConvertFrom-Json
Write-Host "  Gói: $($manifest.product) — phiên bản $($manifest.version) (build $($manifest.builtAtUtc), NCo $($manifest.sapNco))"
foreach ($check in @(
        @{ Dir = 'api';    Files = @('Rdm.Api.exe', 'Rdm.Api.dll', 'web.config', 'appsettings.json', 'sapnco.dll', 'sapnco_utils.dll', 'cpc4n.dll', 'ijwhost.dll') },
        @{ Dir = 'worker'; Files = @('Rdm.Worker.exe', 'Rdm.Worker.dll', 'appsettings.json', 'sapnco.dll', 'sapnco_utils.dll', 'cpc4n.dll', 'ijwhost.dll') })) {
    $missing = @($check.Files | Where-Object { -not (Test-Path (Join-Path $Package "$($check.Dir)\$_")) })
    if ($missing.Count -gt 0) { throw "Gói thiếu $($check.Dir)\$($missing -join ', ')." }
    if (Test-Path (Join-Path $Package "$($check.Dir)\appsettings.Development.json")) { throw "Gói chứa $($check.Dir)\appsettings.Development.json — đóng gói lại bằng Build-Package.ps1." }
}
Write-Ok 'Gói đủ file (gồm SAP NCo 3.1)'

foreach ($d in @($paths.Root, $paths.Api, $paths.Worker, $paths.ConfigDir, $paths.Backups, (Join-Path $paths.Api 'logs'), (Join-Path $paths.Worker 'logs'))) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}

if (-not (Test-Path $paths.ConfigFile)) {
    # Lần đầu: tạo từ mẫu, sinh khóa ký JWT ngẫu nhiên, khóa quyền file, dừng để điền thông tin.
    $template = Get-Content (Join-Path $Package 'config\appsettings.Production.example.json') -Raw -Encoding UTF8
    $bytes = New-Object byte[] 48
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $template = $template.Replace('<TU_SINH_KHI_TAO>', [Convert]::ToBase64String($bytes))
    [IO.File]::WriteAllText($paths.ConfigFile, $template, (New-Object Text.UTF8Encoding $false))
    Protect-ConfigFile $paths.ConfigFile @()
    Write-Warn "Đã tạo $($paths.ConfigFile) (khóa JWT đã sinh ngẫu nhiên)."
    Write-Warn 'Điền SQL Server, SAP, mật khẩu admin (thay mọi giá trị <...>), rồi chạy lại script.'
    exit 2
}
$config = Read-RdmConfig $paths.ConfigFile
$problems = New-Object System.Collections.Generic.List[string]
$raw = Get-Content $paths.ConfigFile -Raw -Encoding UTF8
if ($raw -match '<[A-Z_]+>') { $problems.Add("còn giá trị mẫu chưa điền: $(([regex]::Matches($raw, '<[A-Z_]+>') | ForEach-Object Value | Select-Object -Unique) -join ', ')") }
$cs = $config.ConnectionStrings.SqlServer
if ([string]::IsNullOrWhiteSpace($cs)) { $problems.Add('ConnectionStrings:SqlServer trống') }
foreach ($k in 'AppServerHost', 'SystemNumber', 'Client', 'User', 'Password') { if ([string]::IsNullOrWhiteSpace($config.Sap.$k)) { $problems.Add("Sap:$k trống") } }
if ([Text.Encoding]::UTF8.GetByteCount([string]$config.Jwt.SigningKey) -lt 32) { $problems.Add('Jwt:SigningKey phải >= 32 byte') }
if ($problems.Count -gt 0) { throw "Cấu hình $($paths.ConfigFile) chưa hợp lệ: $($problems -join '; ')." }
Write-Ok "Cấu hình $($paths.ConfigFile)"

$sqlVersion = Invoke-SqlScalar $cs 'SELECT CONCAT(SERVERPROPERTY(''ProductVersion''), N'' '', SERVERPROPERTY(''Edition''), N'' — DB '', DB_NAME())' -Quiet
if ($null -eq $sqlVersion) { throw 'Không kết nối được SQL Server bằng ConnectionStrings:SqlServer (kiểm tra host, cổng, tài khoản, Encrypt / TrustServerCertificate).' }
Write-Ok "SQL Server $sqlVersion"

$sapPort = 3300 + [int]$config.Sap.SystemNumber
$tcp = New-Object Net.Sockets.TcpClient
try {
    $ok = $tcp.BeginConnect($config.Sap.AppServerHost, $sapPort, $null, $null).AsyncWaitHandle.WaitOne(5000) -and $tcp.Connected
    if ($ok) { Write-Ok "SAP gateway $($config.Sap.AppServerHost):$sapPort" }
    else { Write-Warn "Không tới được SAP $($config.Sap.AppServerHost):$sapPort (firewall / VPN) — API vẫn chạy nhưng gọi SAP sẽ lỗi SAP_UNAVAILABLE." }
}
finally { $tcp.Dispose() }

# ===================================================================== 3. job đang chạy
Write-Step '3. Kiểm tra job hàng loạt đang chạy'
$running = Get-RunningJobs $cs
if ($running) {
    if (-not $Force) { throw "Còn job hàng loạt chưa xong: $running. Chờ xong (GET /api/sd/prices-bapi/jobs/...) hoặc chạy lại với -Force (job tạo / sửa bị cắt ngang sẽ thành Unknown)." }
    Write-Warn "Bỏ qua job đang chạy do -Force: $running"
}
else { Write-Ok 'Không có job đang chạy' }

# ===================================================================== 4. dừng + sao lưu + copy
Write-Step '4. Dừng ứng dụng, sao lưu, copy bản mới'
Import-Module WebAdministration
Stop-RdmApi $paths.Api $AppPoolName
Stop-RdmWorker $ServiceName

$backup = $null
if (Test-Path (Join-Path $paths.Api 'Rdm.Api.dll')) {
    $backup = Join-Path $paths.Backups (Get-Date -Format 'yyyyMMdd-HHmmss')
    Invoke-Robocopy $paths.Api (Join-Path $backup 'api') @('/E', '/XD', 'logs', '/XF', 'app_offline.htm', 'dev_nco_rfc.log')
    if (Test-Path (Join-Path $paths.Worker 'Rdm.Worker.dll')) {
        Invoke-Robocopy $paths.Worker (Join-Path $backup 'worker') @('/E', '/XD', 'logs', '/XF', 'dev_nco_rfc.log')
    }
    Write-Ok "Sao lưu bản cũ: $backup"
    Get-ChildItem $paths.Backups -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepBackups |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force; Write-Host "  Xóa bản sao lưu cũ $($_.Name)" }
}

# /MIR: xóa file thừa của bản cũ; giữ logs, app_offline.htm (gỡ ở bước 7), log NCo
Invoke-Robocopy (Join-Path $Package 'api') $paths.Api @('/MIR', '/XD', 'logs', '/XF', 'app_offline.htm', 'dev_nco_rfc.log')
Invoke-Robocopy (Join-Path $Package 'worker') $paths.Worker @('/MIR', '/XD', 'logs', '/XF', 'dev_nco_rfc.log')
Write-Ok "Đã copy phiên bản $($manifest.version)"

# ===================================================================== 5. web.config, IIS, service
Write-Step '5. Cấu hình web.config, IIS, Windows Service'
Update-ApiWebConfig (Join-Path $paths.Api 'web.config') $HostingModel $paths.ConfigFile
Write-Ok "web.config: $HostingModel, ASPNETCORE_ENVIRONMENT=Production, RDM_CONFIG_FILE, Hangfire server tắt, preload /health"

# App pool: không managed code, x64, luôn chạy, không idle, recycle 03:00 hằng ngày thay cho 29 giờ
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) { New-WebAppPool $AppPoolName | Out-Null; Write-Host "  Tạo app pool $AppPoolName" }
$pool = "IIS:\AppPools\$AppPoolName"
Set-ItemProperty $pool managedRuntimeVersion ''
Set-ItemProperty $pool enable32BitAppOnWin64 $false
Set-ItemProperty $pool startMode 'AlwaysRunning'
Set-ItemProperty $pool processModel.identityType 'ApplicationPoolIdentity'
Set-ItemProperty $pool processModel.idleTimeout ([TimeSpan]::Zero)
Set-ItemProperty $pool processModel.loadUserProfile $true
Set-ItemProperty $pool recycling.periodicRestart.time ([TimeSpan]::Zero)
Clear-ItemProperty $pool -Name recycling.periodicRestart.schedule
New-ItemProperty $pool -Name recycling.periodicRestart.schedule -Value @{ value = '03:00:00' } | Out-Null
Write-Ok "App pool $AppPoolName (No Managed Code, x64, AlwaysRunning, idle 0, recycle 03:00)"

if (-not (Get-Website -Name $SiteName)) {
    New-Website -Name $SiteName -PhysicalPath $paths.Api -ApplicationPool $AppPoolName -Port $HttpPort -HostHeader $HostName | Out-Null
    Write-Host "  Tạo site $SiteName"
}
Set-ItemProperty "IIS:\Sites\$SiteName" physicalPath $paths.Api
Set-ItemProperty "IIS:\Sites\$SiteName" applicationPool $AppPoolName
Set-ItemProperty "IIS:\Sites\$SiteName" applicationDefaults.preloadEnabled $true
if (-not (Get-WebBinding -Name $SiteName -Protocol http -Port $HttpPort -HostHeader $HostName)) {
    New-WebBinding -Name $SiteName -Protocol http -Port $HttpPort -HostHeader $HostName
}
if ($HttpsPort -gt 0) {
    if (-not $CertThumbprint) { throw '-HttpsPort cần -CertThumbprint (chứng chỉ trong LocalMachine\My).' }
    if (-not (Test-Path "Cert:\LocalMachine\My\$CertThumbprint")) { throw "Không thấy chứng chỉ $CertThumbprint trong LocalMachine\My." }
    if (-not (Get-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $HostName)) {
        New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $HostName -SslFlags ([int][bool]$HostName)
    }
    (Get-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -HostHeader $HostName).AddSslCertificate($CertThumbprint, 'My')
}
$bindings = (Get-WebBinding -Name $SiteName | ForEach-Object { "$($_.protocol) $($_.bindingInformation)" }) -join '; '
Write-Ok "Site $SiteName → $($paths.Api) [$bindings]"

$exe = Join-Path $paths.Worker 'Rdm.Worker.exe'
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    $params = @{ Name = $ServiceName; BinaryPathName = "`"$exe`""; DisplayName = 'RDM SAP Worker (Hangfire)'; StartupType = 'Automatic'
                 Description = 'Chạy job nền RDM: bảng giá hàng loạt (BAPI_PRICES_CONDITIONS), đồng bộ master data SAP, dọn log.' }
    if ($ServiceCredential) { $params.Credential = $ServiceCredential }
    New-Service @params | Out-Null
    Write-Host "  Tạo service $ServiceName"
}
& sc.exe config $ServiceName binPath= "`"$exe`"" start= delayed-auto | Out-Null
if (-not $ServiceCredential) { & sc.exe config $ServiceName obj= "NT SERVICE\$ServiceName" | Out-Null }
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value @(
    'DOTNET_ENVIRONMENT=Production', "RDM_CONFIG_FILE=$($paths.ConfigFile)")
Write-Ok "Service $ServiceName ($workerIdentity, delayed auto start, tự khởi động lại khi lỗi)"

# ===================================================================== 6. phân quyền
Write-Step '6. Phân quyền'
Grant-Rights $paths.Api $poolIdentity '(OI)(CI)RX'
Grant-Rights (Join-Path $paths.Api 'logs') $poolIdentity '(OI)(CI)M'
Grant-Rights $paths.Worker $workerIdentity '(OI)(CI)RX'
Grant-Rights (Join-Path $paths.Worker 'logs') $workerIdentity '(OI)(CI)M'
# NCo ghi dev_nco_rfc.log vào thư mục làm việc khi có lỗi RFC
foreach ($pair in @(@($paths.Api, $poolIdentity), @($paths.Worker, $workerIdentity))) {
    $f = Join-Path $pair[0] 'dev_nco_rfc.log'
    if (-not (Test-Path $f)) { New-Item -ItemType File -Path $f | Out-Null }
    Grant-Rights $f $pair[1] 'M'
}
Protect-ConfigFile $paths.ConfigFile @($poolIdentity, $workerIdentity)
Write-Ok "api: $poolIdentity · worker: $workerIdentity · config: chỉ Administrators, SYSTEM + 2 tài khoản trên (đọc)"

# ===================================================================== 7. khởi động + kiểm tra
Write-Step '7. Khởi động và kiểm tra'
$healthUrl = "http://localhost:$HttpPort/health"
try {
    Remove-Item (Join-Path $paths.Api 'app_offline.htm') -Force -ErrorAction SilentlyContinue
    Start-WebAppPool $AppPoolName
    if ((Get-Website -Name $SiteName).State -ne 'Started') { Start-Website $SiteName }
    $health = Wait-Health $healthUrl $HostName 180      # lần đầu / có migration: chờ lâu hơn
    Write-Ok "API: $health"

    Start-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-WorkerHeartbeat $cs)) {
        if ((Get-Date) -gt $deadline) { throw "Service $ServiceName chạy nhưng chưa thấy Hangfire server rdm-worker trong hangfire.Server." }
        Start-Sleep -Seconds 5
    }
    Write-Ok "Worker: service Running, Hangfire heartbeat OK"
}
catch {
    Write-Host "`n  TRIỂN KHAI LỖI: $($_.Exception.Message)" -ForegroundColor Red
    Show-LogTail $paths.Api
    Show-LogTail $paths.Worker
    if ($AutoRollback -and $backup) {
        Write-Warn "Tự rollback về $backup"
        & (Join-Path $PSScriptRoot 'Rollback-Rdm.ps1') -InstallRoot $InstallRoot -SiteName $SiteName -AppPoolName $AppPoolName `
            -ServiceName $ServiceName -HttpPort $HttpPort -HostName $HostName -Backup (Split-Path $backup -Leaf) -Force
    }
    elseif ($backup) { Write-Warn "Rollback: .\Rollback-Rdm.ps1 -Backup $(Split-Path $backup -Leaf)" }
    throw
}
finally {
    if ($tempExtract) { Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ("`nTriển khai xong phiên bản {0} trong {1:N0} giây." -f $manifest.version, ((Get-Date) - $started).TotalSeconds) -ForegroundColor Green
Write-Host "  API:     http://$(if ($HostName) { $HostName } else { $env:COMPUTERNAME }):$HttpPort  (/health, /swagger nếu bật)"
Write-Host "  Worker:  service $ServiceName — Hangfire dashboard chỉ mở trên máy chủ: http://localhost:$HttpPort/hangfire"
Write-Host "  Log:     $($paths.Api)\logs, $($paths.Worker)\logs, bảng dbo.SerilogEvents"
