# RDM Admin UI — quản trị bật / tắt API

Project FE tách riêng (React 18 + TypeScript + Vite) cho bảng cấu hình `rdm.ApiEndpoints` của RDM SAP Integration API.
API bị tắt: không hiện trên Swagger và trả 404 như URL không tồn tại.

## Chức năng

- Đăng nhập bằng tài khoản **Admin** của RDM API (`POST /api/auth/token`); token lưu `sessionStorage`, tự đăng xuất khi hết hạn (60 phút).
- Thẻ tổng hợp: tổng API / đang bật / đang tắt / khóa / không còn trong code (bấm để lọc).
- Danh sách theo nhóm (controller): method, route, mô tả (từ tài liệu Swagger), công tắc bật / tắt, ghi chú, người sửa, thời điểm.
- Tìm kiếm (route, mô tả, nhóm, ghi chú), lọc theo nhóm / trạng thái.
- Bật / tắt 1 API, cả nhóm, hoặc nhiều API đã chọn — luôn có hộp xác nhận liệt kê API bị ảnh hưởng + ghi chú lý do.
- API khóa (đăng nhập, quản trị) hiện "🔒 Khóa", không tắt được; nhóm chỉ gồm API khóa nằm cuối, thu gọn sẵn.
- "Nạp lại cache": API nạp lại bảng ngay (sau khi DBA sửa bằng procedure trên DB).
- **Mô tả đối tác / nội bộ:** mỗi dòng hiện "Đối tác:" (hiện trên Swagger `/swagger`) và "Nội bộ:" (mô tả kỹ thuật từ code, chỉ
  Swagger nội bộ + tài liệu TS). Nút "✎ Sửa mô tả đối tác": soạn tóm tắt / mô tả, cảnh báo khi có thuật ngữ SAP (BAPI, RFC, bảng
  SAP...; bỏ qua đường dẫn `/api/...`), tùy chọn "Ẩn API này khỏi tài liệu đối tác". Thẻ / bộ lọc "Chưa có mô tả đối tác", "Ẩn với đối tác".
- Thanh tài liệu: mở Swagger đối tác, tải tài liệu TS nội bộ (.md), tải OpenAPI nội bộ (.json).
- **Tab "Nhật ký SAP"** (`#sapcalls`): tình trạng bộ ghi (đã ghi / chờ / bị bỏ / file dự phòng), lọc theo X-Correlation-Id, requestId,
  hàm SAP, trạng thái (lỗi SAP / lỗi RFC / cảnh báo), khoảng thời gian; bấm 1 dòng → tham số gửi / kết quả nhận JSON (sao chép, tải về).
- **Tab "Đồng bộ DB Dev"** (`#dbsync`): nguồn → đích, so sánh số dòng từng bảng, chọn "Chỉ bảng còn trống" (an toàn) / "Thay toàn bộ"
  (gõ tên database đích để xác nhận), tùy chọn tạo / cập nhật schema DB đích; "1. Xem trước" rồi "2. Đồng bộ sang DB Dev", hiện kết quả
  từng bảng + lần đồng bộ gần nhất. Cần `DbSync:TargetConnectionString` trên RDM API (trống → trang báo chưa cấu hình).

API dùng: `GET /api/admin/endpoints`, `GET /api/admin/endpoints/groups`, `PUT /api/admin/endpoints/status`,
`POST /api/admin/endpoints/refresh`, `PUT /api/admin/endpoints/doc`, `GET /api/admin/endpoints/export/ts`,
`GET /api/admin/endpoints/export/openapi-internal` (role Admin).

## Chạy dev

```powershell
cd admin-ui
npm install
npm run dev          # http://localhost:5173 — proxy /api → http://localhost:5200 (đổi: VITE_API_PROXY trong .env)
```

## Build + deploy (IIS)

```powershell
npm run build        # → dist\ (index.html, config.js, web.config, assets\)
```

1. IIS: tạo site tĩnh (VD `RdmAdmin`, cổng 8090 / HTTPS), physical path = thư mục chứa nội dung `dist\`.
   App pool "No Managed Code". `web.config` kèm sẵn: không cache `index.html` + `config.js`, cache lâu `assets\*`.
2. Sửa **`config.js`** trên máy chủ (không cần build lại):
   ```js
   window.RDM_CONFIG = { apiBaseUrl: 'https://rdm-api.tafico.local:8443' };
   ```
   Trống = gọi API cùng origin (FE và API sau cùng reverse proxy / domain).
3. Khai báo origin của trang admin trong cấu hình RDM API (file `config\appsettings.Production.json` dùng chung), rồi khởi động lại API:
   ```json
   "Cors": { "AllowedOrigins": [ "https://rdm-admin.tafico.local:8090" ] }
   ```

Không tải gì từ Internet lúc chạy (không CDN, không font ngoài) — dùng được trong mạng nội bộ.

## Kiểm thử đã chạy (DRF-930 / DB trdm, Chrome headless)

Đăng nhập sai / đúng; tìm "jobs/create" → tắt → `POST /api/sd/prices-bapi/jobs/create` trả 404, biến khỏi Swagger, dòng ghi
chú + người sửa; tắt cả nhóm MasterData (40 API) → `GET /api/master-data/cost-centers` 404; lọc "Đang tắt" → chọn hết → bật lại
→ 0 API inactive; giao diện điện thoại 390 px.
