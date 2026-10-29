# Z_ESD016_PRICES_CONDITIONS — vỏ RFC cho BAPI_PRICES_CONDITIONS

API `/api/sd/prices-bapi` (tạo / sửa / hủy hiệu lực / duyệt / gỡ duyệt bảng giá ZVLP, ZVDI, ZMIN, ZVIV) dùng BAPI chuẩn
**BAPI_PRICES_CONDITIONS**. Trên DRF-930 BAPI này **không remote-enabled** (TFDIR-FMODE trống, function group
`CND_PRICES_INBOUND`) nên NCo không gọi trực tiếp được → cần 1 function module RFC bọc. Source:
[Z_ESD016_PRICES_CONDITIONS.abap](Z_ESD016_PRICES_CONDITIONS.abap).

Chưa có hàm này: các API ghi trả `PRICE_RFC_MISSING` (kiểm tra dữ liệu / trùng hiệu lực vẫn chạy, không ghi SAP).

## Tạo trên SAP (ABAP team)

- SE37: `Z_ESD016_PRICES_CONDITIONS`, **Processing type: Remote-Enabled Module**.
- Function group: `ZFG_SD_RDM` (group RDM, đã có `ZFM_SD_RDM_GET_PRICELIST`).
- Interface (giống BAPI, thêm `IV_TESTRUN` / `EV_COMMITTED`):

| Loại | Tham số | Kiểu |
|---|---|---|
| Import (optional) | PI_INITIALMODE | CHAR1 |
| Import (optional) | PI_BLOCKNUMBER | CHAR10 |
| Import (optional) | PI_PHYSICAL_DELETION | CHAR1 |
| Import (optional) | IV_TESTRUN | CHAR1 — `X` = rollback (chạy thử) |
| Export | EV_COMMITTED | CHAR1 — `X` = đã commit |
| Tables | TI_BAPICONDCT | BAPICONDCT |
| Tables | TI_BAPICONDHD | BAPICONDHD |
| Tables | TI_BAPICONDIT | BAPICONDIT |
| Tables (optional) | TI_BAPICONDQS | BAPICONDQS |
| Tables (optional) | TI_BAPICONDVS | BAPICONDVS |
| Tables | TO_BAPIRET2 | BAPIRET2 |
| Tables | TO_BAPIKNUMHS | BAPIKNUMHS |
| Tables (optional) | TO_MEM_INITIAL | CND_MEM_INITIAL |

### Khai báo từng tab trong SE37 (bắt buộc làm TRƯỚC khi dán source)

Khối `*"Local Interface:` trong source chỉ là **comment SAP tự sinh** từ các tab — dán vào tab Source code không tạo tham số.
Thiếu tham số → lỗi kiểu *Field "EV_COMMITTED" is unknown*.

**Attributes**: Processing Type = **Remote-Enabled Module** (tham số Import/Export của hàm RFC phải tick **Pass Value**).

**Import** (cả 4 tick Optional + Pass Value):

| Parameter Name | Typing | Associated Type | Optional | Pass Value |
|---|---|---|---|---|
| PI_INITIALMODE | TYPE | CHAR1 | ✓ | ✓ |
| PI_BLOCKNUMBER | TYPE | CHAR10 | ✓ | ✓ |
| PI_PHYSICAL_DELETION | TYPE | CHAR1 | ✓ | ✓ |
| IV_TESTRUN | TYPE | CHAR1 | ✓ | ✓ |

**Export**:

| Parameter Name | Typing | Associated Type | Pass Value |
|---|---|---|---|
| EV_COMMITTED | TYPE | CHAR1 | ✓ |

**Tables** (Typing = LIKE / STRUCTURE):

| Parameter Name | Typing | Associated Type | Optional |
|---|---|---|---|
| TI_BAPICONDCT | LIKE | BAPICONDCT | |
| TI_BAPICONDHD | LIKE | BAPICONDHD | |
| TI_BAPICONDIT | LIKE | BAPICONDIT | |
| TI_BAPICONDQS | LIKE | BAPICONDQS | ✓ |
| TI_BAPICONDVS | LIKE | BAPICONDVS | ✓ |
| TO_BAPIRET2 | LIKE | BAPIRET2 | |
| TO_BAPIKNUMHS | LIKE | BAPIKNUMHS | |
| TO_MEM_INITIAL | LIKE | CND_MEM_INITIAL | ✓ |

**Bổ sung cho v3 — tab Import** (thêm 1 dòng):

| Parameter Name | Typing | Associated Type | Default value | Optional | Pass Value |
|---|---|---|---|---|---|
| IV_DO_COMMIT | TYPE | CHAR1 | 'X' | ✓ | ✓ |

Sau đó dán phần thân từ **`Z_ESD016_PRICES_CONDITIONS_v4.abap`** (từ `CONSTANTS: lc_varkey_max` đến trước `ENDFUNCTION.`) → Check → Activate.
Interface v4 giống v3 (đã khai báo `IV_DO_COMMIT` thì không cần sửa tab nào). Source v4 đã qua `SYNTAX-CHECK` trên DRF-930
(SAP_BASIS 702 SP16) bằng `/BODS/SYNTAX_CHECK` (kiểm tra trong bộ nhớ, không tạo object) — kể cả tên trường A990 / A991 / A912.

## Phiên bản

| Bản | Trạng thái | Ghi chú |
|---|---|---|
| `Z_ESD016_PRICES_CONDITIONS.abap` (v1) | thay thế | SQL động cho master data, chỉ kiểm tra S_TCODE, không kiểm tra KNUMH khớp khóa |
| `Z_ESD016_PRICES_CONDITIONS_v2.abap` | **không dùng** | không activate được trên 702 (`@DATA`); `strlen( VARKEY )` chặn mọi bảng giá đã duyệt; mất kiểm tra chồng hiệu lực; V_KONH_VKO không có KSCHL |
| `Z_ESD016_PRICES_CONDITIONS_v3.abap` | đang chạy trên DRF-930 (23/10/2026) | lọt `IV_DO_COMMIT` trống (KNUMH 0000119706); chặn nhầm khi cùng lần gọi rút ngắn bản cũ + tạo bản mới (test V24); khóa dạng hiển thị báo "không tồn tại" |
| `Z_ESD016_PRICES_CONDITIONS_v4.abap` | **bản đề xuất** | sửa các điểm trên + SELECT tĩnh cho bước chồng hiệu lực + cache master data + giới hạn 1000 dòng — chi tiết ở header source |

## Logic v4 (kiểm tra tuần tự, thận trọng)

BAPI_PRICES_CONDITIONS là cổng nhập hàng loạt (CND_PRICES_INBOUND) — **không kiểm tra master data / chồng hiệu lực**
(DRF-930 đã tạo được bảng giá với đơn vị `ZZZ`). Vỏ RFC kiểm tra lần lượt, **bước nào có lỗi thì dừng, không gọi BAPI**:

| Bước | Kiểm tra | Mã lỗi (`TO_BAPIRET2-PARAMETER`, ID ZRDM) |
|---|---|---|
| 0 | ≤ 1000 dòng / lần gọi; `IV_DO_COMMIT` trống (không chạy thử) bị chặn; OPERATION 009 / 004 / 003; chỉ ZVLP, ZVDI (A990), ZVIV (A991), ZMIN (A912); hiệu lực từ ≤ đến; 009 dùng số tạm `$…`, 004 / 003 dùng KNUMH thật; dòng HD / IT phải có dòng CT cùng COND_NO | `PRICE_INVALID_STRUCTURE` 012, `PRICE_NOT_SUPPORTED` 008, `PRICE_INVALID_PERIOD` 009 |
| 1 | Giải mã VARKEY theo T681E + DDIC; trường thiếu trong DDIC, khóa > 100 ký tự hoặc độ dài khóa ngoài 1..100 → lỗi | `PRICE_INVALID_TABLE` 006 |
| 2 | Khóa phải ở **dạng nội bộ**: MATNR (MATN1, VD `000000000000100026`), KUNAG / KUNWE (ALPHA, VD `0000100166`). Sai → trả giá trị đúng, **không tự đổi** (BAPI ghi nguyên VARKEY) | `PRICE_INVALID_KEY_FORMAT` 014 |
| 3 | Quyền **mọi dòng** (kể cả hủy / xóa kỳ): `V_KONH_VKO` (VKORG, VTWEG hoặc DUMMY, SPART DUMMY, ACTVT) + `V_KONH_VKS` (KSCHL, ACTVT); ACTVT 009 → 01, 004 / 003 → 02 | `PRICE_NO_AUTHORITY` 001 |
| 4 | 004 / 003: KNUMH có trong KONH, đúng KVEWE / KAPPL / KSCHL / KOTABNR và phần khóa VAKEY (trước KFRST) | `PRICE_NOT_FOUND` 011, `PRICE_KEY_MISMATCH` 010 |
| 5 | Master data (SELECT tĩnh, **cache theo giá trị**): TVKO, TVTW, TVKOV, T001W, TVSB, TINC, T005S (VN), MARA (chưa xóa), KNA1 (chưa xóa), T006, TCURC; KFRST chỉ trống / A | `PRICE_INVALID_MASTER_DATA` 002, `PRICE_INVALID_RELEASE` 003 |
| 6 | Chồng hiệu lực với bảng giá có sẵn — **SELECT tĩnh từng bảng** A990 / A991 / A912 (không SQL động). Bỏ: bản đã đánh dấu xóa, chính KNUMH, bản đang hủy, **kỳ đang bị thay / xóa trong cùng lần gọi** | `PRICE_OVERLAP` 004 |
| 7 | Trùng khóa (phần trước KFRST) + chồng hiệu lực giữa các dòng còn hiệu lực trong cùng lần gọi | `PRICE_DUPLICATE_IN_REQUEST` 005 |
| 8 | Gọi BAPI → E/A hoặc `IV_TESTRUN = X` → ROLLBACK; ngược lại `BAPI_TRANSACTION_COMMIT WAIT = X`, `EV_COMMITTED = X` **chỉ khi đã commit thật** | message SAP giữ nguyên ID / NUMBER |

- Dòng hủy hiệu lực (`INDIDELETE = X`) và dòng 003: vẫn qua bước 0–4 (cấu trúc, định dạng, quyền, khớp khóa), bỏ qua 5–7 → luôn hủy được
  bảng giá cũ dù vật tư / khách hàng đã bị xóa hoặc record đang chồng hiệu lực.
- `IV_DO_COMMIT` trống (không chạy thử) → **bị chặn ở bước 0** (`PRICE_INVALID_STRUCTURE`). Kiểm chứng DRF-930 (22/10/2026):
  BAPI ghi thẳng KONH / KONP / Axxx, kết thúc phiên RFC stateless tự COMMIT DB → bảng giá vẫn được ghi (KNUMH 0000119706) trong
  khi `EV_COMMITTED` trống — người gọi tưởng thất bại và gửi lại sẽ tạo trùng. Tham số giữ lại chỉ để không phải đổi interface;
  API RDM luôn truyền `X`.
- `TRY … CATCH cx_root`: lỗi bất ngờ → rollback, `PRICE_UNEXPECTED_ERROR` (ZRDM/099) với message chung — **không** trả text exception;
  `MESSAGE_V1` = lớp exception, `MESSAGE_V2` / `V3` = include / dòng phát sinh để tra cứu.
- `MESSAGE E` từ hàm con (BAPI) được bắt bằng `EXCEPTIONS error_message`; v4 gọi tĩnh CONVERSION_EXIT_MATN1 / ALPHA_INPUT với `EXCEPTIONS error_message` (chỉ để
  phát hiện khóa sai định dạng, không đổi VARKEY).

### Quyền cho user RFC (QAS / PRD)

DRF-930: `TUANNA` có SAP_ALL. User RFC thật cần role có:

| Object | Trường | Giá trị |
|---|---|---|
| V_KONH_VKO | VKORG | G210 (sales org được ghi bảng giá) |
| | VTWEG | 10 |
| | SPART | * (không có trong khóa — kiểm tra DUMMY) |
| | ACTVT | 01, 02 (DRF-930 chỉ định nghĩa 01 / 02 / 03 — không có 06) |
| V_KONH_VKS | KSCHL | ZVLP, ZVDI, ZMIN, ZVIV |
| | ACTVT | 01, 02 |

## API map sang BAPI như sau (đã kiểm chứng trên DRF-930)

| API | OPERATION | Ghi chú |
|---|---|---|
| Tạo | 009 | `COND_NO = $000000001` (số tạm), số thật ở `TO_BAPIKNUMHS-COND_NO_NEW`; KFRST = `A` (ký tự cuối VARKEY) khi `pendingApproval = true` |
| Sửa giá / đơn vị | 004 | gửi đủ giá trị KONP hiện tại + giá trị mới, giữ KFRST hiện tại trong VARKEY; mọi kỳ hiệu lực của KNUMH |
| Sửa hiệu lực đến | 004 kỳ mới **trước**, rồi 003 kỳ cũ | cùng 1 lần gọi, `PI_PHYSICAL_DELETION = X` (003 đứng trước sẽ để lại cờ LOEVM_KO) |
| Hủy hiệu lực | 004 | `INDIDELETE = X` (đánh dấu xóa như VK12 — không xóa vật lý) |
| Duyệt / gỡ duyệt | 004 | KFRST mới ở **ký tự cuối VARKEY** (A → trống / trống → A). `BAPICONDIT-STATUS` chỉ ghi KONP, không đổi KFRST |

`COND_VALUE` gửi số thực (VND: 1500000), BAPI tự đổi sang dạng lưu nội bộ theo TCURX.

## Kiểm thử sau khi cập nhật source

1. Tạo với đơn vị `ZZZ` → `PRICE_INVALID_MASTER_DATA`, không có KNUMH mới (SE16 KONH).
2. Tạo trùng khóa, chồng hiệu lực với bản có sẵn → `PRICE_OVERLAP`.
3. Gửi 2 dòng cùng khóa chồng hiệu lực trong 1 lần gọi (SE37 test) → `PRICE_DUPLICATE_IN_REQUEST`.
4. Sửa hiệu lực đến của chính record (004 + 003 cùng KNUMH) → không bị báo chồng với chính nó.
5. User thiếu quyền V_KONH_VKO / V_KONH_VKS (v1: S_TCODE VK11 / VK12) → `PRICE_NO_AUTHORITY`.
6. (v3) 004 với KNUMH của ZVLP nhưng VARKEY của khách hàng khác → `PRICE_KEY_MISMATCH`, record không đổi.
7. (v3) ZMIN theo bảng 919 hoặc loại điều kiện khác (VD PR00) → `PRICE_NOT_SUPPORTED`.
8. (v3) Hủy hiệu lực record có vật tư đã đánh dấu xóa → vẫn hủy được.
9. (v3) Tạo bảng giá **đã duyệt** (KFRST trống ở cuối VARKEY) → thành công (lỗi `strlen` của v2 đã sửa).
10. Sau khi activate v3: chạy lại `tests\integration\PriceBapi.DevTests.ps1` (152 kiểm tra) để xác nhận không hồi quy.
11. (v4) MATNR `100026` / KUNAG `100166` dạng hiển thị → `PRICE_INVALID_KEY_FORMAT` kèm giá trị nội bộ đúng (v3: "không tồn tại").
12. (v4) Cùng lần gọi: rút ngắn bảng giá cũ (004 kỳ mới + 003 kỳ cũ) và tạo bản mới vào khoảng trống → hợp lệ (v3 chặn nhầm `PRICE_OVERLAP`);
    bản mới lấn vào kỳ còn lại → `PRICE_DUPLICATE_IN_REQUEST`.
13. (v4) 1001 dòng / lần gọi → `PRICE_INVALID_STRUCTURE`.
