*&---------------------------------------------------------------------*
*& Function Z_ESD015_PRICE_UPLOAD  (Remote-Enabled Module)
*&---------------------------------------------------------------------*
*& Chuyển từ report ZCSD002_VN_PRICING_UPLD (Pricing Condition Upload,
*& bản VN: ZVLP / ZVDI / ZMIN / ZVIV) thành RFC gọi được từ bên ngoài.
*& Đây là đường ghi bảng giá duy nhất của RDM API (BAPI_PRICES_CONDITIONS
*& không remote-enabled và chưa được dùng ở đâu trên DRF-930).
*&
*& IV_ACTION:
*&   C = tạo (VK11)            - chuỗi màn hình của report gốc
*&   D = đánh dấu xóa (VK12)    - chuỗi màn hình của report gốc (đã sửa A991)
*&   U = sửa giá / đơn vị / hiệu lực đến (VK12) theo KNUMH
*&   R = duyệt bảng giá chờ duyệt KFRST = 'A' (VK12 processing status 02)
*&       theo KNUMH - chuỗi màn hình của ZRSD004_VN_PRICEMASTER (ZSD_VN_PRICE_APR)
*&
*& Lưu ý trạng thái duyệt: CALL TRANSACTION chạy batch input (SY-BINPT = X) nên
*& enhancement ZESD004_DEFAULT_STAT không gán KFRST = 'A'; KBSTAT 01 → KFRST trống
*& theo T686E → bảng giá tạo / sửa qua hàm này CÓ HIỆU LỰC NGAY (duyệt đã làm trên
*& RDM trước khi gọi). R chỉ dùng cho bảng giá chờ duyệt nhập tay trên SAP.
*&
*& Thay đổi so với report gốc:
*&  - Bỏ phần chỉ chạy được với SAP GUI: đọc file Excel
*&    (ALSM_EXCEL_TO_INTERNAL_TABLE), F4 chọn file, POPUP_TO_CONFIRM, ALV.
*&    Dữ liệu truyền vào qua bảng IT_PRICE, kết quả trả qua ET_RETURN.
*&  - Giá / ngày nhận kiểu số / DATS, tự định dạng theo user SAP
*&    (USR01-DCPFM / DATFM) trước khi đưa vào màn hình. Report gốc đẩy chuỗi
*&    Excel nguyên văn → sai khi user dùng dấu phẩy thập phân.
*&  - Sửa xóa ZVIV (bảng A991): report gốc luôn điền F001 = VKORG và không
*&    điền WERKS → RV13A991 (F001 = VKORGAU, F002 = WERKS bắt buộc) luôn lỗi.
*&  - Kiểm tra loại điều kiện ↔ condition table và các key bắt buộc trước
*&    khi gọi BDC; đưa thêm KPEIN (report gốc đọc nhưng không dùng).
*&  - Kiểm tra quyền S_TCODE cho VK11 / VK12 (CALL TRANSACTION trên 7.02
*&    không tự kiểm tra).
*&  - Ghi nhận mọi message E / A và sy-subrc của CALL TRANSACTION.
*&
*& Loại điều kiện → condition table (access sequence trên DRF-930):
*&   ZVLP (ZVPR) → A990  Sales org/Distr.chl/Sold-to/Ship-to/Material/Shpg cond/Plant
*&   ZVDI (ZVD1) → A990
*&   ZVIV (ZPI1) → A991  Sales org (order)/Plant/Material
*&   ZMIN (ZMIN) → A912  Sales org/Distr.chl/Material/Incoterms   (key comb. 01)
*&                 A919  Sales org/Distr.chl/Region/Material      (key comb. 02)
*&---------------------------------------------------------------------*
FUNCTION z_esd015_price_upload.
*"----------------------------------------------------------------------
*"*"Local Interface:
*"  IMPORTING
*"     VALUE(IV_KSCHL) TYPE  KSCHL OPTIONAL
*"     VALUE(IV_ACTION) TYPE  CHAR1 DEFAULT 'C'
*"  EXPORTING
*"     VALUE(EV_SUCCESS) TYPE  INT4
*"     VALUE(EV_ERROR) TYPE  INT4
*"  TABLES
*"      IT_PRICE STRUCTURE  ZSSD_PRICE_UPLD
*"      ET_RETURN STRUCTURE  BAPIRET2
*"----------------------------------------------------------------------
* IV_KSCHL bắt buộc với C / D; U / R lấy loại điều kiện từ KONH theo KNUMH.
* ET_RETURN-ROW = ROW_NO của dòng IT_PRICE (hoặc số thứ tự dòng nếu ROW_NO trống);
* ROW = 0 là lỗi chung của cả lần gọi.

  DATA: lw_price TYPE zssd_price_upld,
        lv_tcode TYPE sytcode,
        lv_selkz TYPE fnam_____4,
        lv_ok    TYPE c,
        lv_row   TYPE i,
        lv_text  TYPE string,
        lt_bdc   TYPE STANDARD TABLE OF bdcdata,
        lt_msg   TYPE STANDARD TABLE OF bdcmsgcoll.

  REFRESH et_return.
  CLEAR: ev_success, ev_error.

*--- Kiểm tra tham số chung --------------------------------------------
  TRANSLATE iv_kschl TO UPPER CASE.
  TRANSLATE iv_action TO UPPER CASE.

  CASE iv_action.
    WHEN 'C'.
      lv_tcode = 'VK11'.
    WHEN 'D' OR 'U' OR 'R'.
      lv_tcode = 'VK12'.
    WHEN OTHERS.
      PERFORM f_prc_error TABLES et_return
        USING 0 'IV_ACTION must be C (create), D (delete), U (change) or R (release).'.
      RETURN.
  ENDCASE.

  IF ( iv_action = 'C' OR iv_action = 'D' )
     AND iv_kschl <> 'ZVLP' AND iv_kschl <> 'ZVDI'
     AND iv_kschl <> 'ZMIN' AND iv_kschl <> 'ZVIV'.
    CONCATENATE 'Condition type' iv_kschl 'is not supported (ZVLP, ZVDI, ZMIN, ZVIV).'
      INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES et_return USING 0 lv_text.
    RETURN.
  ENDIF.

  AUTHORITY-CHECK OBJECT 'S_TCODE' ID 'TCD' FIELD lv_tcode.
  IF sy-subrc <> 0.
    CONCATENATE 'No authorization for transaction' lv_tcode INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES et_return USING 0 lv_text.
    RETURN.
  ENDIF.

  IF it_price[] IS INITIAL.
    PERFORM f_prc_error TABLES et_return USING 0 'IT_PRICE is empty.'.
    RETURN.
  ENDIF.

*--- Từng dòng: CALL TRANSACTION (UPDATE 'S' → tự commit từng dòng) ----
  LOOP AT it_price INTO lw_price.
    lv_row = sy-tabix.
    IF lw_price-row_no IS INITIAL.
      lw_price-row_no = lv_row.
    ENDIF.

    CASE iv_action.
      WHEN 'C' OR 'D'.
        PERFORM f_prc_prepare TABLES et_return
                              USING iv_kschl iv_action
                              CHANGING lw_price lv_selkz lv_ok.
        IF lv_ok IS NOT INITIAL.
          REFRESH: lt_bdc, lt_msg.
          IF iv_action = 'C'.
            PERFORM f_prc_bdc_create TABLES lt_bdc USING lw_price iv_kschl lv_selkz.
          ELSE.
            PERFORM f_prc_bdc_delete TABLES lt_bdc USING lw_price iv_kschl lv_selkz.
          ENDIF.
          PERFORM f_prc_call TABLES lt_bdc lt_msg et_return
                             USING lv_tcode lw_price-row_no
                             CHANGING lv_ok.
        ENDIF.

      WHEN 'U'.
        PERFORM f_prc_change TABLES et_return USING lw_price CHANGING lv_ok.

      WHEN 'R'.
        PERFORM f_prc_release TABLES et_return USING lw_price CHANGING lv_ok.
    ENDCASE.

    IF lv_ok IS INITIAL.
      ev_error = ev_error + 1.
    ELSE.
      ev_success = ev_success + 1.
    ENDIF.
  ENDLOOP.

ENDFUNCTION.

*&---------------------------------------------------------------------*
*& Include LZISD_FG_PRICEF01 — subroutines của Z_ESD015_PRICE_UPLOAD
*&---------------------------------------------------------------------*

*&---------------------------------------------------------------------*
*& Xác định condition table + key combination, kiểm tra dữ liệu bắt buộc
*&---------------------------------------------------------------------*
FORM f_prc_prepare TABLES   pt_return STRUCTURE bapiret2
                   USING    pv_kschl  TYPE kschl
                            pv_action TYPE char1
                   CHANGING pw_price  TYPE zssd_price_upld
                            pv_selkz  TYPE fnam_____4
                            pv_ok     TYPE c.

  DATA: lv_text TYPE string.

  CLEAR: pv_selkz, pv_ok.
  TRANSLATE pw_price-cond_table TO UPPER CASE.

* Loại điều kiện → condition table (bỏ trống thì lấy bảng duy nhất của access sequence)
  CASE pv_kschl.
    WHEN 'ZVLP' OR 'ZVDI'.
      IF pw_price-cond_table IS INITIAL.
        pw_price-cond_table = 'A990'.
      ENDIF.
      IF pw_price-cond_table = 'A990'.
        pv_selkz = 'RV130-SELKZ(01)'.
      ENDIF.
    WHEN 'ZVIV'.
      IF pw_price-cond_table IS INITIAL.
        pw_price-cond_table = 'A991'.
      ENDIF.
      IF pw_price-cond_table = 'A991'.
        pv_selkz = 'RV130-SELKZ(01)'.
      ENDIF.
    WHEN 'ZMIN'.
      IF pw_price-cond_table = 'A912'.
        pv_selkz = 'RV130-SELKZ(01)'.
      ELSEIF pw_price-cond_table = 'A919'.
        pv_selkz = 'RV130-SELKZ(02)'.
      ENDIF.
  ENDCASE.

  IF pv_selkz IS INITIAL.
    CONCATENATE 'Condition table' pw_price-cond_table 'is not valid for condition type' pv_kschl
                '(ZVLP/ZVDI: A990, ZVIV: A991, ZMIN: A912 or A919).'
      INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.

* Key bắt buộc theo condition table
  CASE pw_price-cond_table.
    WHEN 'A990'.
      IF pw_price-vkorg IS INITIAL OR pw_price-vtweg IS INITIAL OR pw_price-kunag IS INITIAL
         OR pw_price-kunwe IS INITIAL OR pw_price-matnr IS INITIAL OR pw_price-vsbed IS INITIAL
         OR pw_price-werks IS INITIAL.
        lv_text = 'A990 requires VKORG, VTWEG, KUNAG, KUNWE, MATNR, VSBED, WERKS.'.
      ENDIF.
    WHEN 'A991'.
      IF pw_price-vkorgau IS INITIAL OR pw_price-werks IS INITIAL OR pw_price-matnr IS INITIAL.
        lv_text = 'A991 requires VKORGAU, WERKS, MATNR.'.
      ENDIF.
    WHEN 'A912' OR 'A919'.
      IF pw_price-vkorg IS INITIAL OR pw_price-vtweg IS INITIAL OR pw_price-matnr IS INITIAL.
        CONCATENATE pw_price-cond_table 'requires VKORG, VTWEG, MATNR.' INTO lv_text SEPARATED BY space.
      ENDIF.
  ENDCASE.

  IF lv_text IS INITIAL.
    IF pw_price-datab IS INITIAL.
      lv_text = 'DATAB (valid from) is required.'.
    ELSEIF pv_action = 'C'.
      IF pw_price-datbi IS INITIAL.
        lv_text = 'DATBI (valid to) is required.'.
      ELSEIF pw_price-datbi < pw_price-datab.
        lv_text = 'DATBI must not be earlier than DATAB.'.
      ELSEIF pw_price-konwa IS INITIAL OR pw_price-kmein IS INITIAL.
        lv_text = 'KONWA (rate unit) and KMEIN (condition UoM) are required.'.
      ENDIF.
    ENDIF.
  ENDIF.

  IF lv_text IS NOT INITIAL.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.

  pv_ok = 'X'.

ENDFORM.                    " F_PRC_PREPARE

*&---------------------------------------------------------------------*
*& VK11 — giữ chuỗi màn hình của F_BDC_CREATE (report gốc)
*&---------------------------------------------------------------------*
FORM f_prc_bdc_create TABLES pt_bdc   STRUCTURE bdcdata
                      USING  pw_price TYPE zssd_price_upld
                             pv_kschl TYPE kschl
                             pv_selkz TYPE fnam_____4.

  DATA: lv_screen TYPE bdc_dynr,
        lv_from   TYPE char10,
        lv_to     TYPE char10,
        lv_rate   TYPE char30,
        lv_kpein  TYPE char10.

  CONCATENATE '1' pw_price-cond_table+1(3) INTO lv_screen.   "A990 → 1990
  WRITE pw_price-datab TO lv_from.                            "định dạng ngày của user
  WRITE pw_price-datbi TO lv_to.
  PERFORM f_prc_amount USING pw_price-kbetr pw_price-konwa CHANGING lv_rate.

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' '0100'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' 'RV13A-KSCHL'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=ANTA'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'RV13A-KSCHL' pv_kschl.

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPLV14A' '0100'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' pv_selkz.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=WEIT'.
  PERFORM f_prc_field  TABLES pt_bdc USING pv_selkz 'X'.

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '/00'.

  CASE pw_price-cond_table.
    WHEN 'A990'.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VKORG'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VTWEG'     pw_price-vtweg.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-KUNAG(01)' pw_price-kunag.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-KUNWE(01)' pw_price-kunwe.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-MATNR(01)' pw_price-matnr.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VSBED(01)' pw_price-vsbed.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-WERKS(01)' pw_price-werks.
    WHEN 'A991'.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VKORGAU'   pw_price-vkorgau.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-WERKS'     pw_price-werks.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-MATNR(01)' pw_price-matnr.
    WHEN 'A912'.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VKORG'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VTWEG'     pw_price-vtweg.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-MATNR(01)' pw_price-matnr.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-INCO1(01)' pw_price-inco1.
    WHEN 'A919'.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VKORG'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-VTWEG(01)' pw_price-vtweg.
      IF pw_price-regio IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-REGIO(01)' pw_price-regio.
      ENDIF.
      PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-MATNR(01)' pw_price-matnr.
  ENDCASE.

  PERFORM f_prc_field TABLES pt_bdc USING 'KOMG-KBSTAT(01)' '01'.          "Status
  PERFORM f_prc_field TABLES pt_bdc USING 'KONP-KBETR(01)'  lv_rate.       "Rate
  PERFORM f_prc_field TABLES pt_bdc USING 'KONP-KONWA(01)'  pw_price-konwa."Rate unit
  IF pw_price-kpein IS NOT INITIAL.
    WRITE pw_price-kpein TO lv_kpein NO-GROUPING LEFT-JUSTIFIED.
    PERFORM f_prc_field TABLES pt_bdc USING 'KONP-KPEIN(01)' lv_kpein.     "Pricing unit
  ENDIF.
  PERFORM f_prc_field TABLES pt_bdc USING 'KONP-KMEIN(01)'  pw_price-kmein."Condition UoM
  PERFORM f_prc_field TABLES pt_bdc USING 'RV13A-DATAB(01)' lv_from.       "Valid from
  PERFORM f_prc_field TABLES pt_bdc USING 'RV13A-DATBI(01)' lv_to.         "Valid to

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=SICH'.

ENDFORM.                    " F_PRC_BDC_CREATE

*&---------------------------------------------------------------------*
*& VK12 — đánh dấu xóa condition record có hiệu lực tại ngày DATAB
*& (chuỗi màn hình của F_BDC_DELETE, đã sửa A991)
*&---------------------------------------------------------------------*
FORM f_prc_bdc_delete TABLES pt_bdc   STRUCTURE bdcdata
                      USING  pw_price TYPE zssd_price_upld
                             pv_kschl TYPE kschl
                             pv_selkz TYPE fnam_____4.

  DATA: lv_screen TYPE bdc_dynr.

  CONCATENATE '1' pw_price-cond_table+1(3) INTO lv_screen.          "A990 → 1990
  PERFORM f_prc_bdc_select TABLES pt_bdc USING pw_price pv_kschl pv_selkz space.

* Danh sách condition record: chọn dòng 1, đánh dấu xóa, lưu
  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=ENTF'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'RV130-SELKZ(01)' 'X'.

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' 'KOMG-MATNR(01)'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=SICH'.

ENDFORM.                    " F_PRC_BDC_DELETE

*&---------------------------------------------------------------------*
*& VK12 — 3 màn hình đầu: loại điều kiện, key combination, màn hình chọn
*& RV13Axxx (key + release status + ngày hiệu lực). Dùng chung cho D / U / R.
*& Field màn hình chọn theo ZRSD004_VN_PRICEMASTER_F01 (BDC_UPDATE).
*& pv_kfrst = 'A' → chỉ chọn bản chờ duyệt; trống → không lọc theo KFRST.
*&---------------------------------------------------------------------*
FORM f_prc_bdc_select TABLES pt_bdc   STRUCTURE bdcdata
                      USING  pw_price TYPE zssd_price_upld
                             pv_kschl TYPE kschl
                             pv_selkz TYPE fnam_____4
                             pv_kfrst TYPE c.

  DATA: lv_prog TYPE bdc_prog,
        lv_from TYPE char10.

  CONCATENATE 'RV13A' pw_price-cond_table+1(3) INTO lv_prog.        "A990 → RV13A990
  WRITE pw_price-datab TO lv_from.                                   "định dạng ngày của user

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPMV13A' '0100'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' 'RV13A-KSCHL'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=ANTA'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'RV13A-KSCHL' pv_kschl.

  PERFORM f_prc_dynpro TABLES pt_bdc USING 'SAPLV14A' '0100'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' pv_selkz.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=WEIT'.
  PERFORM f_prc_field  TABLES pt_bdc USING pv_selkz 'X'.

  PERFORM f_prc_dynpro TABLES pt_bdc USING lv_prog '1000'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_CURSOR' 'SEL_DATE'.
  PERFORM f_prc_field  TABLES pt_bdc USING 'BDC_OKCODE' '=ONLI'.
  CASE pw_price-cond_table.
    WHEN 'A990'.   "F001 VKORG, F002 VTWEG, F003..F007 KUNAG/KUNWE/MATNR/VSBED/WERKS, F008 KFRST
      PERFORM f_prc_field TABLES pt_bdc USING 'F001'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'F002'     pw_price-vtweg.
      PERFORM f_prc_field TABLES pt_bdc USING 'F003-LOW' pw_price-kunag.
      PERFORM f_prc_field TABLES pt_bdc USING 'F004-LOW' pw_price-kunwe.
      PERFORM f_prc_field TABLES pt_bdc USING 'F005-LOW' pw_price-matnr.
      PERFORM f_prc_field TABLES pt_bdc USING 'F006-LOW' pw_price-vsbed.
      PERFORM f_prc_field TABLES pt_bdc USING 'F007-LOW' pw_price-werks.
      IF pv_kfrst IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'F008-LOW' pv_kfrst.
      ENDIF.
    WHEN 'A991'.   "F001 VKORGAU, F002 WERKS, F003 MATNR, F004 KFRST (report gốc thiếu nhánh này)
      PERFORM f_prc_field TABLES pt_bdc USING 'F001'     pw_price-vkorgau.
      PERFORM f_prc_field TABLES pt_bdc USING 'F002'     pw_price-werks.
      PERFORM f_prc_field TABLES pt_bdc USING 'F003-LOW' pw_price-matnr.
      IF pv_kfrst IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'F004-LOW' pv_kfrst.
      ENDIF.
    WHEN 'A912'.   "F001 VKORG, F002 VTWEG, F003 MATNR, F004 INCO1, F005 KFRST
      PERFORM f_prc_field TABLES pt_bdc USING 'F001'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'F002'     pw_price-vtweg.
      PERFORM f_prc_field TABLES pt_bdc USING 'F003-LOW' pw_price-matnr.
      PERFORM f_prc_field TABLES pt_bdc USING 'F004-LOW' pw_price-inco1.
      IF pv_kfrst IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'F005-LOW' pv_kfrst.
      ENDIF.
    WHEN 'A919'.   "F001 VKORG, F002 VTWEG, F003 REGIO, F004 MATNR, F005 KFRST
      PERFORM f_prc_field TABLES pt_bdc USING 'F001'     pw_price-vkorg.
      PERFORM f_prc_field TABLES pt_bdc USING 'F002-LOW' pw_price-vtweg.
      IF pw_price-regio IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'F003-LOW' pw_price-regio.
      ENDIF.
      PERFORM f_prc_field TABLES pt_bdc USING 'F004-LOW' pw_price-matnr.
      IF pv_kfrst IS NOT INITIAL.
        PERFORM f_prc_field TABLES pt_bdc USING 'F005-LOW' pv_kfrst.
      ENDIF.
  ENDCASE.
  PERFORM f_prc_field TABLES pt_bdc USING 'SEL_DATE' lv_from.     "Valid on

ENDFORM.                    " F_PRC_BDC_SELECT

*&---------------------------------------------------------------------*
*& Đọc condition record theo KNUMH: KONH + các dòng Axxx (sắp theo DATAB).
*& Trả loại điều kiện, key combination; pr_rows = bảng dòng Axxx.
*&---------------------------------------------------------------------*
FORM f_prc_lookup TABLES   pt_return STRUCTURE bapiret2
                  USING    pw_price  TYPE zssd_price_upld
                           pv_kfrst  TYPE c
                  CHANGING ps_konh   TYPE konh
                           pv_selkz  TYPE fnam_____4
                           pr_rows   TYPE REF TO data
                           pv_ok     TYPE c.

  DATA: lv_tab  TYPE tabname,
        lv_text TYPE string.
  FIELD-SYMBOLS <lt_rows> TYPE STANDARD TABLE.

  CLEAR: ps_konh, pv_selkz, pv_ok.
  SELECT SINGLE * FROM konh INTO ps_konh WHERE knumh = pw_price-knumh.
  IF sy-subrc <> 0.
    CONCATENATE 'Condition record' pw_price-knumh 'not found.' INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.

  IF ps_konh-kschl <> 'ZVLP' AND ps_konh-kschl <> 'ZVDI'
     AND ps_konh-kschl <> 'ZMIN' AND ps_konh-kschl <> 'ZVIV'.
    CONCATENATE 'Condition record' pw_price-knumh 'is' ps_konh-kschl '- only ZVLP, ZVDI, ZMIN, ZVIV are supported.'
      INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.

  CASE ps_konh-kotabnr.
    WHEN '990' OR '991' OR '912'.
      pv_selkz = 'RV130-SELKZ(01)'.
    WHEN '919'.
      pv_selkz = 'RV130-SELKZ(02)'.
    WHEN OTHERS.
      CONCATENATE 'Condition table' ps_konh-kotabnr 'is not supported.' INTO lv_text SEPARATED BY space.
      PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
      RETURN.
  ENDCASE.

  CONCATENATE 'A' ps_konh-kotabnr INTO lv_tab.
  CREATE DATA pr_rows TYPE STANDARD TABLE OF (lv_tab).
  ASSIGN pr_rows->* TO <lt_rows>.
  IF pv_kfrst IS INITIAL.
    SELECT * FROM (lv_tab) INTO TABLE <lt_rows>
      WHERE kappl = ps_konh-kappl
        AND kschl = ps_konh-kschl
        AND knumh = ps_konh-knumh.
  ELSE.
    SELECT * FROM (lv_tab) INTO TABLE <lt_rows>
      WHERE kappl = ps_konh-kappl
        AND kschl = ps_konh-kschl
        AND knumh = ps_konh-knumh
        AND kfrst = pv_kfrst.
  ENDIF.
  IF sy-subrc <> 0.
    IF pv_kfrst IS INITIAL.
      CONCATENATE 'Condition record' pw_price-knumh 'has no validity period in' lv_tab INTO lv_text SEPARATED BY space.
    ELSE.
      CONCATENATE 'Condition record' pw_price-knumh 'is not pending approval (KFRST' pv_kfrst ').'
        INTO lv_text SEPARATED BY space.
    ENDIF.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.
  SORT <lt_rows> BY ('DATAB').

  pv_ok = 'X'.

ENDFORM.                    " F_PRC_LOOKUP

*&---------------------------------------------------------------------*
*& 1 dòng Axxx → key trong ZSSD_PRICE_UPLD (COND_TABLE, key, DATAB, DATBI)
*&---------------------------------------------------------------------*
FORM f_prc_row_to_key USING    ps_row   TYPE any
                               pv_tabnr TYPE kotabnr
                      CHANGING pw_key   TYPE zssd_price_upld
                               pv_kfrst TYPE c.

  CONCATENATE 'A' pv_tabnr INTO pw_key-cond_table.
  PERFORM f_prc_comp USING ps_row 'VKORG'   CHANGING pw_key-vkorg.
  PERFORM f_prc_comp USING ps_row 'VTWEG'   CHANGING pw_key-vtweg.
  PERFORM f_prc_comp USING ps_row 'KUNAG'   CHANGING pw_key-kunag.
  PERFORM f_prc_comp USING ps_row 'KUNWE'   CHANGING pw_key-kunwe.
  PERFORM f_prc_comp USING ps_row 'MATNR'   CHANGING pw_key-matnr.
  PERFORM f_prc_comp USING ps_row 'INCO1'   CHANGING pw_key-inco1.
  PERFORM f_prc_comp USING ps_row 'VSBED'   CHANGING pw_key-vsbed.
  PERFORM f_prc_comp USING ps_row 'WERKS'   CHANGING pw_key-werks.
  PERFORM f_prc_comp USING ps_row 'VKORGAU' CHANGING pw_key-vkorgau.
  PERFORM f_prc_comp USING ps_row 'REGIO'   CHANGING pw_key-regio.
  PERFORM f_prc_comp USING ps_row 'DATAB'   CHANGING pw_key-datab.
  PERFORM f_prc_comp USING ps_row 'DATBI'   CHANGING pw_key-datbi.
  PERFORM f_prc_comp USING ps_row 'KFRST'   CHANGING pv_kfrst.

ENDFORM.                    " F_PRC_ROW_TO_KEY

FORM f_prc_comp USING    ps_row   TYPE any
                         pv_name  TYPE c
                CHANGING pv_value TYPE any.
  FIELD-SYMBOLS <lv_value> TYPE any.
  CLEAR pv_value.
  ASSIGN COMPONENT pv_name OF STRUCTURE ps_row TO <lv_value>.
  IF sy-subrc = 0.
    pv_value = <lv_value>.
  ENDIF.
ENDFORM.                    " F_PRC_COMP

*&---------------------------------------------------------------------*
*& U — VK12 sửa giá / tiền tệ / pricing unit / đơn vị / hiệu lực đến của KNUMH.
*& Field trống = giữ nguyên. Record bị SAP tách nhiều kỳ: sửa trên kỳ có
*& DATAB = IT_PRICE-DATAB (trống = kỳ đầu tiên); giá dùng chung KONP nên đổi
*& giá có hiệu lực cho cả record.
*&---------------------------------------------------------------------*
FORM f_prc_change TABLES   pt_return STRUCTURE bapiret2
                  USING    pw_price  TYPE zssd_price_upld
                  CHANGING pv_ok     TYPE c.

  DATA: ls_konh   TYPE konh,
        lr_rows   TYPE REF TO data,
        lw_key    TYPE zssd_price_upld,
        lv_selkz  TYPE fnam_____4,
        lv_kfrst  TYPE c,
        lv_found  TYPE c,
        lv_curr   TYPE konwa,
        lv_screen TYPE bdc_dynr,
        lv_tcode  TYPE sytcode VALUE 'VK12',
        lv_rate   TYPE char30,
        lv_kpein  TYPE char10,
        lv_to     TYPE char10,
        lv_text   TYPE string,
        lt_bdc    TYPE STANDARD TABLE OF bdcdata,
        lt_msg    TYPE STANDARD TABLE OF bdcmsgcoll.
  FIELD-SYMBOLS: <lt_rows> TYPE STANDARD TABLE,
                 <ls_row>  TYPE any.

  CLEAR pv_ok.
  PERFORM f_prc_lookup TABLES pt_return
                       USING pw_price space
                       CHANGING ls_konh lv_selkz lr_rows pv_ok.
  IF pv_ok IS INITIAL.
    RETURN.
  ENDIF.
  CLEAR pv_ok.
  ASSIGN lr_rows->* TO <lt_rows>.

  LOOP AT <lt_rows> ASSIGNING <ls_row>.
    CLEAR lw_key.
    PERFORM f_prc_row_to_key USING <ls_row> ls_konh-kotabnr CHANGING lw_key lv_kfrst.
    IF pw_price-datab IS INITIAL OR pw_price-datab = lw_key-datab.
      lv_found = 'X'.
      EXIT.
    ENDIF.
  ENDLOOP.
  IF lv_found IS INITIAL.
    CONCATENATE 'Condition record' pw_price-knumh 'has no validity period starting on the given DATAB.'
      INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no lv_text.
    RETURN.
  ENDIF.

  IF pw_price-datbi IS NOT INITIAL AND pw_price-datbi < lw_key-datab.
    PERFORM f_prc_error TABLES pt_return USING pw_price-row_no 'DATBI must not be earlier than DATAB.'.
    RETURN.
  ENDIF.

* Tiền tệ để định dạng số lẻ của giá: mới (nếu truyền) hoặc hiện tại trên KONP
  lv_curr = pw_price-konwa.
  IF lv_curr IS INITIAL.
    SELECT SINGLE konwa FROM konp INTO lv_curr
      WHERE knumh = ls_konh-knumh AND kopos = '01'.
  ENDIF.

  CONCATENATE '1' ls_konh-kotabnr INTO lv_screen.                   "990 → 1990
  PERFORM f_prc_bdc_select TABLES lt_bdc USING lw_key ls_konh-kschl lv_selkz lv_kfrst.

  PERFORM f_prc_dynpro TABLES lt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES lt_bdc USING 'BDC_OKCODE' '/00'.
  IF pw_price-kbetr IS NOT INITIAL.
    PERFORM f_prc_amount USING pw_price-kbetr lv_curr CHANGING lv_rate.
    PERFORM f_prc_field TABLES lt_bdc USING 'KONP-KBETR(01)' lv_rate.
  ENDIF.
  IF pw_price-konwa IS NOT INITIAL.
    PERFORM f_prc_field TABLES lt_bdc USING 'KONP-KONWA(01)' pw_price-konwa.
  ENDIF.
  IF pw_price-kpein IS NOT INITIAL.
    WRITE pw_price-kpein TO lv_kpein NO-GROUPING LEFT-JUSTIFIED.
    PERFORM f_prc_field TABLES lt_bdc USING 'KONP-KPEIN(01)' lv_kpein.
  ENDIF.
  IF pw_price-kmein IS NOT INITIAL.
    PERFORM f_prc_field TABLES lt_bdc USING 'KONP-KMEIN(01)' pw_price-kmein.
  ENDIF.
  IF pw_price-datbi IS NOT INITIAL.
    WRITE pw_price-datbi TO lv_to.
    PERFORM f_prc_field TABLES lt_bdc USING 'RV13A-DATBI(01)' lv_to.
  ENDIF.

  PERFORM f_prc_dynpro TABLES lt_bdc USING 'SAPMV13A' lv_screen.
  PERFORM f_prc_field  TABLES lt_bdc USING 'BDC_OKCODE' '=SICH'.

  PERFORM f_prc_call TABLES lt_bdc lt_msg pt_return
                     USING lv_tcode pw_price-row_no
                     CHANGING pv_ok.

ENDFORM.                    " F_PRC_CHANGE

*&---------------------------------------------------------------------*
*& R — duyệt KNUMH: mỗi kỳ Axxx còn KFRST = 'A' chạy VK12 đặt
*& KOMG-KBSTAT(01) = '02' (như ZSD_VN_PRICE_APR). VK12 duyệt theo condition
*& record nên kiểm tra lại trước mỗi kỳ, kỳ đã hết 'A' thì bỏ qua.
*&---------------------------------------------------------------------*
FORM f_prc_release TABLES   pt_return STRUCTURE bapiret2
                   USING    pw_price  TYPE zssd_price_upld
                   CHANGING pv_ok     TYPE c.

  DATA: ls_konh   TYPE konh,
        lr_rows   TYPE REF TO data,
        lw_key    TYPE zssd_price_upld,
        lv_selkz  TYPE fnam_____4,
        lv_kfrst  TYPE c,
        lv_knumh  TYPE knumh,
        lv_tab    TYPE tabname,
        lv_screen TYPE bdc_dynr,
        lv_tcode  TYPE sytcode VALUE 'VK12',
        lv_row_ok TYPE c,
        lt_bdc    TYPE STANDARD TABLE OF bdcdata,
        lt_msg    TYPE STANDARD TABLE OF bdcmsgcoll.
  FIELD-SYMBOLS: <lt_rows> TYPE STANDARD TABLE,
                 <ls_row>  TYPE any.

  CLEAR pv_ok.
  PERFORM f_prc_lookup TABLES pt_return
                       USING pw_price 'A'
                       CHANGING ls_konh lv_selkz lr_rows pv_ok.
  IF pv_ok IS INITIAL.
    RETURN.
  ENDIF.
  ASSIGN lr_rows->* TO <lt_rows>.
  CONCATENATE '1' ls_konh-kotabnr INTO lv_screen.
  CONCATENATE 'A' ls_konh-kotabnr INTO lv_tab.

  LOOP AT <lt_rows> ASSIGNING <ls_row>.
    CLEAR lw_key.
    PERFORM f_prc_row_to_key USING <ls_row> ls_konh-kotabnr CHANGING lw_key lv_kfrst.

    SELECT SINGLE knumh FROM (lv_tab) INTO lv_knumh
      WHERE kappl = ls_konh-kappl
        AND kschl = ls_konh-kschl
        AND knumh = ls_konh-knumh
        AND kfrst = 'A'
        AND datab = lw_key-datab.
    IF sy-subrc <> 0.
      CONTINUE.
    ENDIF.

    REFRESH: lt_bdc, lt_msg.
    PERFORM f_prc_bdc_select TABLES lt_bdc USING lw_key ls_konh-kschl lv_selkz 'A'.
    PERFORM f_prc_dynpro TABLES lt_bdc USING 'SAPMV13A' lv_screen.
    PERFORM f_prc_field  TABLES lt_bdc USING 'BDC_CURSOR' 'KOMG-KBSTAT(01)'.
    PERFORM f_prc_field  TABLES lt_bdc USING 'BDC_OKCODE' '=SICH'.
    PERFORM f_prc_field  TABLES lt_bdc USING 'KOMG-KBSTAT(01)' '02'.

    PERFORM f_prc_call TABLES lt_bdc lt_msg pt_return
                       USING lv_tcode pw_price-row_no
                       CHANGING lv_row_ok.
    IF lv_row_ok IS INITIAL.
      CLEAR pv_ok.
    ENDIF.
  ENDLOOP.

ENDFORM.                    " F_PRC_RELEASE

*&---------------------------------------------------------------------*
*& CALL TRANSACTION + chuyển BDCMSGCOLL → BAPIRET2
*&---------------------------------------------------------------------*
FORM f_prc_call TABLES   pt_bdc    STRUCTURE bdcdata
                         pt_msg    STRUCTURE bdcmsgcoll
                         pt_return STRUCTURE bapiret2
                USING    pv_tcode  TYPE sytcode
                         pv_row    TYPE int4
                CHANGING pv_ok     TYPE c.

  DATA: ls_opt   TYPE ctu_params,
        ls_msg   TYPE bdcmsgcoll,
        ls_ret   TYPE bapiret2,
        lv_subrc TYPE sysubrc,
        lv_text  TYPE string,
        lv_rc    TYPE char10.

  ls_opt-dismode = 'N'.    "chạy nền - không bao giờ chờ màn hình
  ls_opt-updmode = 'S'.    "update đồng bộ → đã commit khi trả về
  ls_opt-defsize = 'X'.    "kích thước màn hình chuẩn → dòng (01) của fast-entry ổn định

  CALL TRANSACTION pv_tcode USING pt_bdc[]
                            OPTIONS FROM ls_opt
                            MESSAGES INTO pt_msg[].
  lv_subrc = sy-subrc.

  pv_ok = 'X'.
  LOOP AT pt_msg INTO ls_msg.
    CLEAR ls_ret.
    ls_ret-type       = ls_msg-msgtyp.
    ls_ret-id         = ls_msg-msgid.
    ls_ret-number     = ls_msg-msgnr.
    ls_ret-message_v1 = ls_msg-msgv1.
    ls_ret-message_v2 = ls_msg-msgv2.
    ls_ret-message_v3 = ls_msg-msgv3.
    ls_ret-message_v4 = ls_msg-msgv4.
    ls_ret-field      = ls_msg-fldname.
    ls_ret-row        = pv_row.
    MESSAGE ID ls_msg-msgid TYPE 'S' NUMBER ls_msg-msgnr
            WITH ls_msg-msgv1 ls_msg-msgv2 ls_msg-msgv3 ls_msg-msgv4
            INTO ls_ret-message.
    IF ls_msg-msgtyp CA 'EA'.
      CLEAR pv_ok.
    ENDIF.
    APPEND ls_ret TO pt_return.
  ENDLOOP.

  IF lv_subrc <> 0 AND pv_ok IS NOT INITIAL.
    CLEAR pv_ok.
    lv_rc = lv_subrc.
    CONDENSE lv_rc.
    CONCATENATE pv_tcode 'ended with sy-subrc' lv_rc 'without error message.' INTO lv_text SEPARATED BY space.
    PERFORM f_prc_error TABLES pt_return USING pv_row lv_text.
  ENDIF.

ENDFORM.                    " F_PRC_CALL

*&---------------------------------------------------------------------*
*& Giá → chuỗi theo định dạng số của user SAP (USR01-DCPFM)
*& và số lẻ của tiền tệ (TCURX, mặc định 2; VND = 0)
*&---------------------------------------------------------------------*
FORM f_prc_amount USING    pv_amount TYPE bapicurr_d
                           pv_curr   TYPE konwa
                  CHANGING pv_text   TYPE char30.

  DATA: lv_dec TYPE tcurx-currdec.

  SELECT SINGLE currdec FROM tcurx INTO lv_dec WHERE currkey = pv_curr.
  IF sy-subrc <> 0.
    lv_dec = 2.
  ENDIF.

  WRITE pv_amount TO pv_text DECIMALS lv_dec NO-GROUPING LEFT-JUSTIFIED.
  CONDENSE pv_text NO-GAPS.

ENDFORM.                    " F_PRC_AMOUNT

FORM f_prc_dynpro TABLES pt_bdc     STRUCTURE bdcdata
                  USING  pv_program TYPE any
                         pv_dynpro  TYPE any.
  DATA: ls_bdc TYPE bdcdata.
  ls_bdc-program  = pv_program.
  ls_bdc-dynpro   = pv_dynpro.
  ls_bdc-dynbegin = 'X'.
  APPEND ls_bdc TO pt_bdc.
ENDFORM.                    " F_PRC_DYNPRO

FORM f_prc_field TABLES pt_bdc  STRUCTURE bdcdata
                 USING  pv_fnam TYPE any
                        pv_fval TYPE any.
  DATA: ls_bdc TYPE bdcdata.
  ls_bdc-fnam = pv_fnam.
  ls_bdc-fval = pv_fval.
  APPEND ls_bdc TO pt_bdc.
ENDFORM.                    " F_PRC_FIELD

FORM f_prc_error TABLES pt_return STRUCTURE bapiret2
                 USING  pv_row    TYPE int4
                        pv_text   TYPE any.
  DATA: ls_ret TYPE bapiret2.
  ls_ret-type    = 'E'.
  ls_ret-id      = 'ZSD'.
  ls_ret-number  = '000'.
  ls_ret-message = pv_text.
  ls_ret-row     = pv_row.
  APPEND ls_ret TO pt_return.
ENDFORM.                    " F_PRC_ERROR
