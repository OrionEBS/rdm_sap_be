<#
.SYNOPSIS
  Khởi động Rdm.Api trên máy dev ở port chỉ định — chạy nền, độc lập với cửa sổ / terminal đã mở nó.

.DESCRIPTION
  Chạy bin\<Configuration>\net8.0\Rdm.Api.exe (thư mục làm việc src\Rdm.Api → nạp appsettings.Development.json).
  Chưa có bản build → tự build. Port đang có API của repo → báo, dùng Restart.ps1; port bị ứng dụng khác giữ → dừng, không tắt hộ.
  Log: .run\api-<port>.log (+ .err), PID: .run\api-<port>.pid.

.EXAMPLE
  scripts\Start.ps1                        # http://0.0.0.0:5200
  scripts\Start.ps1 -Port 5201
  scripts\Start.ps1 -Port 5443 -Https      # HTTPS (chứng chỉ dev của dotnet: dotnet dev-certs https --trust)
  scripts\Start.ps1 -Port 5202 -NoHangfireServer   # instance thứ 2 không chạy job nền
  scripts\Start.ps1 -Port 5200 -Foreground # chạy trong cửa sổ hiện tại (Ctrl+C để dừng)
  scripts\Start.ps1 -Port 5210 -Target dev # trỏ DB Dev: nạp thêm config\targets\dev.json (ghi đè chuỗi kết nối, nhãn môi trường)
  scripts\Start-Ui.ps1 -ApiPort 5210       # giao diện quản trị gọi API đó
#>
[CmdletBinding()]
param(
    [int]$Port = 5200,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    # 0.0.0.0 = mọi card mạng (máy khác trong LAN gọi được); localhost = chỉ máy này.
    [string]$BindAddress = '0.0.0.0',
    [switch]$Https,
    [string]$Environment = 'Development',
    # Không chạy Hangfire server trong instance này (VD đã có instance / worker khác chạy job).
    [switch]$NoHangfireServer,
    [switch]$Foreground,
    # Không tự build khi thiếu bản build.
    [switch]$NoBuild,
    # Target DB / môi trường: tên file trong config\targets (VD dev) hoặc đường dẫn .json. Trống = appsettings.Development.json.
    [string]$Target = ''
)
. (Join-Path $PSScriptRoot '_Dev.ps1')

$targetFile = $null
if ($Target) {
    $targetFile = if ($Target -match '\.json$') { $Target } else { Join-Path $RepoRoot "config\targets\$Target.json" }
    if (-not (Test-Path $targetFile)) {
        $have = (Get-ChildItem (Join-Path $RepoRoot 'config\targets') -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object BaseName) -join ', '
        throw "Không có target '$Target' ($targetFile). Target hiện có: $have"
    }
    $targetFile = (Resolve-Path $targetFile).Path
}

if ($Port -lt 1 -or $Port -gt 65535) { throw "Port không hợp lệ: $Port" }
if ($ReservedPorts.ContainsKey($Port)) { throw "Port $Port dành cho $($ReservedPorts[$Port]) — chọn port khác (-Port)." }
$scheme = if ($Https) { 'https' } else { 'http' }
$urls = "${scheme}://${BindAddress}:$Port"

$running = Get-RdmApiOnPort $Port
if ($running) {
    Write-Warn "Port $Port đã có Rdm.Api chạy: $(Format-Proc $running[0])"
    Write-Host "  Khởi động lại: scripts\Restart.ps1 -Port $Port   ·   Dừng: scripts\Stop.ps1 -Port $Port"
    exit 1
}

$exe = Get-ApiExe $Configuration
if (-not (Test-Path $exe)) {
    if ($NoBuild) { throw "Chưa có $exe — chạy scripts\Build.ps1 -Configuration $Configuration." }
    Write-Step "Chưa có bản build $Configuration — build Rdm.Api"
    Invoke-Checked dotnet @('build', 'src\Rdm.Api', '-c', $Configuration, '--nologo', '-v', 'q', '-clp:ErrorsOnly;Summary')
}

$env:ASPNETCORE_ENVIRONMENT = $Environment
if ($NoHangfireServer) { $env:Hangfire__ServerEnabled = 'false' } else { Remove-Item Env:Hangfire__ServerEnabled -ErrorAction SilentlyContinue }
# RDM_CONFIG_FILE nạp SAU appsettings.{Env}.json → giá trị trong target ghi đè (chuỗi kết nối, nhãn môi trường…).
if ($targetFile) { $env:RDM_CONFIG_FILE = $targetFile } else { Remove-Item Env:RDM_CONFIG_FILE -ErrorAction SilentlyContinue }

if ($Foreground) {
    Write-Step "Rdm.Api $urls ($Configuration, $Environment) — Ctrl+C để dừng"
    Push-Location $ApiProject
    try { & $exe --urls $urls } finally { Pop-Location }
    exit $LASTEXITCODE
}

New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
$log = Join-Path $RunDir "api-$Port.log"
Remove-Item $log, "$log.err" -Force -ErrorAction SilentlyContinue

Write-Step "Khởi động Rdm.Api $urls ($Configuration, $Environment$(if ($targetFile) { ", target $Target" }))"
$proc = Start-Process $exe -ArgumentList '--urls', $urls -WorkingDirectory $ApiProject -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"
Set-Content (Join-Path $RunDir "api-$Port.pid") $proc.Id -Encoding ASCII
# Restart.ps1 dùng lại target của lần chạy trước.
if ($targetFile) { Set-Content (Join-Path $RunDir "api-$Port.target") $targetFile -Encoding UTF8 }
else { Remove-Item (Join-Path $RunDir "api-$Port.target") -Force -ErrorAction SilentlyContinue }

$health = Wait-ApiHealth $Port $proc $log
$hostName = if ($BindAddress -in '0.0.0.0', '*', '+') { $env:COMPUTERNAME } else { $BindAddress }
Write-Ok "PID $($proc.Id) · phiên bản $($health.version) · DB = $($health.database)"
if ($targetFile) {
    $db = ([regex]::Match((Get-Content $targetFile -Raw), '"SqlServer"\s*:\s*"Server=([^;]+);Database=([^;]+)')).Groups
    Write-Host "  Target:    $Target → DB $($db[1].Value.Replace('\\', '\')) / $($db[2].Value)   ($targetFile)" -ForegroundColor Yellow
}
Write-Host "  API:       ${scheme}://localhost:$Port   (LAN: ${scheme}://${hostName}:$Port)"
Write-Host "  Swagger:   ${scheme}://localhost:$Port/swagger   ·   nội bộ: ${scheme}://localhost:$Port/swagger-internal"
Write-Host "  Hangfire:  ${scheme}://localhost:$Port/hangfire$(if ($NoHangfireServer) { '  (instance này không chạy job)' })"
Write-Host "  Log:       $log"
Write-Host "  Dừng:      scripts\Stop.ps1 -Port $Port   ·   Khởi động lại: scripts\Restart.ps1 -Port $Port"
