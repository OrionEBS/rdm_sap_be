# Triển khai RDM SAP Integration — Windows Server 2019 + SQL Server 2025

| Thành phần | Chạy ở | Việc |
|---|---|---|
| **Rdm.Api** | IIS site `RdmApi`, app pool `RdmApi` (No Managed Code, x64) | REST API + Swagger + `/health`; chỉ **đưa** job vào hàng đợi (`Hangfire__ServerEnabled=false`) |
| **Rdm.Worker** | Windows Service `RdmWorker` (virtual account `NT SERVICE\RdmWorker`) | Hangfire server: job bảng giá hàng loạt `/api/sd/prices-bapi/jobs`, đồng bộ master data (01:00), dọn log (02:00) |
| SQL Server 2025 | máy chủ SQL | DB `trdm`: schema `rdm`, `md`, `hangfire`, `dbo.SerilogEvents` |

Tách worker khỏi IIS: IIS recycle / idle / cập nhật API **không cắt ngang** job khai báo giá hàng loạt.

## Bố cục trên máy chủ (mặc định `D:\Apps\Rdm`)

```
D:\Apps\Rdm\
  api\        Rdm.Api (IIS physical path) + logs\
  worker\     Rdm.Worker.exe (service) + logs\
  config\appsettings.Production.json   ← cấu hình DÙNG CHUNG: SQL, SAP, JWT, admin (chỉ Administrators + 2 tài khoản chạy được đọc)
  backups\yyyyMMdd-HHmmss\{api,worker}  ← bản trước mỗi lần cập nhật
```

Ứng dụng đọc cấu hình chung qua biến môi trường `RDM_CONFIG_FILE` (web.config / registry của service) — **không** để mật khẩu
trong thư mục ứng dụng hay trong gói.

## 1. Đóng gói (máy build có mã nguồn + .NET 8 SDK)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\Build-Package.ps1            # test + publish + zip
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\Build-Package.ps1 -SkipTests
```

Kết quả: `artifacts\Rdm_<yyyy.MM.dd.HHmm>.zip` (+ `.sha256`) gồm `api\`, `worker\` (đủ SAP NCo 3.1: sapnco.dll, sapnco_utils.dll,
cpc4n.dll, ijwhost.dll), `deploy\` (script), `config\appsettings.Production.example.json`, `manifest.json`.
`appsettings.Development.json` (mật khẩu DEV) **luôn bị loại** khỏi gói.

## 2. Chuẩn bị máy chủ (lần đầu)

1. Windows Server 2019, PowerShell 64-bit chạy **Run as Administrator**.
2. Tải sẵn (máy chủ không có internet thì copy vào, VD `D:\Setup`):
   - **.NET 8 Hosting Bundle** `dotnet-hosting-8.0.x-win.exe` — https://dotnet.microsoft.com/download/dotnet/8.0
     (cài **sau** IIS; script tự cài IIS trước rồi mới cài bundle)
   - **Visual C++ 2015-2022 x64** `vc_redist.x64.exe` — https://aka.ms/vs/17/release/vc_redist.x64.exe (SAP NCo cần)
3. SQL Server 2025:
   - Tạo DB `trdm` + login (VD `rdmuser`). Lần đầu login cần quyền tạo bảng / schema (`db_owner`) vì API chạy EF migration
     và Hangfire tự tạo schema `hangfire`, Serilog tự tạo `dbo.SerilogEvents`.
   - SQL Server 2025 + Microsoft.Data.SqlClient mặc định **mã hóa kết nối**: dùng `Encrypt=True;TrustServerCertificate=True`
     (chứng chỉ tự ký) hoặc cài chứng chỉ hợp lệ cho SQL rồi bỏ `TrustServerCertificate`.
4. Mở firewall: máy chủ ứng dụng → SQL Server (1433 / cổng instance), → SAP app server `33<SYSNR>` (VD 3301);
   client → cổng API (mặc định 8080).
5. User RFC SAP (PRD): quyền V_KONH_VKO, V_KONH_VKS (ACTVT 01, 02) — xem `abap\Z_ESD016_PRICES_CONDITIONS.md`.

## 3. Triển khai lần đầu

```powershell
Expand-Archive Rdm_2026.10.23.1430.zip -DestinationPath D:\Setup\Rdm_2026.10.23.1430
cd D:\Setup\Rdm_2026.10.23.1430\deploy
.\Deploy-Rdm.ps1 -InstallPrerequisites -HostingBundlePath D:\Setup\dotnet-hosting-8.0.x-win.exe -VcRedistPath D:\Setup\vc_redist.x64.exe
```

Lần chạy đầu script **tạo** `D:\Apps\Rdm\config\appsettings.Production.json` từ mẫu (khóa JWT sinh ngẫu nhiên, file đã khóa quyền)
rồi **dừng** (exit 2). Mở bằng Notepad (Administrator), thay mọi giá trị `<...>` (SQL, SAP, mật khẩu admin), chạy lại lệnh trên.

Script kiểm tra: tính năng IIS, Hosting Bundle, VC++, đủ file NCo, cấu hình, kết nối SQL (in phiên bản SQL Server), cổng SAP;
rồi tạo app pool / site / service, phân quyền, khởi động API (migration) → `/health` → worker → heartbeat Hangfire.

Tham số hay dùng:

| Tham số | Mặc định | Ý nghĩa |
|---|---|---|
| `-InstallRoot` | `D:\Apps\Rdm` | thư mục cài |
| `-HttpPort` / `-HostName` | `8080` / trống | binding HTTP |
| `-HttpsPort` + `-CertThumbprint` | — | binding HTTPS (chứng chỉ trong LocalMachine\My) |
| `-ServiceCredential (Get-Credential)` | virtual account | chạy worker bằng tài khoản domain / gMSA |
| `-HostingModel` | `OutOfProcess` | NCo (native) chạy trong `Rdm.Api.exe` tách khỏi `w3wp` — lỗi native không làm sập IIS |
| `-Force` | — | triển khai dù còn job hàng loạt đang chạy (job tạo / sửa bị cắt ngang → `Unknown`) |
| `-AutoRollback` | — | lỗi health check → tự quay về bản trước |

## 4. Cập nhật bản mới

```powershell
cd D:\Setup\Rdm_<phiên bản mới>\deploy
.\Deploy-Rdm.ps1
```

- **Chặn** nếu còn job hàng loạt đang chạy (`rdm.SapPostings` Pending) — chờ job xong (`GET /api/sd/prices-bapi/jobs/...`).
  Không triển khai trong đợt khai báo giá đầu năm.
- API trả 503 (app_offline) trong lúc cập nhật; worker dừng sau khi job đang chạy kết thúc (tối đa 120 giây).
- Bản cũ sao lưu vào `backups\`, giữ 5 bản (`-KeepBackups`). Cấu hình và log không bị ghi đè.

## 5. Rollback / gỡ cài đặt

```powershell
.\Rollback-Rdm.ps1 -List
.\Rollback-Rdm.ps1                         # bản mới nhất
.\Rollback-Rdm.ps1 -Backup 20261023-0915
.\Uninstall-Rdm.ps1 [-RemoveFiles]         # giữ config, backups, CSDL
```

Rollback không hoàn tác CSDL: migration của dự án chỉ thêm cột / procedure nên bản cũ vẫn chạy trên schema mới.

## 6. Vận hành

| Việc | Cách |
|---|---|
| Kiểm tra sống | `http://<server>:8080/health` → `{"status":"Healthy","database":true,"version":"..."}` |
| Log | `D:\Apps\Rdm\api\logs`, `D:\Apps\Rdm\worker\logs` (rdm-YYYYMMDD.log, giữ 30 ngày); bảng `dbo.SerilogEvents` (cột `Application` = Rdm.Api / Rdm.Worker) |
| Lỗi RFC SAP | `dev_nco_rfc.log` trong `api\` / `worker\` |
| Job nền | Hangfire dashboard `http://localhost:8080/hangfire` (chỉ mở trên chính máy chủ); job bảng giá: `GET /api/sd/prices-bapi/jobs/{operation}/{requestId}` |
| Khởi động lại worker | `Restart-Service RdmWorker` (chờ job đang chạy xong) |
| Đổi cấu hình | sửa `config\appsettings.Production.json` → `Restart-WebAppPool RdmApi; Restart-Service RdmWorker` |
| App pool recycle | 03:00 hằng ngày (không còn recycle 29 giờ), không idle timeout |
