<#
.SYNOPSIS
  Chạy RDM SAP API bằng cửa sổ console (bản kiểm thử) — chỉ định cổng, HTTPS.

.DESCRIPTION
  Cấu hình: api\appsettings.Development.json (giữ nguyên như máy dev). Hangfire chạy trong cùng process.
  HTTPS (mặc định): dùng chứng chỉ -PfxPath / -PfxPassword; không truyền → tự sinh chứng chỉ tự ký cho tên máy + IP
  (lưu ở cert\, dùng lại các lần sau) và xuất cert\rdm-api.cer để máy client import vào "Trusted Root Certification Authorities".

  MỌI lần chạy ghi log ở logs\start-api-<thời gian>.log (kiểm tra điều kiện + toàn bộ output của API + mã thoát + Event Log
  khi API crash) — gửi file này khi báo lỗi. Log ứng dụng: api\logs\startup-*.log, api\logs\rdm-*.log.

.EXAMPLE
  Start-Api.cmd                                   # HTTPS cổng 5200, chứng chỉ tự ký
  Start-Api.cmd -Port 8443                        # HTTPS cổng 8443
  Start-Api.cmd -Port 8443 -HttpPort 8080         # HTTPS 8443 + HTTP 8080
  Start-Api.cmd -Port 8443 -PfxPath D:\cert\api.pfx -PfxPassword "***"   # chứng chỉ của công ty
  Start-Api.cmd -Port 5200 -Http                  # chỉ HTTP
  Start-Api.cmd -SkipChecks                       # bỏ kiểm tra kết nối SQL / SAP
#>
param(
    [ValidateRange(1, 65535)][int]$Port = 5200,
    [switch]$Http,
    [ValidateRange(0, 65535)][int]$HttpPort = 0,
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [switch]$SkipChecks,
    # Start-Api.cmd tự pause ở cuối → script không hỏi Enter
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$api = Join-Path $root 'api'
$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logFile = Join-Path $logDir ("start-api-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$log = New-Object IO.StreamWriter($logFile, $true, (New-Object Text.UTF8Encoding $true))
$log.AutoFlush = $true

function Say([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    $log.WriteLine(("{0:HH:mm:ss} {1}" -f (Get-Date), $text))
}
function Finish([int]$code) {
    if ($code -ne 0) { Say "Log của lần chạy này: $logFile" 'Yellow' }
    $log.Dispose()
    if (-not $NoPause) { Read-Host 'Nhấn Enter để đóng' | Out-Null }
    exit $code
}
function Fail([string]$message) {
    Say "`n[!] $message" 'Red'
    Finish 1
}

# Lỗi không lường trước trong script → vẫn ghi log và giữ cửa sổ.
trap {
    Say "`n[!] Lỗi launcher: $($_.Exception.Message)" 'Red'
    Say ($_.ScriptStackTrace) 'DarkGray'
    Finish 1
}

$Host.UI.RawUI.WindowTitle = "RDM SAP API (console test) :$Port"
Say "RDM SAP API console test — $(Get-Date -Format 'dd/MM/yyyy HH:mm:ss') — máy $env:COMPUTERNAME, user $env:USERNAME"
$osName = try { (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption } catch { [Environment]::OSVersion.VersionString }
Say "Windows: $osName · PowerShell $($PSVersionTable.PSVersion) · 64-bit: $([Environment]::Is64BitProcess)"
Say "Thư mục: $root"
if (-not [Environment]::Is64BitOperatingSystem) { Fail 'Windows 32-bit không chạy được (API và SAP NCo là x64).' }

# File giải nén từ zip tải về bị đánh dấu "Mark of the Web" → gỡ để Windows không chặn exe / dll.
Get-ChildItem $root -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- điều kiện máy
Say "`n== Kiểm tra điều kiện" 'Cyan'
$exe = Join-Path $api 'Rdm.Api.exe'
foreach ($f in 'Rdm.Api.exe', 'Rdm.Api.dll', 'appsettings.json', 'appsettings.Development.json', 'sapnco.dll', 'cpc4n.dll', 'ijwhost.dll') {
    if (-not (Test-Path (Join-Path $api $f))) { Fail "Thiếu api\$f — giải nén lại đủ gói (không chạy trực tiếp trong file zip)." }
}
# Runtime x64 (Rdm.Api.exe là x64; chỉ có runtime x86 trong "Program Files (x86)" thì exe không chạy)
$dotnetX64 = if ($env:ProgramW6432) { Join-Path $env:ProgramW6432 'dotnet' } else { Join-Path $env:ProgramFiles 'dotnet' }
$aspnet = @(Get-ChildItem (Join-Path $dotnetX64 'shared\Microsoft.AspNetCore.App') -Directory -Filter '8.*' -ErrorAction SilentlyContinue)
$core = @(Get-ChildItem (Join-Path $dotnetX64 'shared\Microsoft.NETCore.App') -Directory -Filter '8.*' -ErrorAction SilentlyContinue)
if ($aspnet.Count -eq 0 -or $core.Count -eq 0) {
    $x86 = Test-Path (Join-Path ${env:ProgramFiles(x86)} 'dotnet\shared\Microsoft.AspNetCore.App')
    Fail ("Thiếu ASP.NET Core Runtime 8 **x64** trong $dotnetX64" + $(if ($x86) { ' (máy chỉ có bản x86)' } else { '' }) +
          '. Tải "ASP.NET Core Runtime 8.0.x — Windows x64 Installer" hoặc Hosting Bundle: https://dotnet.microsoft.com/download/dotnet/8.0')
}
Say "  [OK] .NET x64: Microsoft.AspNetCore.App $($aspnet[-1].Name), Microsoft.NETCore.App $($core[-1].Name)" 'Green'
if (-not ((Test-Path "$env:windir\System32\vcruntime140.dll") -and (Test-Path "$env:windir\System32\vcruntime140_1.dll"))) {
    Fail 'Thiếu Visual C++ 2015-2022 x64 (SAP NCo cần). Tải: https://aka.ms/vs/17/release/vc_redist.x64.exe'
}
Say '  [OK] Visual C++ 2015-2022 x64' 'Green'

function Test-PortFree([int]$p) {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $p)
    try { $listener.Start(); return $true } catch { return $false } finally { try { $listener.Stop() } catch { } }
}
foreach ($p in @($Port) + @($HttpPort | Where-Object { $_ -gt 0 })) {
    if (-not (Test-PortFree $p)) { Fail "Cổng $p đang được chương trình khác dùng — chọn cổng khác: Start-Api.cmd -Port <cổng>." }
}
Say "  [OK] Cổng $Port$(if ($HttpPort) { ", $HttpPort" }) trống" 'Green'

function Test-Tcp([string]$hostName, [int]$port, [int]$timeoutMs = 4000) {
    $c = New-Object Net.Sockets.TcpClient
    try { return ($c.BeginConnect($hostName, $port, $null, $null).AsyncWaitHandle.WaitOne($timeoutMs) -and $c.Connected) }
    catch { return $false } finally { $c.Dispose() }
}

# ---------------------------------------------------------------- kết nối SQL / SAP (theo đúng cấu hình API sẽ dùng)
$cfg = Get-Content (Join-Path $api 'appsettings.Development.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$cs = if ($env:ConnectionStrings__SqlServer) { $env:ConnectionStrings__SqlServer } else { $cfg.ConnectionStrings.SqlServer }
$sqlServer = ([regex]::Match($cs, '(?i)(?:^|;)\s*(?:server|data source|addr|address)\s*=\s*([^;]+)')).Groups[1].Value.Trim()
$sapHost = if ($env:Sap__AppServerHost) { $env:Sap__AppServerHost } else { $cfg.Sap.AppServerHost }
$sapNr = if ($env:Sap__SystemNumber) { $env:Sap__SystemNumber } else { $cfg.Sap.SystemNumber }
Say "  SQL Server: $sqlServer $(if ($env:ConnectionStrings__SqlServer) { '(biến môi trường ConnectionStrings__SqlServer)' } else { '(appsettings.Development.json)' })"
if (-not $SkipChecks) {
    $sqlHost = ($sqlServer -replace '^tcp:', '') -split ',' | Select-Object -First 1
    $sqlPort = if ($sqlServer -match ',\s*(\d+)') { [int]$Matches[1] } else { 1433 }
    if ($sqlHost -match '\\') { Say "  [?] SQL dùng named instance ($sqlHost) — bỏ qua kiểm tra cổng" 'Yellow' }
    elseif (Test-Tcp ($sqlHost -replace '^\(local\)$|^\.$', 'localhost') $sqlPort) { Say "  [OK] Kết nối được SQL $sqlHost`:$sqlPort" 'Green' }
    else {
        Fail ("Không kết nối được SQL Server $sqlHost`:$sqlPort — API không khởi động được khi không có DB.`n" +
              "    Cấu hình đang trỏ 'localhost' nghĩa là SQL trên CHÍNH máy test. Trỏ về máy SQL thật (mở cmd tại thư mục này):`n" +
              "    set ConnectionStrings__SqlServer=Server=<IP máy SQL>,43310;Database=trdm;User Id=rdmuser;Password=<mật khẩu>;Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True`n" +
              "    Start-Api.cmd ...   (bỏ kiểm tra: thêm -SkipChecks)")
    }
    $sapPort = 3300 + [int]$sapNr
    if (Test-Tcp $sapHost $sapPort) { Say "  [OK] Kết nối được SAP $sapHost`:$sapPort" 'Green' }
    else { Say "  [!]  Không tới được SAP $sapHost`:$sapPort (VPN / firewall) — API vẫn chạy, nhưng gọi SAP sẽ lỗi SAP_UNAVAILABLE." 'Yellow' }
}

# ---------------------------------------------------------------- chứng chỉ HTTPS
if (-not $Http) {
    Say "`n== Chứng chỉ HTTPS" 'Cyan'
    if (-not $PfxPath) {
        $certDir = Join-Path $root 'cert'
        $PfxPath = Join-Path $certDir 'rdm-api.pfx'
        $passFile = "$PfxPath.password.txt"
        if (-not (Test-Path $PfxPath)) {
            Say '  Tạo chứng chỉ tự ký (lần đầu)...'
            New-Item -ItemType Directory -Path $certDir -Force | Out-Null
            $fqdn = try { [Net.Dns]::GetHostEntry('').HostName } catch { $env:COMPUTERNAME }
            $ips = @([Net.Dns]::GetHostAddresses('') | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.IPAddressToString })
            $names = @('localhost', $env:COMPUTERNAME, $fqdn) | Select-Object -Unique
            $san = (@($names | ForEach-Object { "DNS=$_" }) + @(@('127.0.0.1') + $ips | Select-Object -Unique | ForEach-Object { "IPAddress=$_" })) -join '&'
            $cert = New-SelfSignedCertificate -Subject "CN=$env:COMPUTERNAME" -FriendlyName 'RDM API console test' `
                -TextExtension @("2.5.29.17={text}$san", '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
                -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 `
                -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
            $bytes = New-Object byte[] 24
            [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            $generated = [Convert]::ToBase64String($bytes)
            Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password (ConvertTo-SecureString $generated -AsPlainText -Force) | Out-Null
            Export-Certificate -Cert $cert -FilePath (Join-Path $certDir 'rdm-api.cer') | Out-Null
            Set-Content $passFile $generated -Encoding ASCII
            Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
            Say "  Tên / IP trong chứng chỉ: $($san -replace 'DNS=|IPAddress=', '' -replace '&', ', ')"
        }
        $PfxPassword = (Get-Content $passFile -Raw).Trim()
    }
    if (-not (Test-Path $PfxPath)) { Fail "Không thấy file chứng chỉ $PfxPath." }
    try { $x509 = New-Object Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword) }
    catch { Fail "Không mở được $PfxPath (sai mật khẩu / file hỏng): $($_.Exception.Message)" }
    if (-not $x509.HasPrivateKey) { Fail "$PfxPath không có private key." }
    if ($x509.NotAfter -lt (Get-Date)) { Fail "Chứng chỉ $PfxPath đã hết hạn ($($x509.NotAfter))." }
    $env:Kestrel__Certificates__Default__Path = (Resolve-Path $PfxPath).Path
    $env:Kestrel__Certificates__Default__Password = $PfxPassword
    Say "  [OK] $($x509.Subject), hết hạn $($x509.NotAfter.ToString('dd/MM/yyyy'))" 'Green'
}

# ---------------------------------------------------------------- chạy
$scheme = if ($Http) { 'http' } else { 'https' }
$urls = @("${scheme}://0.0.0.0:$Port")
if ($HttpPort -gt 0 -and -not $Http) { $urls += "http://0.0.0.0:$HttpPort" }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $urls -join ';'

Say "`n RDM SAP API - console test" 'Green'
Say "  Swagger : ${scheme}://localhost:$Port/swagger    (máy khác: ${scheme}://$($env:COMPUTERNAME):$Port/swagger)"
Say "  Health  : ${scheme}://localhost:$Port/health"
if ($HttpPort -gt 0 -and -not $Http) { Say "  HTTP    : http://localhost:$HttpPort/swagger" }
if (-not $Http -and $PfxPath -like (Join-Path $root 'cert\*')) {
    Say "  Tự ký: máy client import $(Join-Path $root 'cert\rdm-api.cer') vào Trusted Root để hết cảnh báo." 'Yellow'
}
Say '  Dừng    : Ctrl+C (chờ job hàng loạt đang chạy xong trước khi dừng)'
Say ''

# Output của API vừa hiện trên màn hình vừa ghi vào log launcher (bắt được cả lỗi .NET in ra stderr trước khi Serilog chạy).
$started = Get-Date
Push-Location $api
$ErrorActionPreference = 'Continue'
try {
    & $exe 2>&1 | ForEach-Object {
        $line = if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
        Write-Host $line
        $log.WriteLine($line)
    }
    $code = $LASTEXITCODE
}
finally { Pop-Location; $ErrorActionPreference = 'Stop' }

# ---------------------------------------------------------------- API dừng: giải thích mã thoát
$hex = '0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int32]$code), 0)   # mã NTSTATUS / HRESULT
$reason = switch ($hex) {
    '0x00000000' { 'dừng bình thường' }
    '0xC000013A' { 'dừng bằng Ctrl+C' }
    '0xC0000135' { 'thiếu DLL hệ thống (Visual C++ x64 / file trong api\ bị thiếu)' }
    '0xC0000005' { 'crash bộ nhớ trong thư viện native (SAP NCo) — xem Event Log bên dưới' }
    '0xC0000409' { 'crash (fail-fast) — xem Event Log bên dưới' }
    '0xE0434352' { 'lỗi .NET chưa xử lý — xem api\logs\startup-*.log' }
    '0x80008083' { 'apphost: thiếu runtime .NET phù hợp' }
    '0x80008096' { 'apphost: không tìm thấy .NET 8 x64 (cài ASP.NET Core Runtime 8 x64)' }
    '0x8000809A' { 'apphost: không tìm thấy .NET 8 x64 (cài ASP.NET Core Runtime 8 x64)' }
    '0x00000002' { 'API không khởi động được do cấu hình / kết nối (thường là SQL Server) — xem dòng [FTL] phía trên' }
    default { if ($code -eq 1) { 'API báo lỗi khi khởi động — xem dòng [FTL] / [ERR] phía trên và api\logs\startup-*.log' } else { 'không rõ' } }
}
Say "`nAPI đã dừng sau $([int]((Get-Date) - $started).TotalSeconds) giây — mã thoát $code ($hex): $reason" $(if ($code -eq 0) { 'Gray' } else { 'Red' })

if ($code -ne 0 -and $hex -ne '0xC000013A') {
    $since = $started.AddSeconds(-5)
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Windows Error Reporting' -and $_.Message -like '*Rdm.Api*' } |
        Select-Object -First 3)
    foreach ($e in $events) { Say "--- Event Log ($($e.ProviderName), $($e.TimeCreated)):`n$($e.Message)" 'DarkYellow' }
    $startup = Get-ChildItem (Join-Path $api 'logs') -Filter 'startup-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($startup) { Say "Log khởi động của API: $($startup.FullName)" 'Yellow' }
}
Finish $code
