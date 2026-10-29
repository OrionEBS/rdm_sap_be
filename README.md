# RDM – SAP ECC 6.0 Integration API

ASP.NET Core 8 Web API ghi chứng từ sang SAP ECC 6.0 (DEV: DRF / client 930) qua **SAP NCo 3.1.6 for .NET (core)**.
NCo trong `sapnco/lib` là bản hỗ trợ .NET 8 nên chạy **in-process** (không cần bridge .NET Framework).

## Script dev: biên dịch, đóng gói, start / restart (`scripts\`)

Chạy bằng PowerShell (`scripts\X.ps1 ...`) hoặc double-click / cmd (`scripts\X.cmd ...`). Build và đóng gói **tự tắt trước** các
tiến trình RDM của repo đang chạy (Rdm.Api / Rdm.Worker / Rdm.DbSync / `dotnet run`, kể cả gói console-test chạy trong `artifacts\`) —
không đụng HRM.API, IIS hay ứng dụng khác.

| Script | Việc |
|---|---|
| `Build.ps1 [-Configuration Release] [-Test] [-SkipUi] [-Start -Port 5201]` | Tắt tiến trình → `dotnet build` solution → (test) → build admin-ui |
| `Package.ps1 [-Only Deploy,ConsoleTest,AdminUi] [-SkipTests] [-NoData]` | Tắt tiến trình → gói IIS + Worker (kèm ảnh dữ liệu), gói console-test, gói admin-ui — cùng 1 phiên bản trong `artifacts\` |
| `Start.ps1 [-Port 5200] [-Https] [-BindAddress localhost] [-NoHangfireServer] [-Foreground]` | Chạy API nền ở port chỉ định, chờ `/health`, in URL; log `.run\api-<port>.log` |
| `Restart.ps1 [-Port 5200] [-Build]` | Dừng API ở port đó → khởi động lại; `-Build`: tắt hết, build lại rồi khởi động |
| `Stop.ps1 [-Port 5201] [-List] [-IncludeUi]` | Dừng API ở 1 port / mọi tiến trình RDM; `-List` chỉ liệt kê |

**Chạy trỏ DB Dev (hoặc DB khác):** mỗi target là 1 file `config\targets\<tên>.json` ghi đè cấu hình (chuỗi kết nối, nhãn môi trường
`Environment:Label` / `Color`…) – nạp qua `RDM_CONFIG_FILE` sau `appsettings.Development.json`. File có mật khẩu, không đóng gói.

```powershell
scripts\Start.ps1 -Port 5210 -Target dev        # API → DB Dev (172.16.4.101\s25x / trdm)
scripts\Start-Ui.ps1 -Port 5174 -ApiPort 5210    # admin-ui → API đó (proxy, không cần CORS)
scripts\Restart.ps1 -Port 5210                   # tự dùng lại target dev của lần chạy trước (-Target none để bỏ)
```

Trang quản trị hiện nhãn môi trường + DB đang trỏ ở thanh trên (`GET /api/admin/system`, Admin, không trả mật khẩu).

**Sửa cấu hình có hiệu lực ngay khi deploy (DEV / production):** Rdm.Api và Rdm.Worker theo dõi `appsettings.json`,
`appsettings.{Env}.json` và file `RDM_CONFIG_FILE` (`D:\Apps\Rdm\config\appsettings.Production.json`). File đổi nội dung và
JSON hợp lệ → dừng có kiểm soát (ghi nốt log / job) → IIS chạy lại API ở request kế tiếp, service Worker được recovery khởi động
lại sau 5 giây (mã thoát 3). JSON lỗi → giữ cấu hình đang chạy, ghi log `[ERR] … KHÔNG hợp lệ`. Bật / tắt:
`Hosting:RestartOnConfigChange` (mặc định bật, trừ Development). Admin-ui: sửa `config.js` (apiBaseUrl) – không cache, F5 là nhận.

`Start.ps1` từ chối port dành riêng (5100 HRM.API, 3000 / 3001 / 5000 / 8001 IIS, 5173 Vite) và port đang bị ứng dụng khác giữ.
Gói triển khai luôn xóa chuỗi kết nối trong `dbsync\appsettings.json` (mật khẩu, và tránh nạp dữ liệu nhầm sang DB Dev khi cài).

## Chạy

```powershell
dotnet tool restore
dotnet build Rdm.SapIntegration.sln
cd src/Rdm.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --launch-profile http        # http://localhost:5200/swagger
dotnet test ../../tests/Rdm.Tests
```

- Cấu hình DEV (SQL `localhost,43310/trdm`, SAP `10.72.1.39`) nằm trong `src/Rdm.Api/appsettings.Development.json`.
  Môi trường khác: đặt qua biến môi trường, vd. `ConnectionStrings__SqlServer`, `Sap__AppServerHost`, `Sap__Client`, `Sap__User`, `Sap__Password`.
- Migration EF tự áp khi khởi động (`Database:MigrateOnStartup`). Thủ công: `cd src/Rdm.Infrastructure; dotnet ef database update`.
- Build bắt buộc **x64** (`cpc4n.dll` / `ijwhost.dll` native x64). `sapnco.dll` cần package `System.Configuration.ConfigurationManager` trong deps.json — chỉ copy DLL là không đủ.

## Chứng thực (JWT)

Mọi API đều yêu cầu token (chính sách mặc định "bắt buộc đăng nhập" — endpoint mới thêm cũng tự được bảo vệ).

1. `POST /api/auth/token` với `{"username": "...", "password": "..."}` → `accessToken` (hạn `Jwt:ExpiryMinutes`, mặc định 60 phút).
2. Gửi header `Authorization: Bearer <accessToken>` cho mọi API. Thiếu / sai / hết hạn token → **401**; không đủ quyền → **403**.
3. Token hết hạn → gọi lại bước 1.

Trên Swagger: gọi `/api/auth/token`, bấm **Authorize**, dán token.

- Tài khoản lưu ở `rdm.ApiUsers`, mật khẩu băm PBKDF2 (ASP.NET Identity), ghi qua `rdm.usp_ApiUser_*`.
- Role `Admin`: thêm quản trị tài khoản — `GET /api/auth/users`, `PUT /api/auth/users/{username}` (tạo, đổi mật khẩu, đổi role, khóa với `isActive=false`).
  Role `User`: gọi các API nghiệp vụ. `GET /api/auth/me`: thông tin token hiện tại.
- Lần chạy đầu (chưa có tài khoản nào) tự tạo admin từ `Auth:BootstrapAdmin` — **đổi mật khẩu ngay** sau khi đăng nhập.
- Khóa ký `Jwt:SigningKey` ≥ 32 byte; production đặt qua biến môi trường `Jwt__SigningKey` (thiếu khóa → API không khởi động).
- Swagger và dashboard Hangfire (`/hangfire`, chỉ truy cập từ localhost) không cần token.

## Bật / tắt API (bảng cấu hình `rdm.ApiEndpoints`)

Mỗi API (khóa `METHOD route`, VD `POST api/sd/prices-bapi/jobs/create`) có cờ `IsActive`. API **inactive**:
không hiện trên Swagger (kể cả schema chỉ API đó dùng) và trả **404 y hệt URL không tồn tại** — kể cả khi không gửi token
(không trả 401) và khi gọi sai method (không trả 405).

- Khởi động: API tự đồng bộ danh sách trong code vào bảng (API mới = `ApiEndpoints:DefaultActive`, mặc định `true`;
  API đã xóa khỏi code giữ lại, `inCode = false`).
- Đổi trạng thái áp dụng ngay trên instance nhận lệnh; instance khác sau tối đa `ApiEndpoints:RefreshSeconds` (mặc định 30).
  DB tạm lỗi khi làm mới → giữ trạng thái đã nạp (không tự mở lại API đang tắt).
- **Khóa (luôn active):** `api/auth/*` (đăng nhập, quản lý user) và `api/admin/endpoints/*`.
- **Giao diện quản trị:** project React tách riêng [admin-ui/](admin-ui/README.md) — đăng nhập Admin, bật / tắt từng API / cả nhóm /
  nhiều API, tìm kiếm, ghi chú. FE ở origin khác → khai báo `Cors:AllowedOrigins` (dev: `http://localhost:5173`).

| API (chỉ role Admin) | Việc |
|---|---|
| `GET /api/admin/endpoints[?group=PricesBapi&active=false]` | Danh sách: `key`, `httpMethod`, `route`, `group` (tên controller), `description` (từ tài liệu Swagger), `isActive`, `isLocked`, `inCode`, `note`, `updatedBy`, `updatedAtUtc` |
| `GET /api/admin/endpoints/groups` | Tổng hợp theo nhóm: `total`, `activeCount`, `inactiveCount`, `lockedCount` |
| `PUT /api/admin/endpoints/status` | `{ "keys": ["POST api/sd/prices-bapi/jobs/create"], "groups": ["MasterData"], "isActive": false, "note": "..." }` → `changed`, `skippedLocked`, `alreadyInState`, `unknownKeys`, `unknownGroups` |
| `POST /api/admin/endpoints/refresh` | Nạp lại bảng ngay (sau khi sửa trực tiếp trên DB) |

DBA (không qua giao diện) — dùng procedure, rồi bấm "Nạp lại cache" / gọi `POST /api/admin/endpoints/refresh` hoặc chờ tối đa 30 giây.
Procedure tự bỏ qua API khóa:

```sql
SELECT * FROM rdm.vw_ApiEndpoints ORDER BY Nhom, Route;                                   -- xem nhanh (có mô tả)
EXEC rdm.usp_ApiEndpoint_SetActiveByPattern N'%prices-bapi%', 0, N'chưa mở cho team test';  -- tắt theo mẫu (route / nhóm / khóa)
EXEC rdm.usp_ApiEndpoint_SetActiveByPattern N'MasterData', 1;                             -- bật lại cả nhóm
```

## Cột output theo API (bảng `rdm.ApiOutputProfiles`)

Trang quản trị → nút **⚙ Cột output** ở từng API (hoặc API `/api/admin/endpoints/output`, role Admin). Áp dụng ngay; instance khác
sau tối đa `ApiEndpoints:RefreshSeconds`. Swagger (đối tác + nội bộ) hiện response theo cấu hình.

- **Ẩn / hiện, đổi tên, sắp thứ tự** field trong JSON trả về — kể cả field lồng (`items[].customerName`). Tên mới không được trùng
  field cùng cấp; field không có trong output → 400. Chỉ áp cho kết quả đúng kiểu response thành công (lỗi / ProblemDetails giữ nguyên;
  422 dùng chung kiểu với 200 như `PostingResult` thì cũng áp).
- **Reset như ban đầu**: xóa cấu hình gốc (`DELETE /api/admin/endpoints/output/profiles/{id}`) → output như trong code.
- **Sao chép thành API mới**: `/api/c/{tên}/{tham số route của API gốc}` — cùng method, request, quyền và xử lý, output theo cấu hình
  riêng (VD bản rút gọn cho 1 đối tác). Tắt bản sao / API gốc đang tắt / sai method / sai số tham số → 404.
- Ghi chỉ qua `rdm.usp_ApiOutputProfile_Save` / `_Delete` / `_Import`; xem nhanh: `SELECT * FROM rdm.vw_ApiOutputProfiles`.
- Đồng bộ DB: `rdm.ApiOutputProfiles` nằm trong mẫu mặc định của Rdm.DbSync / nút "Đồng bộ DB Dev".

## Tài liệu API: đối tác vs nội bộ

| Tài liệu | URL | Nội dung | Ai xem |
|---|---|---|---|
| **Đối tác** | `/swagger`, `/swagger/v1/swagger.json` | Chỉ API đang bật, không phải API quản trị, không bị "ẩn với đối tác". Tóm tắt / mô tả = **mô tả đối tác** do admin soạn (chưa soạn → để trống). Mọi mô tả kỹ thuật từ code (tham số, schema, response, tag) bị xóa | Đối tác bên ngoài |
| **Nội bộ** | `/swagger-internal`, `/swagger/internal/swagger.json` | Tất cả API (API tắt có tiền tố `[ĐANG TẮT]`), mô tả kỹ thuật đầy đủ (BAPI, bảng, field SAP) | Chỉ từ chính máy chủ (localhost) hoặc `ApiDocs:InternalOpen = true`; máy khác nhận 404 |
| **Tài liệu TS** | `GET /api/admin/endpoints/export/ts` (Admin) | File Markdown: mục lục, từng API (mô tả nội bộ + mô tả đối tác, tham số, body, response), phụ lục kiểu dữ liệu | Team nội bộ |
| **OpenAPI nội bộ** | `GET /api/admin/endpoints/export/openapi-internal` (Admin) | File JSON của tài liệu nội bộ (import Postman / sinh client) | Team nội bộ |

- Soạn mô tả đối tác: giao diện [admin-ui](admin-ui/README.md) nút "✎ Sửa mô tả đối tác" (cảnh báo khi gõ thuật ngữ SAP), hoặc
  `PUT /api/admin/endpoints/doc` `{ "key": "POST api/sd/prices-bapi/jobs/create", "publicSummary": "...", "publicDescription": "...", "publicHidden": false }`
  (≤ 200 / 2000 ký tự; ghi qua `rdm.usp_ApiEndpoint_SetDoc`). Áp dụng ngay trên Swagger đối tác.
- "Ẩn với đối tác" chỉ ẩn khỏi tài liệu — API **vẫn gọi được**; muốn chặn hẳn thì tắt API (mục trên).
- Mô tả nội bộ lấy từ XML doc trong code (`///`), đồng bộ vào cột `Description` khi khởi động.
- Cấu hình: `ApiDocs:PublicTitle`, `ApiDocs:PublicDescription` (tiêu đề / giới thiệu Swagger đối tác), `ApiDocs:InternalOpen` (mặc định `false`).
- Giới hạn: tên route (`/api/sd/prices-bapi/...`) và tên kiểu dữ liệu (`PriceBapiCreateRequest`...) vẫn chứa chữ "Bapi" trong tài liệu
  đối tác — đổi sẽ phá client đang dùng.

## Endpoint

| Method | Path | SAP |
|---|---|---|
| POST | `/api/fi/fb01` | `Z_EFI011_CREATE_FB01` (tự COMMIT) |
| POST | `/api/fi/fb75` | `Z_EFI009_CREATE_FB75` (tự COMMIT; `businessTransaction` phải là `G`) |
| POST | `/api/fi/reversal` | `BAPI_ACC_DOCUMENT_REV_POST` + `BAPI_TRANSACTION_COMMIT` (cùng stateful session) |
| POST | `/api/fi/documents` | `BAPI_ACC_DOCUMENT_POST` + `BAPI_TRANSACTION_COMMIT` — chứng từ FI nhiều dòng (G/L, khách hàng, thuế); xem mục bên dưới |
| POST | `/api/fi/documents/check` | `BAPI_ACC_DOCUMENT_CHECK` — chỉ kiểm tra, không ghi, không lưu nhật ký |
| POST | `/api/sd/contracts` | `BAPI_CONTRACT_CREATEFROMDATA` + COMMIT — tạo sale contract ở trạng thái chờ duyệt |
| POST | `/api/sd/contracts/change` | `BAPI_CUSTOMERCONTRACT_CHANGE` + COMMIT — sửa header, thêm / sửa / từ chối dòng |
| GET | `/api/sd/prices` | Đọc bảng giá ZVLP / ZVDI / ZMIN / ZVIV (Axxx + KONP, `RFC_READ_TABLE`) |
| POST | `/api/sd/prices/check` | Kiểm tra lô bảng giá (khóa, hiệu lực, dấu giá, trùng / chồng hiệu lực) — không ghi |
| POST | `/api/sd/prices` | Tạo 1 / nhiều bảng giá — `Z_ESD015_PRICE_UPLOAD` (VK11 bằng BDC) |
| POST | `/api/sd/prices/change` | Sửa 1 / nhiều bảng giá theo KNUMH (VK12) |
| POST | `/api/sd/prices/approve` | Duyệt 1 / nhiều bảng giá (VK12 processing status 02) |
| POST | `/api/sd/prices/copy-with-vat` | Tạo bảng giá mới từ bảng giá cũ theo VAT% |
| POST | `/api/sd/contracts/approve` | `BAPI_CUSTOMERCONTRACT_CHANGE` + COMMIT — gỡ block duyệt |
| POST | `/api/sd/va01` | `Z_ESD014_CREATE_VA01` (tự COMMIT; chỉ loại đơn `ZVCR`) |
| POST | `/api/sd/va41` | `Z_ESD013_CREATE_VA41` (tự COMMIT; chỉ loại hợp đồng `ZVCF`) |
| POST | `/api/sd/vbn1` | `Z_ESD012_CREATE_VBN1` (batch input VBN1; chỉ `ZA00`; `keyCombination` 1 = theo khách hàng, 2 = sales office + customer group 3; thành công không có số chứng từ) |
| POST | `/api/mm/material-tax-classification` | `BAPI_MATERIAL_SAVEDATA` — đổi MLAN-TAXM1 cho danh sách vật tư (thay MM17); xem mục bên dưới |
| GET | `/api/customer-credits` | `RFC_READ_TABLE` KNKK — tất cả khách hàng |
| GET | `/api/customer-credits/{kunnr}?creditControlArea=G110` | `RFC_READ_TABLE` KNKK theo KUNNR (+ KKBER tùy chọn) |
| GET | `/api/postings`, `/api/postings/{id}`, `/api/postings/{operation}/{requestId}` | tra cứu nhật ký |
| GET | `/api/sap/ping` | `RFC_SYSTEM_INFO` |
| — | `/hangfire` | dashboard (chỉ localhost) |

Ngày gửi dạng ISO (`2026-10-13`); API tự đổi sang `DD.MM.YYYY` mà các hàm Z yêu cầu. Tiền tệ do SAP cố định VND.

## Chống tạo chứng từ trùng

Mọi request ghi bắt buộc có `requestId`. Bảng `rdm.SapPostings` unique theo (operation, requestId):

| Trạng thái | HTTP | Gửi lại cùng requestId |
|---|---|---|
| `Succeeded` | 200 | trả lại kết quả cũ (`replayed: true`), không gọi SAP |
| `Failed` – lỗi nghiệp vụ | 422 | được, có thể sửa payload |
| `Failed` – không kết nối/đăng nhập được SAP | 503 | được |
| `Unknown` – mất kết nối sau khi đã gửi lệnh | 502 | **409** — phải kiểm tra trên SAP rồi gửi bằng requestId mới |
| đang xử lý / payload khác với lần đã thành công | 409 | — |

Lời gọi ghi **không bao giờ retry**. Polly chỉ retry và có circuit breaker ở bước ping trước khi gửi lệnh, lúc đó SAP chưa ghi gì.

## Số chứng từ trả về

Ba hàm Z không có tham số export. Số chứng từ được đọc từ `ET_RETURN`:
- FB01/FB75: `S RW 605`, `MESSAGE_V2` = OBJ_KEY (BELNR+BUKRS+GJAHR). Response trả `objectType/objectKey/objectSystem` để dùng cho bước đảo chứng từ.
- VA01 / VA41: `S V1 311`, `MESSAGE_V2` = VBELN.
- VBN1: `ET_RETURN` kiểu BDCMSGCOLL; thành công = `S VK 382` (condition record, không có số). Lỗi trả kèm màn hình + field BDC.

Dữ liệu test DEV: khách `100166` chỉ được mở cho sales area **G210 / 10 / 11**, shipping condition `PK`, sales office `VN05`.

## Chứng từ FI nhiều dòng (BAPI_ACC_DOCUMENT_POST)

Thay các bút toán bị giới hạn bởi Z_EFI011_CREATE_FB01 (cấn trừ chiết khấu, chiết khấu thanh toán nhiều hóa đơn, lãi quá hạn, trích trước).

```json
{ "requestId": "ZB-0001", "companyCode": "G210", "documentType": "ZB", "documentDate": "2026-10-15", "postingDate": "2026-10-15",
  "reference": "RDM-QT-0001", "headerText": "Quyết toán CK",
  "lines": [
    { "lineType": "GL", "debitCredit": "S", "amount": 70000, "glAccount": "31140010", "costCenter": "G21010103", "assignment": "CT-01" },
    { "lineType": "Customer", "debitCredit": "H", "amount": 70000, "customer": "100166", "creditControlArea": "G110" } ] }
```
- `debitCredit` S = Nợ, H = Có; `amount` luôn dương. Posting key do SAP tự xác định (DRF không có BAdI/BTE can thiệp):
  G/L 40/50, khách hàng 01/11, khách hàng có `specialGlIndicator` 09/19. Tổng Nợ phải bằng tổng Có (validate trước khi gọi SAP).
- Dòng `Tax`: BAPI **không tự tính thuế** — truyền số thuế + `taxBaseAmount`.
- `reference` (XBLNR) bắt buộc: trước khi post API chặn nếu đã có chứng từ chưa đảo cùng company code + loại CT + **năm tài chính** + XBLNR
  (năm lấy từ `BAPI_COMPANYCODE_GET_PERIOD` — G210 không theo năm dương lịch, 15/10/2026 thuộc FY 2027). `allowDuplicateReference: true` để bỏ qua.
- Kiểm chứng trên DRF-930: `USERNAME` bắt buộc (API tự điền user RFC); validation ZFI 010 bắt buộc `creditControlArea` trên dòng khách hàng
  (G210/10/**10** → G100, G210/10/**11** → G110); TK 311xxxxx cần đối tượng CO → dùng `costCenter`. `profitabilitySegment` (CO-PA) hiện báo
  KF 018 vì DRF chưa cấu hình PA transfer structure FI. Loại CT có sẵn: ZB Rebates, ZL Accruals, ZD DN to Customer.
- Response trả `objectType/objectKey/objectSystem` (BKPFF) → đảo bằng `/api/fi/reversal` (lý do 05 cho hoàn nhập trích trước).

## Sale contract (tạo / sửa / duyệt)

- Loại cho phép: `Sap:ContractTypes` (mặc định ZVCQ, ZVCF). Duyệt = delivery block + billing block ở header
  (`Sap:ContractApprovalDeliveryBlock` / `ContractApprovalBillingBlock`, mặc định **30** "Price Chg. Approval").
- **Tạo** (`requireApproval` mặc định true): gắn block duyệt; dòng tự đánh 10, 20…; partner AG (+ WE/RE/RG nếu truyền).
- **Sửa**: đọc VBAK/VBAP/VEDA trước, chỉ đổi field được truyền; `items[].action` = `Add` / `Update` / `Reject`
  (`rejectionReason`, vd. Z0 "VN manual reject order"); không đổi được vật tư của dòng cũ. `requireReapproval` mặc định true → gắn lại block.
- **Duyệt**: chỉ gỡ block đúng mã duyệt (block khác như Z1 credit giữ nguyên, trả cảnh báo); không còn block → thành công, không gọi SAP.
- ⚠️ Enhancement **ZESD_QUOTATION_CHECK** kiểm tra giá bán ≥ ZMIN khi `SY-TCODE` trống (mọi lời gọi RFC/BAPI), trừ loại có trong
  `ZTSD_VN_UNCHECK` TYPE 2. **ZVCQ chưa có trong bảng** → tạo/sửa/duyệt ZVCQ qua API bị `E ZMM 033` "Selling price is lower than
  minimum selling price" (ZVCQ tạo tay bằng VA41 không bị kiểm tra nên giá 0). ZVCF chạy được (đã test hợp đồng 1300000644 trên DRF-930).

## Bảng giá ZVLP / ZVDI / ZMIN / ZVIV (Z_ESD015_PRICE_UPLOAD)

Ghi qua RFC **`Z_ESD015_PRICE_UPLOAD`** (`Sap:PriceConditionFunction`) — bản RFC của report upload giá đang dùng `ZCSD002_VN_PRICING_UPLD`
(ZSD_VN_PRICE_UPLD), chạy VK11 / VK12 bằng BDC: `C` tạo · `U` sửa · `R` duyệt. **Hàm phải được tạo trên SAP** theo
[abap/Z_ESD015_PRICE_UPLOAD.md](abap/Z_ESD015_PRICE_UPLOAD.md); chưa có thì API ghi trả `PRICE_RFC_MISSING`. `GET` và `/check` không cần hàm.
(`BAPI_PRICES_CONDITIONS` không remote-enabled và chưa được dùng ở đâu trên DRF-930 nên không dùng.)

| Loại | Bảng | Trường khóa trong request |
|---|---|---|
| ZVLP, ZVDI | A990 | salesOrganization, distributionChannel, soldTo, shipTo, material, shippingCondition, plant |
| ZVIV | A991 | salesOrganization (= VKORGAU), plant, material |
| ZMIN | A912 (A919 chỉ đọc) | salesOrganization, distributionChannel, material + `incoterms` (A912). Tạo / sửa theo `region` (A919) bị chặn `PRICE_INVALID_KEY` — VK11 nghiệp vụ không dùng region, DRF-930 không có region VN |

### Phương án BAPI — `/api/sd/prices-bapi` (tách riêng để so sánh với BDC)

Dùng **BAPI_PRICES_CONDITIONS** qua vỏ RFC `Z_ESD016_PRICES_CONDITIONS` (`Sap:PriceBapiFunction`; BAPI không remote-enabled —
tạo hàm theo [abap/Z_ESD016_PRICES_CONDITIONS.md](abap/Z_ESD016_PRICES_CONDITIONS.md), chưa có → `PRICE_RFC_MISSING`).

| API | Việc |
|---|---|
| `POST /api/sd/prices-bapi` | Tạo 1 / nhiều (009). **Luôn chặn** trùng / chồng hiệu lực: `PRICE_OVERLAP`, `PRICE_SAME_PERIOD_EXISTS`, `PRICE_OVERLAP_PENDING`, `PRICE_DUPLICATE_IN_REQUEST`. `pendingApproval: true` = tạo chờ duyệt |
| `POST …/change` | Sửa giá / pricing unit / đơn vị / hiệu lực đến theo KNUMH (004) |
| `POST …/invalidate` | Hủy hiệu lực = đánh dấu xóa (004 + INDIDELETE) |
| `POST …/approve` / `…/unapprove` | Duyệt (KFRST A → trống) / gỡ duyệt (trống → A) |

Response: `{ status, successCount, failedCount, skippedCount, errorCode, errorMessage, records[] }`; mỗi record:
`item` (số dòng / KNUMH), `conditionRecordNumber`, `success`, `errorCode` (mã RDM), `sapErrorCode` (message SAP `ID/NUMBER`),
`message`, `sapMessages`. Lỗi kiểm tra ở 1 dòng → không ghi dòng nào (`PRICE_VALIDATION`). Ghi theo lô
(`Sap:PriceBapiChunkSize`, mặc định 50 bảng giá / 1 lần gọi + commit); lô bị SAP từ chối → ghi lại từng bảng giá của lô để báo
đúng dòng lỗi → lỗi SAP ở vài record → `PRICE_PARTIAL`. Hủy / duyệt / gỡ duyệt bỏ qua record đã ở trạng thái đích và đọc lại SAP
(theo lô) để xác nhận. API đồng bộ tối đa **500 dòng** (quá → 400, hướng dẫn dùng jobs).

#### Khai báo giá hàng loạt (đầu năm) — `/api/sd/prices-bapi/jobs` (xử lý nền Hangfire, tối đa 20.000 dòng)

| API | Việc |
|---|---|
| `POST …/jobs/create` · `…/jobs/change` · `…/jobs/invalidate` · `…/jobs/approve` · `…/jobs/unapprove` | Body như API đồng bộ. Giữ chỗ `requestId` rồi đưa vào hàng đợi → **202** + `Location` |
| `GET …/jobs/{operation}/{requestId}` | **202** khi đang chờ / đang chạy (`progress`: `total`, `done`, `succeeded`, `failed`); xong → kết quả + HTTP như API đồng bộ; 404 không có requestId |

- Gửi lại cùng `requestId` + payload khi đang chạy → 202 trạng thái hiện tại; đã xong → kết quả cũ (`replayed`).
- Job không tự retry. Server tắt giữa chừng: tạo / sửa → `Unknown` (kiểm tra SAP rồi gửi phần thiếu bằng requestId mới);
  hủy / duyệt / gỡ duyệt (idempotent) → chạy lại.
- Đo trên DRF-930 (23/10/2026): tạo 1.197 bảng giá ZVLP ~5 giây, hủy 1.197 ~15 giây (trước tối ưu: 134 ms và 447 ms / bảng giá).
  Test: `tests\integration\PriceBapi.BulkTest.ps1 -Password … [-Count 1200] [-Year 2035]` (ghi thật rồi tự hủy).

### Lấy bảng giá — `GET /api/sd/prices`

Bắt buộc `conditionType`, `salesOrganization`, `validFrom`, `validTo` (lấy các kỳ có hiệu lực **giao** với khoảng này; thiếu / `validTo < validFrom` → 400).
Tùy chọn: `distributionChannel`, `soldTo`, `shipTo`, `material`, `shippingCondition`, `plant`, `incoterms`, `region`,
`pendingApproval` (true = chỉ chờ duyệt KFRST = A), `includeDeleted`. ZMIN không truyền incoterms / region → đọc cả A912 và A919.

Mỗi bảng có cấu trúc cột khóa khác nhau → response `{ conditionType, salesOrganization, validFrom, validTo, count, tables, items }`:
`tables[]` mô tả cột của từng bảng trong kết quả (`keyColumns`: tên JSON + field SAP + nhãn; `detailColumns` chung);
mỗi dòng `items[]` chỉ có cột khóa của bảng mình (`conditionTable`) + cột chi tiết: `conditionRecordNumber` (KNUMH),
`releaseStatus` (KFRST), `processingStatus` (KBSTAT), `validFrom` / `validTo`, KONP: `amount`, `currency`, `pricingUnit`, `unit`,
`calculationType` (KRECH), `deleted`.

| Bảng | Cột khóa trong `items` |
|---|---|
| A990 (ZVLP, ZVDI) | salesOrganization, distributionChannel, soldTo, shipTo, material / materialCode, shippingCondition, plant |
| A991 (ZVIV) | salesOrganization (VKORGAU), plant, material / materialCode |
| A912 (ZMIN) | salesOrganization, distributionChannel, material / materialCode, incoterms |
| A919 (ZMIN) | salesOrganization, distributionChannel, region, material / materialCode |

```json
{ "requestId": "BG-0001", "overlapMode": "Reject",
  "items": [ { "conditionType": "ZVLP", "salesOrganization": "G210", "distributionChannel": "10", "soldTo": "100001",
               "shipTo": "100001", "material": "760000", "shippingCondition": "PK", "plant": "G112",
               "amount": 1300000, "unit": "TO", "validFrom": "2027-01-01", "validTo": "2027-12-31" } ] }
```
- **Duyệt làm trên RDM**: bảng giá tạo / sửa qua API **có hiệu lực ngay** (BDC là batch input nên enhancement `ZESD004_DEFAULT_STAT` không
  gán KFRST = A, giống report upload hiện tại). `/approve` chỉ để duyệt bảng giá chờ duyệt (KFRST = A) nhập tay trên SAP — VK12 processing
  status 02 như **ZSD_VN_PRICE_APR**, sau đó đọc lại SAP xác nhận; bản đã duyệt → `PRICE_ALREADY_APPROVED` (gửi lại an toàn).
- **Kiểm tra theo lô** trước khi ghi: có dòng lỗi → `422 PRICE_VALIDATION`, không ghi dòng nào; mã lỗi ở `messages[].number`, số dòng ở
  `messages[].item`. **SAP ghi từng dòng** (commit riêng): lỗi SAP vài dòng → `422 PRICE_PARTIAL` (dòng `PRICE_OK` đã ghi, dòng
  `PRICE_NOT_CREATED` / `PRICE_NOT_CHANGED` chưa). Gửi lại cả lô an toàn: dòng đã tạo bị chặn bởi `PRICE_SAME_PERIOD_EXISTS`.
  Tạo xong API đọc lại bảng Axxx để trả KNUMH (`messages[].messageV1`).
- **Sửa** (`/change`, VK12): `amount`, `pricingUnit`, `unit`, `validTo` (không đổi được hiệu lực từ — tạo bản mới thay thế).
  Record bị SAP tách nhiều kỳ: đổi `validTo` phải truyền `periodStart` (ngày bắt đầu kỳ); giá dùng chung nên đổi giá áp cho cả record.
- **Trùng / chồng hiệu lực** (xét theo dòng bảng Axxx — record bị SAP tách có nhiều kỳ cùng KNUMH; bỏ qua record đã xóa):

| Mã | Khi nào |
|---|---|
| `PRICE_OVERLAP` | Chồng với bảng giá đã duyệt cùng khóa (`overlapMode = Reject`, mặc định khi tạo / sửa) |
| `PRICE_WILL_ADJUST_EXISTING` (W) | `overlapMode = Replace`: SAP (VK11) sẽ cắt / tách / thay bảng giá cũ khi tạo |
| `PRICE_OVERLAP_PENDING` | Chồng với bản đang chờ duyệt (KFRST = A) cùng khóa (luôn lỗi) |
| `PRICE_SAME_PERIOD_EXISTS` | Đã có bản cùng khóa, cùng nguyên kỳ → dùng API sửa |
| `PRICE_DUPLICATE_IN_REQUEST` | 2 dòng trong request trùng khóa + chồng hiệu lực (hoặc lặp KNUMH khi sửa) |
| `PRICE_INVALID_KEY` / `_CONDITION_TYPE` / `_PERIOD` / `_AMOUNT` | Thiếu / thừa trường khóa · loại ngoài 4 loại · đến < từ · sai dấu (ZVDI âm, loại khác dương) |
| `PRICE_NOT_FOUND` / `PRICE_DELETED` / `PRICE_NOT_SUPPORTED` / `PRICE_NO_CHANGE` | KNUMH không có · đã xóa · không thuộc 4 loại · dòng sửa không có field nào |
| `PRICE_SPLIT_RECORD` | Đổi `validTo` của record bị tách nhiều kỳ mà thiếu / sai `periodStart` |

- **Copy theo VAT** (`/copy-with-vat`): nguồn = bảng giá đã duyệt còn hiệu lực ngày `sourceDate` (mặc định `validFrom - 1`) khớp `source`;
  có `oldVatPercent` → giá mới = giá cũ / (1 + old%) × (1 + vat%) (đổi thuế suất, vd. 8 → 10); không có → giá cũ × (1 + vat%).
  Làm tròn theo `roundingUnit`; mặc định `overlapMode = Replace`. `messageV2` = KNUMH nguồn, `messageV3/V4` = giá cũ / mới.
- Giá đọc từ KONP quy đổi từ dạng lưu nội bộ theo TCURX (VND: `300.00` lưu = 30 000).

## Đổi tax classification vật tư (MLAN-TAXM1)

`POST /api/mm/material-tax-classification`
```json
{ "requestId": "TAX-001", "country": "VN", "taxClass": "5",
  "items": [ { "material": "100016" }, { "material": "100017", "taxClass": "1" } ] }
```
- `taxClass` chung áp cho vật tư không có giá trị riêng; tax category bỏ trống = loại thuế thứ 1 của quốc gia (TSTL, VN = `MWS1`).
  Giá trị MWS1: 0 No tax · 1 Full tax · 2 Half tax · 3 Low tax rate · 4 Mixed · 5 Tax rate 8%.
- Trước khi ghi đọc MARA + MLAN: vật tư không tồn tại → `NOT_FOUND` (BAPI sẽ tìm cách **tạo** vật tư nếu gọi thẳng);
  đã đúng giá trị → `UNCHANGED`, không gọi BAPI. Tối đa 500 vật tư / request.
- Mỗi vật tư 1 lần BAPI + `BAPI_TRANSACTION_COMMIT`. Kết quả từng vật tư trong `messages`: `item` = mã vật tư,
  `messageV1` / `messageV2` = giá trị cũ / mới, `number` = `CHANGED` / `UNCHANGED` / `NOT_FOUND` / `FAILED` / `NOT_PROCESSED`.
- RETURN của BAPI luôn `S MM/356` → kết quả thật lấy từ RETURNMESSAGES (`M3/801` đã đổi, `M3/810` không đổi, E = lỗi).
- Có vật tư lỗi → status `Failed` (HTTP 422; vật tư hợp lệ vẫn đã cập nhật). "Đặt TAXM1 = X" là idempotent nên gửi lại cùng
  `requestId` an toàn, kể cả khi mất kết nối giữa chừng.

## Đọc KNKK (customer credit)

- **Hàm RFC `Z_ESD017_READ_KNKK`** (tham số mã khách hàng IV_KUNNR, credit control area IV_KKBER) thay cho RFC_READ_TABLE:
  SAP trả sẵn số tiền giá trị thực + tiền tệ area, kiểm tra quyền F_KNKA_KKB, tự ALPHA mã khách hàng. Tạo trên SAP theo
  [abap/Z_ESD017_READ_KNKK.md](abap/Z_ESD017_READ_KNKK.md) (structure SE11 `ZSRDM_S_KNKK` + SE37, function group ZFG_SD_RDM).
- `Sap:CustomerCreditSource`: `Auto` (mặc định) = dùng hàm nếu đã có trên SAP, chưa có thì RFC_READ_TABLE (kiểm tra lại mỗi 10 phút —
  hàm mới kích hoạt được dùng không cần khởi động lại) · `Function` = luôn dùng hàm (chưa có → 502 `CREDIT_RFC_MISSING`) ·
  `ReadTable` = luôn RFC_READ_TABLE. Tên hàm: `Sap:CustomerCreditFunction`.
- Lỗi từ hàm: khách hàng / credit control area không tồn tại → **404** (`CREDIT_CUSTOMER_NOT_FOUND`, `CREDIT_AREA_NOT_FOUND`),
  không có quyền F_KNKA_KKB → **403** (`CREDIT_NO_AUTHORITY`), lỗi khác → 502.

Cách cũ (RFC_READ_TABLE — vẫn dùng khi chưa có hàm):

- RFC_READ_TABLE trả số tiền dạng lưu nội bộ (luôn 2 số lẻ). API quy đổi về giá trị thực theo TCURX: VND có 0 số lẻ
  nên `112941.20` → `11294120`. Tiền tệ của KLIMK/SAUFT/SKFOR/SSOBL lấy từ T014 theo KKBER; CASHA theo CASHC.
  T014/TCURX được cache 1 giờ.
- Số âm SAP trả dạng `112941.20-`; ngày `00000000` → `null`; KUNNR toàn số tự thêm 0 đầu (`100166` → `0000100166`).
- Đọc theo lô `Sap:ReadTableBatchSize` dòng (ROWSKIPS). Lời gọi đọc được retry qua Polly; lỗi kết nối → 503.

## Master data SD (lưu DB local, schema `md`)

Mỗi bảng 1 cặp API: `POST /api/master-data/{bảng}/sync` (đọc SAP → thay toàn bộ bảng local) và `GET /api/master-data/{bảng}` (đọc local).
`POST /api/master-data/sync-all` đồng bộ tất cả; Hangfire job `sync-master-data` chạy theo `Jobs:MasterDataSyncCron` (mặc định 01:00).
Lịch sử: `GET /api/master-data/sync-runs`.

| {bảng} | Bảng local | Nguồn SAP |
|---|---|---|
| `sold-to` | `md.SoldTo` | KNA1 + KNVP `AG`, account group thuộc `Sap:SoldToAccountGroups`, chưa đánh dấu xóa |
| `ship-to` | `md.ShipTo` | Danh mục ship-to (1 dòng / ship-to): KNA1 của các khách là partner `WE` của sold-to |
| `sold-to-ship-to` | `md.SoldToShipTo` | Quan hệ sold-to ↔ ship-to theo sales area: KNVP `WE` (lọc được theo `soldTo` hoặc `shipTo`) |
| `sales-districts` | `md.SalesDistrict` | T171 / T171T |
| `sales-offices` | `md.SalesOffice`, `md.SalesOfficeAssignment` | TVBUR / TVKBT + TVKBZ |
| `shipping-conditions` | `md.ShippingCondition` | TVSB / TVSBT |
| `sales-orgs` | `md.SalesOrg` | TVKO / TVKOT |
| `plants` | `md.Plant` | T001W **chỉ quốc gia VN** (`Sap:PlantCountries`) |
| `payment-methods` | `md.PaymentMethod` | T042Z |
| `vehicle-types` | `md.VehicleType` | T173 / T173T (shipping type) |
| `customer-groups-3` | `md.CustomerGroup3` | TVV3 / TVV3T |
| `materials` | `md.Material` | MARA **chỉ loại vật tư ZFNP** (`Sap:MaterialTypes`) + tên EN/VI từ MAKT + đơn vị tính T006A (mã nội bộ `BaseUnit` dùng khi gọi BAPI, mã hiển thị `BaseUnitExternal`) |
| `material-groups` | `md.MaterialGroup` | T023 / T023T |
| `material-types` | `md.MaterialType` | T134 / T134T |
| `divisions` | `md.Division` | TSPA / TSPAT |

| `gl-accounts` | `md.GlAccount` | `BAPI_GL_ACC_GETLIST` cho từng company code thuộc **COA 1000** (T001-KTOPL) **và** CO area G100 (TKA02) → G110, G120, G130, G210 |
| `cost-centers` | `md.CostCenter` | `BAPI_COSTCENTER_GETLIST1`, **CO area G100** |

COA / CO area cấu hình qua `Sap:ChartOfAccounts` (mặc định `1000`) và `Sap:ControllingArea` (mặc định `G100`).
Plant của mục 10 dùng chung bảng `plants` ở trên. `GET materials` mặc định bỏ vật tư bị đánh dấu xóa (`includeDeleted=true` để lấy cả);
`GET materials/{mã}` nhận mã hiển thị (`100016`) hoặc mã SAP đủ 18 ký tự.

- Text lưu cả tiếng Anh (`NameEn`) và tiếng Việt (`NameVi`). Cột `SapData` (JSON) chứa toàn bộ field của bảng gốc SAP.
- Sold-to: `CustomerName` = NAME2 + NAME3 + NAME4 (quy ước trên SAP: NAME1 là tên ngắn), mã số thuế = STCEG (trống thì STCD1),
  loại khách hàng = account group KTOKD + tên (T077X).
- Nếu SAP trả 0 dòng mà bảng local đang có dữ liệu → báo lỗi, giữ dữ liệu cũ.

**Mọi thao tác ghi DB đi qua stored procedure** (không ghi trực tiếp bảng): master data `md.usp_*_Replace` nhận table-valued
parameter (DELETE + INSERT trong 1 transaction), nhật ký `md.usp_SyncRun_Insert`, posting `rdm.usp_SapPosting_*`, dọn log
`dbo.usp_SerilogEvents_Purge`. Script nằm ở `src/Rdm.Infrastructure/Persistence/Sql/*.sql`, được EF migration tạo.
Ngoại lệ: bảng nội bộ của Hangfire và Serilog sink do thư viện tự ghi.

## Nhật ký gọi SAP — bằng chứng đối soát với EU (`rdm.SapCallLog`)

Mọi lời gọi function module (BAPI / Z RFC / RFC_READ_TABLE / commit) đi qua `fn.InvokeAudited(dest)` → 1 dòng nhật ký:
**tham số gửi** (IMPORT / CHANGING / TABLES, chụp TRƯỚC khi gọi — lời gọi lỗi vẫn có), **kết quả nhận** (EXPORT / CHANGING / TABLES)
dạng JSON, thời gian, trạng thái, và ngữ cảnh: `X-Correlation-Id` (hoặc `X-Request-Id`) EU gửi — không có thì trace id; `requestId`
(khóa idempotency), thao tác, posting, user, IP, URL, job. Mọi response API trả header `X-Correlation-Id` để EU lưu.

| Trạng thái | Ý nghĩa |
|---|---|
| `OK` / `WARNING` | Gọi xong; RETURN / BAPIRET2 / BDCMSGCOLL không có E / A (có W) |
| `ERROR` | SAP trả message E / A — `ErrorCode` (ID/NUMBER hoặc mã RDM như `CREDIT_CUSTOMER_NOT_FOUND`), `Message` |
| `EXCEPTION` | Lỗi RFC: ABAP exception / runtime error, mất kết nối, logon — chi tiết ở ResponseJson.exception |

**Không ảnh hưởng tốc độ:** luồng gọi SAP chỉ tạo JSON (bỏ field rỗng; bảng > `MaxTableRows` = 500 dòng chỉ ghi `rowCount` + 500 dòng
đầu, RFC_READ_TABLE 20 dòng) rồi đưa vào hàng đợi bộ nhớ. Bộ ghi nền gom lô (`BatchSize` 200 / `FlushIntervalMs` 1000) → 1 lần
`rdm.usp_SapCallLog_InsertBatch` (TVP); JSON nén `COMPRESS()` trong SQL. Đo trên máy dev: tạo JSON 0,03 ms (trang RFC_READ_TABLE),
0,09 ms (lô 50 bảng giá), 1,9 ms (bảng 500 dòng đủ); latency API tăng trong mức nhiễu. DB lỗi → lô ghi ra
`logs/sap-call-fallback/*.jsonl`, DB chạy lại tự nạp (bỏ qua CallId đã có); hàng đợi đầy (`QueueCapacity` 50.000) → bỏ + cảnh báo,
không chặn lời gọi SAP. Dừng ứng dụng → ghi nốt hàng đợi. Xóa nhật ký cũ hơn `RetentionDays` (180) lúc 02:30 hằng ngày.

| API | Quyền | Việc |
|---|---|---|
| `GET /api/admin/sap-calls?from&to&function&status&correlationId&requestId&operation&user&postingId&q&page` | Admin | Tìm (không JSON) |
| `GET /api/admin/sap-calls/{id}` | Admin | Chi tiết + JSON gửi / nhận |
| `GET /api/admin/sap-calls/summary`, `/export` (CSV), `/writer` (trạng thái bộ ghi) | Admin | Thống kê / xuất / theo dõi |
| `GET /api/reconciliation/sap-calls?from&to&requestId&correlationId` | Mọi user | Đối soát cho EU: lời gọi của chính tài khoản, kết quả + số chứng từ, không lộ tên hàm / JSON SAP |

Giao diện: tab **"Nhật ký SAP"** của [admin-ui](admin-ui/README.md). SSMS: `SELECT * FROM rdm.vw_SapCallLog` (JSON đã giải nén).

## Đồng bộ DB local → DB Dev (Rdm.DbSync)

Chép master data `md.*` + cấu hình API (`rdm.ApiEndpoints`: bật / tắt, ghi chú, mô tả đối tác) từ DB này sang DB khác, để môi
trường mới có dữ liệu ngay mà không phải đồng bộ lại từ SAP. Đọc bằng EF; **ghi DB đích chỉ qua procedure**
(`md.usp_*_Replace`, `rdm.usp_ApiEndpoint_Import`, `rdm.usp_ApiUser_Upsert`). DB đích chưa có schema → tự chạy EF migration.

Cấu hình `src/Rdm.DbSync/appsettings.json` (máy dev: `appsettings.Development.json`, không đóng gói) hoặc file `--config` / biến
môi trường `DbSync__TargetConnectionString`:

| Khóa | Ý nghĩa |
|---|---|
| `DbSync:SourceConnectionString` | DB nguồn (trống = `ConnectionStrings:SqlServer`) — máy dev: DB local trdm |
| `DbSync:TargetConnectionString` | DB đích (DB Dev). Lệnh `import`: trống = `ConnectionStrings:SqlServer` |
| `DbSync:Tables` | Mẫu bảng, mặc định `md.*`, `rdm.ApiEndpoints`, `rdm.ApiOutputProfiles`; thêm `rdm.ApiUsers` để chép tài khoản |
| `DbSync:Mode` | `IfEmpty` (mặc định): chỉ nạp bảng đích đang trống, không đè API đã chỉnh bên đích · `Replace`: thay toàn bộ |

```powershell
dotnet run --project src/Rdm.DbSync -- compare                         # số dòng từng bảng: nguồn vs đích
dotnet run --project src/Rdm.DbSync -- sync --dry-run                  # xem trước
dotnet run --project src/Rdm.DbSync -- sync                            # IfEmpty
dotnet run --project src/Rdm.DbSync -- sync --mode Replace --yes       # thay toàn bộ (không --yes: hỏi gõ tên DB đích)
dotnet run --project src/Rdm.DbSync -- export --out data\rdm-data.json.gz
dotnet run --project src/Rdm.DbSync -- import --in data\rdm-data.json.gz --target "<DB đích>"
```

**Nút bấm trên trang quản trị** (tab "Đồng bộ DB Dev" của [admin-ui](admin-ui/README.md)): khai báo trong cấu hình **Rdm.Api**
`DbSync:TargetConnectionString` (trống = tính năng tắt), tùy chọn `DbSync:Tables`, `DbSync:AllowMigrate` (mặc định `true`),
`DbSync:TimeoutMinutes` (15). Nguồn = DB của chính API (`DbSync:SourceConnectionString` để đổi). Trang hiện nguồn → đích, so sánh
số dòng từng bảng, "Xem trước" rồi "Đồng bộ"; "Thay toàn bộ" phải gõ đúng tên database đích. Mỗi lúc 1 lần chạy; lần chạy thật ghi log
Serilog (người bấm, số bảng). API (role Admin, chỉ ở Swagger nội bộ): `GET /api/admin/db-sync`, `GET /api/admin/db-sync/compare`,
`POST /api/admin/db-sync/run` `{ "mode": "IfEmpty|Replace", "dryRun": true, "migrate": false, "confirmTarget": "srdm" }`.

An toàn: nguồn = đích → từ chối; bảng nguồn 0 dòng → không xóa dữ liệu đích; `Replace` ghi đè dữ liệu đang có phải `--yes`;
snapshot kiểm tra số dòng + mức schema (migration) trước khi nạp. Mã thoát: 0 OK, 1 lỗi, 2 sai tham số / cấu hình, 3 cần `--yes`.
`Build-Package.ps1` tự xuất ảnh dữ liệu vào gói, `Deploy-Rdm.ps1` tự nạp ([deploy/README.md](deploy/README.md)).

## Cấu trúc

```
src/Rdm.Api             controllers, Swagger, Hangfire, exception handler
src/Rdm.Application     request DTO + validate, PostingService (idempotency), abstractions
src/Rdm.Sap             NCo: destination, Polly, SapClient, mapper, diễn giải RETURN
src/Rdm.Infrastructure  EF Core (schema rdm), repository, Serilog, job Hangfire dọn log, DbSync (đồng bộ DB)
src/Rdm.DbSync          CLI đồng bộ dữ liệu DB → DB / file snapshot (compare, sync, export, import)
tests/Rdm.Tests         xUnit + FluentAssertions
```

Log: Console, file `logs/rdm-*.log` cạnh binary, bảng `dbo.SerilogEvents` (job Hangfire `purge-serilog-events` xóa log cũ hơn `Jobs:LogRetentionDays`).
