# Z_ESD015_PRICE_UPLOAD — RFC upload bảng giá (từ ZCSD002_VN_PRICING_UPLD)

Chuyển report **ZCSD002_VN_PRICING_UPLD** (màn hình "Pricing Condition Upload Program", loại ZVLP / ZVDI / ZMIN / ZVIV)
thành function module **Remote-Enabled** để API gọi từ bên ngoài. Source: [Z_ESD015_PRICE_UPLOAD.abap](Z_ESD015_PRICE_UPLOAD.abap).

Đây là **đường ghi bảng giá duy nhất** của RDM API (mục 13 / ZRDM-004): tạo, xóa, sửa, duyệt. Đã chọn thay cho phương án bọc
`BAPI_PRICES_CONDITIONS` (Z_ESD016 — đã bỏ) vì đi đúng màn hình VK11/VK12 mà người dùng đang dùng hằng ngày; BAPI chưa được dùng ở
đâu trên DRF-930.

> **Trạng thái duyệt:** CALL TRANSACTION chạy batch input (`SY-BINPT = X`) nên enhancement `ZESD004_DEFAULT_STAT` không gán
> `KFRST = A`; `KBSTAT 01` → KFRST trống theo T686E → bảng giá tạo / sửa qua hàm này **có hiệu lực ngay** (giống report upload hiện tại).
> Việc duyệt làm trên RDM trước khi gọi. Hành động `R` chỉ dùng duyệt bảng giá chờ duyệt (KFRST = A) nhập tay trên SAP.

> `price_upload.md` là bản rút gọn (thiếu `F_POST`, `F_ASSIGN`…). Bản đầy đủ đọc trực tiếp trên DRF-930.
> Lưu ý trên DRF có 2 chương trình: `ZCSD001_PRICING_UPLD` (bản cũ 2019, loại ZLSP/ZDIS…) và
> `ZCSD002_VN_PRICING_UPLD` (bản VN 2020, đúng màn hình trong ảnh) — RFC này dựa trên bản VN.

## 1. Đối tượng cần tạo (ABAP team)

### 1.1 Structure SE11 `ZSSD_PRICE_UPLD`

| Field | Data element | Kiểu | Ghi chú |
|---|---|---|---|
| ROW_NO | INT4 | INT4 | Số dòng của client, trả lại ở `ET_RETURN-ROW` (trống = thứ tự dòng) |
| COND_TABLE | CHAR4 | CHAR 4 | A990 / A991 / A912 / A919 (thay cột "Option" của file Excel). ZVLP/ZVDI/ZVIV bỏ trống được |
| VKORG | VKORG | CHAR 4 | Sales org |
| VTWEG | VTWEG | CHAR 2 | Distribution channel |
| KUNAG | KUNAG | CHAR 10 | Sold-to |
| KUNWE | KUNWE | CHAR 10 | Ship-to |
| MATNR | MATNR | CHAR 18 | Material |
| INCO1 | INCO1 | CHAR 3 | Incoterms (A912) |
| VSBED | VSBED | CHAR 2 | Shipping condition |
| WERKS | WERKS_D | CHAR 4 | Plant |
| VKORGAU | VKORG_AUFT | CHAR 4 | Sales org của đơn hàng (A991 / ZVIV) |
| REGIO | REGIO | CHAR 3 | Region (A919, tùy chọn) |
| KBETR | BAPICURR_D | DEC 23,4 | Giá — số **thực** (VND: 1000000), không phải dạng lưu nội bộ |
| KONWA | KONWA | CUKY 5 | Đơn vị giá (VND) |
| KPEIN | KPEIN | DEC 5 | Pricing unit (report gốc đọc nhưng không dùng) |
| KMEIN | KMEIN | UNIT 3 | Đơn vị tính (mã nội bộ, vd. TO) |
| DATAB | KODATAB | DATS | Valid from |
| DATBI | KODATBI | DATS | Valid to (U: hiệu lực đến mới, trống = giữ nguyên) |
| KNUMH | KNUMH | CHAR 10 | **Mới** — số condition record cho U / R (C / D bỏ trống) |

KBETR dùng `BAPICURR_D` (DEC) thay vì `KBETR` (CURR) để RFC không bị lệch số lẻ tiền tệ (VND có 0 số lẻ).

### 1.2 Function module SE37

- Tên: `Z_ESD015_PRICE_UPLOAD`, **Processing type: Remote-Enabled Module**.
- Function group: khuyến nghị tạo group mới `ZISD_FG_PRICE` (include forms `LZISD_FG_PRICEF01`) để không ảnh hưởng
  `ZISD_FG_RDM` đang chứa Z_ESD012/013/014. Nếu đặt chung group thì thêm `INCLUDE LZISD_FG_RDMF03.` vào `SAPLZISD_FG_RDM`.
  Các FORM đều có tiền tố `F_PRC_` nên không trùng tên `F_BDC_DYNPRO` / `F_BDC_FIELD` hiện có.
- Interface:

| Loại | Tham số | Kiểu | Ý nghĩa |
|---|---|---|---|
| Import | IV_KSCHL | KSCHL (**optional**) | ZVLP / ZVDI / ZMIN / ZVIV — bắt buộc với C / D; U / R lấy từ KONH |
| Import | IV_ACTION | CHAR1 (default `C`) | `C` tạo (VK11) · `D` đánh dấu xóa (VK12) · `U` sửa (VK12) · `R` duyệt (VK12) |
| Export | EV_SUCCESS | INT4 | Số dòng thành công |
| Export | EV_ERROR | INT4 | Số dòng lỗi |
| Tables | IT_PRICE | ZSSD_PRICE_UPLD | Dữ liệu (thay file Excel) |
| Tables | ET_RETURN | BAPIRET2 | Message từng dòng; `ROW` = ROW_NO, `ROW = 0` = lỗi chung |

- User RFC cần quyền S_TCODE VK11 / VK12 và quyền tạo condition record (V_KONH_VKS…).

## 2. Loại điều kiện → condition table (cấu hình DRF-930)

| Loại | Access seq. | Bảng | Key bắt buộc | Key combination VK11 |
|---|---|---|---|---|
| ZVLP | ZVPR | A990 | VKORG, VTWEG, KUNAG, KUNWE, MATNR, VSBED, WERKS | (01) |
| ZVDI | ZVD1 | A990 | như trên | (01) |
| ZVIV | ZPI1 | A991 | VKORGAU, WERKS, MATNR | (01) |
| ZMIN | ZMIN | A912 | VKORG, VTWEG, MATNR (+ INCO1) | (01) |
| ZMIN | ZMIN | A919 | VKORG, VTWEG, MATNR (+ REGIO) | (02) |

Tạo: thêm bắt buộc KONWA, KMEIN, DATAB, DATBI (DATBI ≥ DATAB). Xóa: DATAB = ngày hiệu lực của record cần xóa.

**U (sửa)** — dòng IT_PRICE: `KNUMH` + field cần đổi (`KBETR`, `KONWA`, `KPEIN`, `KMEIN`, `DATBI`; trống = giữ nguyên).
Hàm đọc KONH + Axxx để lấy key và kỳ hiệu lực, vào VK12 (màn hình chọn như ZRSD004), sửa dòng (01) rồi lưu. Không đổi được
hiệu lực từ (DATAB). Record bị SAP tách nhiều kỳ: truyền `DATAB` = ngày bắt đầu kỳ cần sửa hiệu lực đến (giá dùng chung KONP
nên đổi giá áp cho cả record).

**R (duyệt)** — dòng IT_PRICE: `KNUMH`. Mỗi kỳ Axxx còn `KFRST = A`: VK12 chọn key + release status A + ngày, đặt
`KOMG-KBSTAT(01) = 02` (chuỗi màn hình của **ZRSD004_VN_PRICEMASTER** / ZSD_VN_PRICE_APR). Kiểm tra lại trước mỗi kỳ — VK12 duyệt
theo condition record nên kỳ sau có thể đã được duyệt cùng lúc. KNUMH không còn chờ duyệt → E "is not pending approval".

## 3. Thay đổi / sửa lỗi so với report gốc

1. **Bỏ phần chỉ chạy với SAP GUI** (không chạy được qua RFC): `ALSM_EXCEL_TO_INTERNAL_TABLE` (đọc Excel qua OLE trên máy người dùng),
   `KD_GET_FILENAME_ON_F4`, `POPUP_TO_CONFIRM`, ALV. Dữ liệu vào qua `IT_PRICE`, kết quả ra `ET_RETURN`.
2. **Định dạng số / ngày theo user**: report gốc đưa chuỗi Excel nguyên văn vào màn hình. User RFC `TUANNA` dùng dấu phẩy thập phân
   (USR01-DCPFM trống → `1.234.567,89`) nên `1234567.89` sẽ bị hiểu sai. RFC nhận số / DATS và `WRITE` theo định dạng của user.
3. **Xóa ZVIV (A991) bị lỗi trong report gốc**: luôn điền `F001 = VKORG` (trống với A991) và không điền `WERKS`, trong khi
   `RV13A991` có `F001 = VKORGAU`, `F002 = WERKS` bắt buộc. Đã bổ sung nhánh A991.
4. **Kiểm tra trước khi gọi BDC**: loại điều kiện ↔ bảng hợp lệ, key bắt buộc, ngày. Report gốc chạy BDC với dữ liệu thiếu → lỗi màn hình khó hiểu.
5. **KPEIN** được đưa vào `KONP-KPEIN(01)` khi có giá trị (report gốc bỏ qua).
6. **Kiểm tra quyền S_TCODE** VK11 / VK12 (CALL TRANSACTION trên 7.02 không tự kiểm tra).
7. **Kết quả đầy đủ**: mọi message (S/W/I/E/A) trả về `ET_RETURN`; dòng lỗi khi có E/A **hoặc** `sy-subrc <> 0` không kèm message
   (report gốc chỉ xét S/E). `DEFSIZE = 'X'` để vị trí dòng (01) của màn hình fast entry ổn định khi chạy nền.
8. Bỏ code chết của report gốc: nhánh `A912` lặp 2 lần, các bảng A910/A911/A913/A914/A918/A921/A922/A923 không thuộc 4 loại điều kiện trên.

## 4. Cần kiểm tra khi test trên DEV

- `KONP-KPEIN(01)` có trên màn hình 1990/1991/1912/1919 (nếu không có, BDC báo field không tồn tại → bỏ dòng KPEIN).
- Xóa (VK12) đánh dấu xóa **dòng đầu tiên** của danh sách record có hiệu lực tại DATAB — giống report gốc. Với A912 nếu bỏ trống
  INCO1 thì có thể khớp nhiều record.
- **U**: sửa giá 1 record ZVLP đã duyệt → KONP-KBETR đổi, KFRST vẫn trống; sửa DATBI → A990 đổi hiệu lực đến.
  Kiểm tra field `RV13A-DATBI(01)` / `KONP-KBETR(01)` cho sửa được trên màn hình danh sách VK12 khi chạy nền.
- **R**: tạo tay 1 record ZVLP bằng VK11 (KFRST = A) → gọi R → KFRST trống; record bị tách nhiều kỳ không báo lỗi giả.
- CALL TRANSACTION mode `N` không treo RFC: màn hình thiếu dữ liệu → `sy-subrc` ≠ 0 + message trong ET_RETURN.
- API .NET đã có: `/api/sd/prices` (tạo), `/change` (U), `/approve` (R), `/copy-with-vat` (C) — kiểm tra trùng / chồng hiệu lực
  làm ở API trước khi gọi hàm này.
