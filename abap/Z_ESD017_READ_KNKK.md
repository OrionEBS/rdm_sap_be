# Z_ESD017_READ_KNKK — đọc hạn mức tín dụng khách hàng (KNKK)

API `GET /api/customer-credits` và `GET /api/customer-credits/{customer}?creditControlArea=...` đọc KNKK. Trước đây đọc thẳng
bảng bằng **RFC_READ_TABLE**; hàm này thay thế:

| | RFC_READ_TABLE (cũ) | Z_ESD017_READ_KNKK (mới) |
|---|---|---|
| Số tiền | Dạng lưu nội bộ (VND: 112941.20) — API tự đọc T014 + TCURX để quy đổi | **Giá trị thực** (11294120) + tiền tệ area (WAERS) |
| Quyền | S_TABU_DIS (cả bảng) | **F_KNKA_KKB** (ACTVT 03, theo từng credit control area) |
| Mã khách hàng | Phải gửi đủ 10 số | Tự ALPHA (100166 → 0000100166) |
| Lỗi | Không phân biệt "không có khách hàng" / "không có dữ liệu" | Mã lỗi rõ: 101–105, 199 |

Source: [Z_ESD017_READ_KNKK.abap](Z_ESD017_READ_KNKK.abap) — chỉ đọc, không ghi. Đã kiểm tra cú pháp trên DRF-930
(SAP_BASIS 702) bằng `/BODS/SYNTAX_CHECK`: **OK**.

API **không cần chờ** hàm này: `Sap:CustomerCreditSource = Auto` (mặc định) dùng RFC_READ_TABLE khi hàm chưa có, tự chuyển sang
hàm trong tối đa 10 phút sau khi hàm được kích hoạt (không cần khởi động lại API).

## 1. SE11 — structure `ZSRDM_S_KNKK`

Data type → Structure `ZSRDM_S_KNKK`, mô tả "RDM: hạn mức tín dụng khách hàng (KNKK)", package như `ZFG_SD_RDM`.
Số tiền dùng **BAPICURR_D** (DEC 23,4 — không cần trường tham chiếu tiền tệ, giá trị đã quy đổi).

| Component | Component type | Ghi chú |
|---|---|---|
| KUNNR | KUNNR | Customer |
| KKBER | KKBER | Credit control area |
| WAERS | WAERS | Tiền tệ của credit control area (T014-WAERS) |
| KLIMK | BAPICURR_D | Credit limit — giá trị thực |
| KNKLI | KNKLI | Customer's account with credit limit reference |
| SAUFT | BAPICURR_D | Sales value — giá trị thực |
| SKFOR | BAPICURR_D | Receivables — giá trị thực |
| SSOBL | BAPICURR_D | Special liabilities — giá trị thực |
| UEDAT | UEDAT | Date credit limit exceeded |
| ERNAM | ERNAM_RF | Created by |
| ERDAT | ERDAT_RF | Created on |
| CTLPC | CTLPC_CM | Risk category |
| KRAUS | KRAUS_CM | Credit information number |
| AEDAT | AEDAT_CM | Changed on |
| AENAM | AENAM_CM | Changed by |
| CASHD | CASHD | Date of last payment |
| CASHA | BAPICURR_D | Last payment — giá trị thực theo CASHC (trống → tiền tệ area) |
| CASHC | CASHC | Currency of last payment |

Enhancement category: *Can be enhanced (character-type or numeric)*. Activate.

## 2. SE37 — function module

- Tên `Z_ESD017_READ_KNKK`, function group **`ZFG_SD_RDM`** (cùng group với Z_ESD016).
- **Attributes**: Processing Type = **Remote-Enabled Module**.

Khai báo tab TRƯỚC khi dán source (khối `*"Local Interface:` trong source chỉ là comment):

**Import** (tick Pass Value):

| Parameter Name | Typing | Associated Type | Default | Optional | Pass Value |
|---|---|---|---|---|---|
| IV_KUNNR | TYPE | KUNNR | | ✓ | ✓ |
| IV_KKBER | TYPE | KKBER | | ✓ | ✓ |
| IV_MAX_ROWS | TYPE | INT4 | 0 | ✓ | ✓ |

**Export**:

| Parameter Name | Typing | Associated Type | Pass Value |
|---|---|---|---|
| EV_COUNT | TYPE | INT4 | ✓ |

**Tables**:

| Parameter Name | Typing | Associated Type | Optional |
|---|---|---|---|
| ET_KNKK | LIKE | ZSRDM_S_KNKK | |
| ET_RETURN | LIKE | BAPIRET2 | |

**Source code**: dán toàn bộ [Z_ESD017_READ_KNKK.abap](Z_ESD017_READ_KNKK.abap) (từ `FUNCTION` tới `ENDFUNCTION`; không có FORM).
Check → Activate.

### Lỗi hay gặp

| Lỗi | Nguyên nhân / cách xử lý |
|---|---|
| Tab Tables: *"ZSRDM_S_KNKK" must be a flat structure…* | Nhập tên structure khi structure chưa Active → màn hình giữ kết quả kiểm tra cũ. Activate structure (SE11), thoát SE37 (`/n`), mở lại, gõ lại `ZSRDM_S_KNKK`, Enter. (Đã kiểm chứng: structure 18 trường đều phẳng, biên dịch OK với TABLES … STRUCTURE.) |
| Gọi RFC: *Function module "Z_ESD017_READ_KNKK" cannot be used for 'remote' calls* | Tab **Attributes** chưa chọn **Remote-Enabled Module** (TFDIR-FMODE trống). Chọn, Save, Activate. API (Auto) chỉ dùng hàm khi FMODE = R, trước đó vẫn đọc RFC_READ_TABLE. |
| IV_MAX_ROWS không Optional | Tick **Optional** cho IV_MAX_ROWS (default 0). |

## 3. Quyền user RFC (user tích hợp RDM)

| Object | Field | Giá trị |
|---|---|---|
| S_RFC | RFC_TYPE / RFC_NAME / ACTVT | FUGR / `ZFG_SD_RDM` / 16 |
| F_KNKA_KKB | ACTVT / KKBER | 03 / các credit control area được xem (VD G100, G110) |

Không có F_KNKA_KKB cho area được hỏi → lỗi 103 (API trả **403**); đọc mọi area → bỏ qua area không có quyền (cảnh báo W 103).

## 4. Mã trả về (ET_RETURN: ID `ZRDM`, PARAMETER = mã, FIELD = trường)

| Số | PARAMETER | TYPE | Khi nào | API |
|---|---|---|---|---|
| 101 | CREDIT_CUSTOMER_NOT_FOUND | E | KUNNR không có trong KNA1 | 404 |
| 102 | CREDIT_AREA_NOT_FOUND | E | KKBER không có trong T014 | 404 |
| 103 | CREDIT_NO_AUTHORITY | E / W | Không có quyền F_KNKA_KKB | 403 (E) · log cảnh báo (W) |
| 104 | CREDIT_NOT_FOUND | I | Không có dòng KNKK phù hợp | 404 (danh sách rỗng) |
| 105 | CREDIT_ROWS_LIMITED | W | Dừng ở IV_MAX_ROWS dòng | log cảnh báo |
| 199 | CREDIT_UNEXPECTED_ERROR | E | Lỗi không lường trước (TRY … CATCH cx_root) | 502 |

## 5. Test trên SE37 (DRF-930) — kết quả mong đợi

| # | IV_KUNNR | IV_KKBER | Mong đợi |
|---|---|---|---|
| 1 | 100166 | G110 | 1 dòng: WAERS VND, SKFOR **-11294120**, SSOBL -21807172, CASHA 412080, CASHC VND, ERDAT 26.08.2022, CTLPC Z02 |
| 2 | 0000100166 | (trống) | 2 dòng: G100 (toàn 0), G110 |
| 3 | 999999999 | | E 101 CREDIT_CUSTOMER_NOT_FOUND, ET_KNKK rỗng |
| 4 | 100166 | ZZZZ | E 102 CREDIT_AREA_NOT_FOUND |
| 5 | 100166 | g110 (chữ thường) | như #1 |
| 6 | (trống) | (trống), IV_MAX_ROWS 5 | 5 dòng + W 105 |
| 7 | user không có F_KNKA_KKB cho G110 | G110 | E 103 CREDIT_NO_AUTHORITY |

Sau khi kích hoạt, kiểm tra từ API (đặt `Sap:CustomerCreditSource = Function` để chắc chắn đang dùng hàm):
`GET /api/customer-credits/100166?creditControlArea=G110` phải ra đúng dòng như khi đọc RFC_READ_TABLE (unit test
`Function_and_read_table_paths_return_the_same_record` kiểm tra 2 đường cho cùng kết quả).
