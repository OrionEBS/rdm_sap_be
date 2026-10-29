<#
  Test khai báo giá hàng loạt (đầu năm) qua /api/sd/prices-bapi/jobs trên SAP DEV — ghi THẬT rồi tự hủy hiệu lực.
  Lấy cặp sold-to / ship-to (G210 / 10, ship-to mặc định) và vật tư (đơn vị TO, chưa xóa) từ DB local (md.*),
  tạo -Count bảng giá ZVLP năm -Year (plant G112, PK), theo dõi tiến độ, rồi hủy toàn bộ bằng job invalidate.
  Khóa đã có bảng giá chồng năm -Year (VD bản hiệu lực tới 31.12.9999) bị API chặn → script bỏ các dòng đó và gửi lại 1 lần.

  Chạy:  powershell -NoProfile -File tests\integration\PriceBapi.BulkTest.ps1 -Password '<mật khẩu admin API>' [-Count 1200] [-Year 2035]
#>
param(
    [string]$BaseUrl = 'http://localhost:5200',
    [string]$User = 'admin',
    [string]$Password = $env:RDM_API_PASSWORD,
    [int]$Count = 1200,
    [int]$Year = 2035,
    [string]$Db = 'Server=localhost,43310;Database=trdm;User Id=rdmuser;Password=Tafico$2026;TrustServerCertificate=True'
)
$ErrorActionPreference = 'Stop'
if (-not $Password) { throw 'Thiếu -Password (hoặc biến môi trường RDM_API_PASSWORD).' }

$token = (Invoke-RestMethod -Method Post "$BaseUrl/api/auth/token" -ContentType 'application/json' `
    -Body (@{ username = $User; password = $Password } | ConvertTo-Json)).accessToken
$run = Get-Date -Format 'MMddHHmmss'

function Call([string]$Method, [string]$Path, $Body) {
    $out = [IO.Path]::GetTempFileName()
    $a = @('-s', '-o', $out, '-w', '%{http_code}', '-X', $Method, "$BaseUrl$Path", '-H', "Authorization: Bearer $token")
    if ($null -ne $Body) {
        $in = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($in, ($Body | ConvertTo-Json -Depth 6 -Compress), (New-Object Text.UTF8Encoding $false))
        $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$in")
    }
    $code = [int](& curl.exe @a)
    $text = [IO.File]::ReadAllText($out, [Text.Encoding]::UTF8)
    [pscustomobject]@{ Code = $code; Json = $(if ($text) { $text | ConvertFrom-Json } else { $null }) }
}

# Gửi job rồi theo dõi tới khi xong; trả (kết quả cuối, số giây)
function RunJob([string]$Action, $Body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Call 'POST' "/api/sd/prices-bapi/jobs/$Action" $Body
    Write-Host ("  POST jobs/{0}: HTTP {1} status={2}" -f $Action, $r.Code, $r.Json.status)
    if ($r.Code -ne 202) { return @($r, $sw.Elapsed.TotalSeconds) }
    do {
        Start-Sleep -Seconds 5
        $r = Call 'GET' "/api/sd/prices-bapi/jobs/$Action/$($Body.requestId)" $null
        $p = $r.Json.progress
        if ($p) { Write-Host ("  {0,6:N0}s  {1}/{2}  ok={3} lỗi={4}" -f $sw.Elapsed.TotalSeconds, $p.done, $p.total, $p.succeeded, $p.failed) }
        else { Write-Host ("  {0,6:N0}s  đang chờ / đang kiểm tra dữ liệu..." -f $sw.Elapsed.TotalSeconds) }
    } while ($r.Code -eq 202)
    return @($r, $sw.Elapsed.TotalSeconds)
}

# ------------------------------------------------------------------ dữ liệu từ DB local
$c = New-Object System.Data.SqlClient.SqlConnection $Db; $c.Open()
function Q($sql) { $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $rd = $cmd.ExecuteReader(); $t = New-Object System.Data.DataTable; $t.Load($rd); $t }
$customers = @(Q "SELECT TOP 200 r.SoldTo, MIN(r.ShipTo) AS ShipTo FROM md.SoldToShipTo r JOIN md.SoldTo s ON s.CustomerNumber = r.SoldTo
                 WHERE r.SalesOrg = 'G210' AND r.DistributionChannel = '10' GROUP BY r.SoldTo ORDER BY r.SoldTo")
$materials = @(Q "SELECT MaterialNumber FROM md.Material WHERE IsDeleted = 0 AND BaseUnit = 'TO' ORDER BY MaterialNumber")
$c.Close()
Write-Host "Nguồn: $($customers.Count) cặp sold-to/ship-to, $($materials.Count) vật tư (TO)"

$items = New-Object System.Collections.Generic.List[object]
foreach ($m in $materials) {
    foreach ($cu in $customers) {
        if ($items.Count -ge $Count) { break }
        $items.Add(@{ conditionType = 'ZVLP'; salesOrganization = 'G210'; distributionChannel = '10'; soldTo = $cu.SoldTo; shipTo = $cu.ShipTo;
                      material = $m.MaterialNumber; shippingCondition = 'PK'; plant = 'G112'; amount = 1000000 + $items.Count; currency = 'VND';
                      pricingUnit = 1; unit = 'TO'; validFrom = "$Year-01-01"; validTo = "$Year-12-31" })
    }
}
Write-Host "Tạo $($items.Count) bảng giá ZVLP năm $Year"

# ------------------------------------------------------------------ tạo hàng loạt
Write-Host "`n== TẠO (jobs/create)" -ForegroundColor Cyan
$res = RunJob 'create' @{ requestId = "BULK-$run"; pendingApproval = $false; items = $items }
$r = $res[0]; $createSec = $res[1]
if ($r.Json.errorCode -eq 'PRICE_VALIDATION') {
    $bad = @($r.Json.records | Where-Object { -not $_.success } | ForEach-Object { [int]$_.item })
    $codes = $r.Json.records | Where-Object { -not $_.success } | Group-Object errorCode | ForEach-Object { "$($_.Name)=$($_.Count)" }
    Write-Host "  $($bad.Count) dòng bị chặn khi kiểm tra ($($codes -join ', ')) — bỏ các dòng này và gửi lại" -ForegroundColor Yellow
    $keep = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $items.Count; $i++) { if ($bad -notcontains ($i + 1)) { $keep.Add($items[$i]) } }
    $items = $keep
    $res = RunJob 'create' @{ requestId = "BULK-$run-2"; pendingApproval = $false; items = $items }
    $r = $res[0]; $createSec = $res[1]
}
$created = @($r.Json.records | Where-Object { $_.success } | ForEach-Object { $_.conditionRecordNumber })
Write-Host ("  KẾT QUẢ TẠO: HTTP {0} {1} — {2}/{3} thành công, {4} lỗi — {5:N1}s ({6:N0} ms/bảng giá)" -f `
    $r.Code, $r.Json.status, $r.Json.successCount, $items.Count, $r.Json.failedCount, $createSec, ($createSec * 1000 / [math]::Max(1, $items.Count)))

# ------------------------------------------------------------------ hủy hàng loạt
if ($created.Count -gt 0) {
    Write-Host "`n== HỦY HIỆU LỰC (jobs/invalidate)" -ForegroundColor Cyan
    $res = RunJob 'invalidate' @{ requestId = "BULKDEL-$run"; conditionRecordNumbers = $created }
    $d = $res[0]; $delSec = $res[1]
    Write-Host ("  KẾT QUẢ HỦY: HTTP {0} {1} — {2}/{3} thành công, {4} lỗi, {5} bỏ qua — {6:N1}s ({7:N0} ms/bảng giá)" -f `
        $d.Code, $d.Json.status, $d.Json.successCount, $created.Count, $d.Json.failedCount, $d.Json.skippedCount, $delSec, ($delSec * 1000 / $created.Count))
}
