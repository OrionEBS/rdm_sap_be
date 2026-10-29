# Hàm dùng chung cho script dev (Build / Package / Start / Restart / Stop) trên máy build E:\rdmapi.
# Chỉ đụng tới tiến trình của CHÍNH repo này (đường dẫn exe hoặc dòng lệnh nằm dưới thư mục repo) — không bao giờ tắt
# HRM.API, IIS (w3wp) hay ứng dụng khác trên máy.

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path $PSScriptRoot
$script:RunDir = Join-Path $script:RepoRoot '.run'          # log + pid của API chạy bằng Start.ps1
$script:ApiProject = Join-Path $script:RepoRoot 'src\Rdm.Api'
# Port dành cho ứng dụng khác trên máy này — Start.ps1 từ chối dùng kể cả khi đang trống (ứng dụng đó có thể đang tắt).
$script:ReservedPorts = @{ 5100 = 'HRM.API'; 3000 = 'IIS hrm-api'; 3001 = 'IIS hrm-api'; 5000 = 'IIS hrmapp'; 8001 = 'IIS app1'; 5173 = 'Vite admin-ui' }

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  OK  $text" -ForegroundColor Green }
function Write-Warn([string]$text) { Write-Host "  !!  $text" -ForegroundColor Yellow }

function Invoke-Checked([string]$exe, [string[]]$arguments, [string]$workDir = $script:RepoRoot) {
    Push-Location $workDir
    # PowerShell 5.1: lệnh native ghi stderr (cảnh báo npm / dotnet) khi bị redirect sẽ thành lỗi dừng script
    # → chỉ xét exit code.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @arguments 2>&1 | ForEach-Object { "$_" }
        $rc = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $eap; Pop-Location }
    if ($rc -ne 0) { throw "$exe $($arguments -join ' ') lỗi (exit $rc)." }
}

# Tiến trình thuộc repo: Rdm.Api / Rdm.Worker / Rdm.DbSync (bin, artifacts, gói console-test giải nén trong repo)
# + "dotnet run/watch" của các project đó. -IncludeUi: thêm Vite dev server của admin-ui.
function Get-RdmProcesses([switch]$IncludeUi) {
    $root = $script:RepoRoot.TrimEnd('\') + '\'
    Get-CimInstance Win32_Process | Where-Object {
        $p = $_
        $path = [string]$p.ExecutablePath
        $cmd = [string]$p.CommandLine
        (($p.Name -in 'Rdm.Api.exe', 'Rdm.Worker.exe', 'Rdm.DbSync.exe') -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) -or
        ($p.Name -eq 'dotnet.exe' -and $cmd -match '\b(run|watch)\b' -and $cmd -match 'Rdm\.(Api|Worker|DbSync)' -and
            ($cmd.IndexOf($script:RepoRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $cmd -match '--project\s+"?src[\\/]')) -or
        ($IncludeUi -and $p.Name -eq 'node.exe' -and $cmd -match 'vite' -and $cmd.IndexOf('admin-ui', [StringComparison]::OrdinalIgnoreCase) -ge 0)
    }
}

function Get-PortOwner([int]$port) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Get-CimInstance Win32_Process -Filter "ProcessId=$($c.OwningProcess)" }
}

function Format-Proc($p) {
    $ports = @(Get-NetTCPConnection -OwningProcess $p.ProcessId -State Listen -ErrorAction SilentlyContinue | ForEach-Object LocalPort | Sort-Object -Unique)
    "PID {0,-6} {1,-15} {2}{3}" -f $p.ProcessId, $p.Name, $(if ($ports) { "port $($ports -join ',')  " } else { '' }),
        $(if ($p.ExecutablePath -and $p.Name -ne 'dotnet.exe' -and $p.Name -ne 'node.exe') { $p.ExecutablePath } else { ([string]$p.CommandLine).Trim() })
}

function Stop-Processes($processes, [string]$reason) {
    $list = @($processes | Where-Object { $_ })
    if ($list.Count -eq 0) { Write-Ok "Không có tiến trình RDM nào đang chạy$reason"; return }
    foreach ($p in $list) {
        Write-Host "  tắt $(Format-Proc $p)"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    # Chờ file (dll / exe) được nhả hẳn trước khi build / copy.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @($list | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }
    Write-Ok "Đã tắt $($list.Count) tiến trình$reason"
}

function Stop-RdmProcesses([switch]$IncludeUi) {
    Stop-Processes (Get-RdmProcesses -IncludeUi:$IncludeUi) ''
    Get-ChildItem $script:RunDir -Filter 'api-*.pid' -ErrorAction SilentlyContinue | Remove-Item -Force
}

# API của repo đang nghe ở port này (null nếu port trống). Port bị ứng dụng KHÁC giữ → lỗi, không tắt hộ.
function Get-RdmApiOnPort([int]$port) {
    $owner = Get-PortOwner $port
    if (-not $owner) { return $null }
    $mine = @(Get-RdmProcesses | Where-Object { $_.ProcessId -eq $owner.ProcessId -or $_.ProcessId -eq $owner.ParentProcessId })
    if ($mine.Count -eq 0) { throw "Port $port đang bị ứng dụng khác giữ: $(Format-Proc $owner). Chọn port khác (-Port)." }
    # Kèm "dotnet run" cha (nếu có) để không tự khởi động lại.
    $parents = @(Get-RdmProcesses | Where-Object { $_.ProcessId -eq $owner.ParentProcessId })
    return @($owner) + $parents
}

function Get-ApiExe([string]$configuration) { Join-Path $script:ApiProject "bin\$configuration\net8.0\Rdm.Api.exe" }

function Wait-ApiHealth([int]$port, $process, [string]$log, [int]$timeoutSeconds = 120) {
    $url = "http://localhost:$port/health"
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            Write-Host "`n  API thoát ngay (exit $($process.ExitCode)). Log cuối:" -ForegroundColor Red
            Get-Content $log -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" }
            Get-Content "$log.err" -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" }
            throw "Rdm.Api không khởi động được trên port $port."
        }
        try {
            $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return ($r.Content | ConvertFrom-Json) }
        }
        catch {
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 503) {
                Write-Warn "API chạy nhưng /health = 503 (DB không kết nối được) — xem log $log"
                return $null
            }
        }
        Start-Sleep -Milliseconds 700
    }
    throw "Quá $timeoutSeconds giây chưa thấy $url trả 200 — xem log $log"
}
