<#
  Test tích hợp /api/sd/prices-bapi trên SAP DEV (DRF-930) — chạy THẬT, ghi bảng giá vào SAP rồi tự hủy hiệu lực.
  Mỗi loại bảng giá (ZVLP, ZVDI, ZVIV, ZMIN/A912) chạy cùng bộ kịch bản: tạo, trùng, chồng hiệu lực, trùng trong request,
  sai dấu giá, sai master data, tạo chờ duyệt, duyệt, gỡ duyệt, sửa, đổi hiệu lực, hủy hiệu lực.
  ZMIN chỉ tạo theo A912 (Incoterms) như VK11; truyền Region (A919) → PRICE_INVALID_KEY.

  Chạy:  powershell -NoProfile -File tests\integration\PriceBapi.DevTests.ps1 -Password '<mật khẩu admin API>' [-Year 2032]
  Kết quả: bảng PASS/FAIL trên console + file CSV (tham số -Report).
#>
param(
    [string]$BaseUrl = 'http://localhost:5200',
    [string]$User = 'admin',
    [string]$Password = $env:RDM_API_PASSWORD,
    [int]$Year = 2032,
    [string]$Report = (Join-Path $PSScriptRoot 'PriceBapi.DevTests.result.csv')
)
$ErrorActionPreference = 'Stop'
if (-not $Password) { throw 'Thiếu -Password (hoặc biến môi trường RDM_API_PASSWORD).' }

$token = (Invoke-RestMethod -Method Post "$BaseUrl/api/auth/token" -ContentType 'application/json' `
    -Body (@{ username = $User; password = $Password } | ConvertTo-Json)).accessToken
$run = Get-Date -Format 'MMddHHmmss'
$results = New-Object System.Collections.Generic.List[object]
$created = New-Object System.Collections.Generic.List[string]

# PowerShell 5.1 không đọc được body của HTTP 4xx → dùng curl.exe
function Call([string]$Method, [string]$Path, $Body) {
    $out = [IO.Path]::GetTempFileName()
    $a = @('-s', '-o', $out, '-w', '%{http_code}', '-X', $Method, "$BaseUrl$Path", '-H', "Authorization: Bearer $token")
    if ($null -ne $Body) {
        $in = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($in, ($Body | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
        $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$in")
    }
    $code = [int](& curl.exe @a)
    $text = [IO.File]::ReadAllText($out, [Text.Encoding]::UTF8)
    [pscustomobject]@{ Code = $code; Json = $(if ($text) { $text | ConvertFrom-Json } else { $null }) }
}

function D([string]$md) { "$Year-$md" }

function Record($case, $name, [bool]$ok, $detail) {
    $results.Add([pscustomobject]@{ Case = $case; Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0,-4} {1,-14} {2}  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $case, $name, $detail) -ForegroundColor $color
}

function Summary($r) {
    $j = $r.Json
    $codes = ($j.records | ForEach-Object { "[$($_.item)]$($_.errorCode)" }) -join ' '
    "HTTP $($r.Code) $($j.status) ok=$($j.successCount) fail=$($j.failedCount) skip=$($j.skippedCount) err=$($j.errorCode) $codes"
}

# Kỳ vọng: HTTP + số thành công / bỏ qua + mã lỗi của từng dòng (item → code)
function Expect($case, $name, $r, [int]$http, [int]$success = 0, [int]$skipped = 0, [hashtable]$codes = @{}) {
    $j = $r.Json
    $ok = $r.Code -eq $http -and [int]$j.successCount -eq $success -and [int]$j.skippedCount -eq $skipped
    foreach ($k in $codes.Keys) {
        $rec = $j.records | Where-Object { "$($_.item)" -eq "$k" } | Select-Object -First 1
        if (-not $rec -or $rec.errorCode -ne $codes[$k]) { $ok = $false }
    }
    Record $case $name $ok (Summary $r)
    foreach ($rec in $j.records) { if ($rec.success -and $rec.conditionRecordNumber) { $created.Add($rec.conditionRecordNumber) } }
}

function Knumh($r, $item = '1') { ($r.Json.records | Where-Object { "$($_.item)" -eq $item } | Select-Object -First 1).conditionRecordNumber }

# Đọc lại trên SAP qua GET /api/sd/prices (theo khóa + khoảng hiệu lực)
function Read-Prices($t, $from, $to) {
    $q = "conditionType=$($t.Type)&salesOrganization=G210&validFrom=$from&validTo=$to&includeDeleted=true&material=$($t.Key.material)"
    foreach ($f in 'distributionChannel', 'soldTo', 'shipTo', 'shippingCondition', 'plant', 'incoterms', 'region') {
        if ($t.Key.$f) { $q += "&$f=$($t.Key.$f)" }
    }
    @((Call 'GET' "/api/sd/prices?$q" $null).Json.items)
}

function Verify($case, $name, $t, $knumh, [scriptblock]$cond, $from = (D '01-01'), $to = (D '12-31')) {
    $rows = @(Read-Prices $t $from $to | Where-Object { $_.conditionRecordNumber -eq $knumh })
    $ok = $rows.Count -gt 0 -and (& $cond $rows)
    $detail = ($rows | ForEach-Object { "$($_.validFrom)..$($_.validTo) amt=$($_.amount) pending=$($_.pendingApproval) deleted=$($_.deleted)" }) -join ' | '
    Record $case $name $ok "KNUMH $knumh $detail"
}

function VerifyNone($case, $name, $t, $from, $to) {
    $rows = @(Read-Prices $t $from $to | Where-Object { -not $_.deleted })
    Record $case $name ($rows.Count -eq 0) "bản ghi hiệu lực trong $from..$to = $($rows.Count)"
}

function Item($t, $from, $to, [decimal]$amount, $unit = 'TO') {
    $i = @{ conditionType = $t.Type; amount = $amount; currency = 'VND'; pricingUnit = 1; unit = $unit; validFrom = $from; validTo = $to }
    foreach ($p in $t.Key.GetEnumerator()) { $i[$p.Key] = $p.Value }
    $i
}

function Create($t, $tag, [bool]$pending, [object[]]$items) {
    Call 'POST' '/api/sd/prices-bapi' @{ requestId = "IT-$run-$($t.Type)$($t.Table)-$tag"; pendingApproval = $pending; items = $items }
}
function Status($t, $tag, $action, [string[]]$knumhs) {
    Call 'POST' "/api/sd/prices-bapi/$action" @{ requestId = "IT-$run-$($t.Type)$($t.Table)-$tag"; conditionRecordNumbers = $knumhs }
}
function Change($t, $tag, [object[]]$items) {
    Call 'POST' '/api/sd/prices-bapi/change' @{ requestId = "IT-$run-$($t.Type)$($t.Table)-$tag"; items = $items }
}

# ------------------------------------------------------------------ dữ liệu test (khóa trống hiệu lực năm $Year trên DRF-930)

$types = @(
    @{ Type = 'ZVLP'; Table = 'A990'; Price = 1500000; Key = [ordered]@{ salesOrganization = 'G210'; distributionChannel = '10'; soldTo = '100166'; shipTo = '100166'; material = '100026'; shippingCondition = 'PK'; plant = 'G112' } },
    @{ Type = 'ZVDI'; Table = 'A990'; Price = -50000; Key = [ordered]@{ salesOrganization = 'G210'; distributionChannel = '10'; soldTo = '100166'; shipTo = '100166'; material = '100026'; shippingCondition = 'PK'; plant = 'G112' } },
    @{ Type = 'ZVIV'; Table = 'A991'; Price = 1000000; Key = [ordered]@{ salesOrganization = 'G210'; material = '100026'; plant = 'G130' } },
    @{ Type = 'ZMIN'; Table = 'A912'; Price = 900000; Key = [ordered]@{ salesOrganization = 'G210'; distributionChannel = '10'; material = '100026'; incoterms = 'CIF' } }
)

foreach ($t in $types) {
    $p = "$($t.Type)/$($t.Table)"
    Write-Host "`n===== $p =====" -ForegroundColor Cyan
    $price = [decimal]$t.Price

    # Dọn sạch trước (lần chạy trước bị dừng giữa chừng)
    $left = @(Read-Prices $t (D '01-01') (D '12-31') | Where-Object { -not $_.deleted } | ForEach-Object { $_.conditionRecordNumber } | Select-Object -Unique)
    if ($left.Count -gt 0) { $null = Status $t 'PRECLEAN' 'invalidate' $left; Write-Host "  dọn trước: $($left -join ', ')" }

    # TC01 tạo đã duyệt
    $r = Create $t 'TC01' $false @((Item $t (D '01-01') (D '03-31') $price))
    Expect "$p TC01" 'Tạo bảng giá đã duyệt Q1' $r 200 -success 1
    $k1 = Knumh $r
    Verify "$p TC01" 'SAP: có record, đã duyệt, đúng giá' $t $k1 { param($x) $x.Count -eq 1 -and -not $x[0].pendingApproval -and [decimal]$x[0].amount -eq $price -and $x[0].validTo -eq (D '03-31') }

    # TC02 trùng nguyên kỳ
    Expect "$p TC02" 'Tạo trùng khóa + trùng kỳ → chặn' (Create $t 'TC02' $false @((Item $t (D '01-01') (D '03-31') $price))) 422 -codes @{ '1' = 'PRICE_SAME_PERIOD_EXISTS' }
    # TC03 chồng hiệu lực
    Expect "$p TC03" 'Tạo chồng hiệu lực một phần → chặn' (Create $t 'TC03' $false @((Item $t (D '03-01') (D '05-31') $price))) 422 -codes @{ '1' = 'PRICE_OVERLAP' }
    Expect "$p TC03b" 'Tạo kỳ bao trùm bản có sẵn → chặn' (Create $t 'TC03b' $false @((Item $t (D '01-01') (D '12-31') $price))) 422 -codes @{ '1' = 'PRICE_OVERLAP' }
    # TC04 trùng trong request
    Expect "$p TC04" '2 dòng cùng khóa chồng nhau trong 1 request → chặn cả request' (Create $t 'TC04' $false @(
            (Item $t (D '07-01') (D '08-31') $price), (Item $t (D '08-01') (D '09-30') $price))) 422 -codes @{ '2' = 'PRICE_DUPLICATE_IN_REQUEST' }
    VerifyNone "$p TC04" 'SAP: không ghi dòng nào của request lỗi' $t (D '07-01') (D '09-30')
    # TC05 sai dấu giá
    Expect "$p TC05" 'Sai dấu giá → chặn' (Create $t 'TC05' $false @((Item $t (D '10-01') (D '10-31') (-$price)))) 422 -codes @{ '1' = 'PRICE_INVALID_AMOUNT' }
    # TC06 master data sai (ưu tiên trước chồng hiệu lực)
    Expect "$p TC06" 'Đơn vị ZZZ + chồng hiệu lực → chỉ báo master data' (Create $t 'TC06' $false @((Item $t (D '01-01') (D '03-31') $price 'ZZZ'))) 422 -codes @{ '1' = 'PRICE_INVALID_MASTER_DATA' }
    $bad = Item $t (D '10-01') (D '10-31') $price; $bad.material = '999999999'
    Expect "$p TC06b" 'Vật tư không tồn tại → chặn' (Create $t 'TC06b' $false @($bad)) 422 -codes @{ '1' = 'PRICE_INVALID_MASTER_DATA' }
    VerifyNone "$p TC06" 'SAP: không ghi gì ở kỳ 10' $t (D '10-01') (D '10-31')

    # TC07 tạo chờ duyệt Q2 (liền kề Q1, không chồng)
    $r = Create $t 'TC07' $true @((Item $t (D '04-01') (D '06-30') $price))
    Expect "$p TC07" 'Tạo bảng giá chờ duyệt Q2' $r 200 -success 1
    $k2 = Knumh $r
    Verify "$p TC07" 'SAP: KFRST = A (chờ duyệt)' $t $k2 { param($x) $x.Count -eq 1 -and $x[0].pendingApproval }
    # TC08 chồng với bản chờ duyệt
    Expect "$p TC08" 'Tạo chồng với bản chờ duyệt → chặn' (Create $t 'TC08' $false @((Item $t (D '06-01') (D '06-30') $price))) 422 -codes @{ '1' = 'PRICE_OVERLAP_PENDING' }

    # TC09–12 duyệt / gỡ duyệt (idempotent)
    Expect "$p TC09" 'Duyệt bản chờ duyệt' (Status $t 'TC09' 'approve' @($k2)) 200 -success 1
    Verify "$p TC09" 'SAP: KFRST trống (đã duyệt)' $t $k2 { param($x) -not $x[0].pendingApproval }
    Expect "$p TC10" 'Duyệt lại bản đã duyệt → bỏ qua' (Status $t 'TC10' 'approve' @($k2)) 200 -skipped 1
    Expect "$p TC11" 'Gỡ duyệt' (Status $t 'TC11' 'unapprove' @($k2)) 200 -success 1
    Verify "$p TC11" 'SAP: KFRST = A' $t $k2 { param($x) $x[0].pendingApproval }
    Expect "$p TC12" 'Gỡ duyệt lại → bỏ qua' (Status $t 'TC12' 'unapprove' @($k2)) 200 -skipped 1

    # TC13 sửa giá khi đang chờ duyệt → vẫn chờ duyệt
    $newPrice = $price + [Math]::Sign($price) * 10000
    Expect "$p TC13" 'Sửa giá bản chờ duyệt' (Change $t 'TC13' @(@{ conditionRecordNumber = $k2; amount = $newPrice })) 200 -success 1
    Verify "$p TC13" 'SAP: giá mới, vẫn chờ duyệt' $t $k2 { param($x) [decimal]$x[0].amount -eq $newPrice -and $x[0].pendingApproval }
    # TC14 kéo dài Q1 chồng sang Q2
    Expect "$p TC14" 'Kéo dài hiệu lực Q1 chồng Q2 (chờ duyệt) → chặn' (Change $t 'TC14' @(@{ conditionRecordNumber = $k1; validTo = (D '04-30') })) 422 -codes @{ '1' = 'PRICE_OVERLAP_PENDING' }
    # TC15 rút ngắn Q1
    Expect "$p TC15" 'Rút ngắn hiệu lực Q1 tới 15/03' (Change $t 'TC15' @(@{ conditionRecordNumber = $k1; validTo = (D '03-15') })) 200 -success 1
    Verify "$p TC15" 'SAP: đúng 1 kỳ 01/01–15/03, không còn kỳ cũ' $t $k1 { param($x) $x.Count -eq 1 -and $x[0].validTo -eq (D '03-15') }
    # TC15b kéo dài lại vào khoảng trống (không chồng)
    Expect "$p TC15b" 'Kéo dài Q1 tới 31/03 (khoảng trống)' (Change $t 'TC15b' @(@{ conditionRecordNumber = $k1; validTo = (D '03-31') })) 200 -success 1
    Verify "$p TC15b" 'SAP: đúng 1 kỳ 01/01–31/03' $t $k1 { param($x) $x.Count -eq 1 -and $x[0].validTo -eq (D '03-31') }
    # TC15c sửa trùng KNUMH trong request
    Expect "$p TC15c" 'Sửa 1 KNUMH 2 lần trong request → chặn' (Change $t 'TC15c' @(@{ conditionRecordNumber = $k1; amount = $price }, @{ conditionRecordNumber = $k1; amount = $price })) 422 -codes @{ '2' = 'PRICE_DUPLICATE_IN_REQUEST' }

    # TC16–18 hủy hiệu lực
    Expect "$p TC16" 'Hủy hiệu lực 2 bảng giá' (Status $t 'TC16' 'invalidate' @($k1, $k2)) 200 -success 2
    Verify "$p TC16" 'SAP: Q1 đánh dấu xóa' $t $k1 { param($x) $x[0].deleted }
    Verify "$p TC16" 'SAP: Q2 đánh dấu xóa' $t $k2 { param($x) $x[0].deleted }
    Expect "$p TC17" 'Hủy lại → bỏ qua' (Status $t 'TC17' 'invalidate' @($k1, $k2)) 200 -skipped 2
    Expect "$p TC18" 'Duyệt bản đã hủy → chặn' (Status $t 'TC18' 'approve' @($k1)) 422 -codes @{ $k1 = 'PRICE_DELETED' }
    Expect "$p TC18b" 'Sửa bản đã hủy → chặn' (Change $t 'TC18b' @(@{ conditionRecordNumber = $k1; amount = $price })) 422 -codes @{ '1' = 'PRICE_DELETED' }

    # TC19 tạo lại sau khi hủy (bản đã xóa không chặn)
    $r = Create $t 'TC19' $false @((Item $t (D '01-01') (D '03-31') $price))
    Expect "$p TC19" 'Tạo lại kỳ Q1 sau khi bản cũ đã hủy' $r 200 -success 1
    $k3 = Knumh $r
    Expect "$p TC19" 'Dọn: hủy bản vừa tạo' (Status $t 'TC19x' 'invalidate' @($k3)) 200 -success 1
}

# ------------------------------------------------------------------ ZMIN theo Region (A919)
Write-Host "`n===== ZMIN/A919 =====" -ForegroundColor Cyan
$t919 = @{ Type = 'ZMIN'; Table = 'A919'; Key = [ordered]@{ salesOrganization = 'G210'; distributionChannel = '10'; region = '01'; material = '100026' } }
Expect 'ZMIN/A919 TC01' 'ZMIN theo Region (A919) → chặn: chỉ tạo theo A912' (Create $t919 'TC01' $false @((Item $t919 (D '01-01') (D '03-31') 900000))) 422 -codes @{ '1' = 'PRICE_INVALID_KEY' }
$noKey = Item $t919 (D '01-01') (D '03-31') 900000; $noKey.Remove('region')
Expect 'ZMIN/A919 TC01b' 'ZMIN thiếu Incoterms → chặn' (Create $t919 'TC01b' $false @($noKey)) 422 -codes @{ '1' = 'PRICE_INVALID_KEY' }
$both = Item $t919 (D '01-01') (D '03-31') 900000; $both.incoterms = 'CIF'
Expect 'ZMIN/A919 TC02' 'Truyền cả Incoterms và Region → chặn' (Create $t919 'TC02' $false @($both)) 422 -codes @{ '1' = 'PRICE_INVALID_KEY' }

# ------------------------------------------------------------------ dọn + báo cáo
# Bản Q1 đầu tiên (TC01) đã bị SAP thay dòng Axxx khi tạo lại cùng kỳ ở TC19 (cùng DATBI, như VK11) → không còn trong Axxx: bỏ qua NOT_FOUND.
$alive = @($created | Select-Object -Unique)
if ($alive.Count -gt 0) {
    $r = Call 'POST' '/api/sd/prices-bapi/invalidate' @{ requestId = "IT-$run-CLEANUP"; conditionRecordNumbers = $alive }
    $other = @($r.Json.records | Where-Object { $_.errorCode -and $_.errorCode -ne 'PRICE_NOT_FOUND' })
    Record 'CLEANUP' 'Hủy hiệu lực mọi record test (bỏ qua bản đã bị thay dòng Axxx)' ($other.Count -eq 0) (Summary $r)
}
foreach ($t in $types) {
    VerifyNone "$($t.Type)/$($t.Table) END" "SAP: năm $Year không còn bảng giá hiệu lực của khóa test" $t (D '01-01') (D '12-31')
}

$results | Export-Csv -Path $Report -NoTypeInformation -Encoding UTF8
$fail = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ("`nTổng: {0} kiểm tra, {1} PASS, {2} FAIL. Báo cáo: {3}" -f $results.Count, ($results.Count - $fail), $fail, $Report) -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
Write-Host "Record đã tạo (đã hủy hiệu lực): $($alive -join ', ')"
exit $fail
