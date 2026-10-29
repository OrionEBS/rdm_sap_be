*&---------------------------------------------------------------------*
*& Function Z_ESD016_PRICES_CONDITIONS  (Remote-Enabled Module) — v4
*&---------------------------------------------------------------------*
*& Vỏ RFC cho BAPI_PRICES_CONDITIONS (BAPI không remote-enabled trên DRF-930 và KHÔNG kiểm tra
*& master data / chồng hiệu lực — là cổng nhập hàng loạt CND_PRICES_INBOUND).
*& Cú pháp tương thích SAP_BASIS 702 (DRF-930: 702 SP16). Interface giống v3 (không đổi tab SE37).
*&
*& Kiểm tra TUẦN TỰ, bước nào có lỗi thì dừng, KHÔNG gọi BAPI:
*&   0. Cấu trúc: số dòng <= 1000; IV_DO_COMMIT; OPERATION 009/004/003; whitelist loại / bảng
*&      (ZVLP/ZVDI-A990, ZVIV-A991, ZMIN-A912); hiệu lực; số tạm $ / KNUMH thật; HD / IT khớp CT.
*&   1. Giải mã VARKEY theo T681E + DDIC; độ dài khóa 1..100.
*&   2. Định dạng nội bộ: MATNR (MATN1), KUNAG / KUNWE (ALPHA) phải đúng dạng lưu trong DB
*&      (VD 000000000000100026, 0000100166) — sai thì trả giá trị đúng, KHÔNG tự đổi.
*&   3. Quyền: V_KONH_VKO (VKORG, VTWEG | DUMMY, SPART DUMMY, ACTVT) + V_KONH_VKS (KSCHL, ACTVT).
*&   4. 004 / 003: KNUMH tồn tại, đúng loại / bảng / khóa (KONH-VAKEY trước KFRST).
*&   5. Master data (SELECT tĩnh + cache theo giá trị): TVKO, TVTW, TVKOV, T001W, TVSB, TINC,
*&      T005S (VN), MARA (chưa xóa), KNA1 (chưa xóa), T006, TCURC; KFRST trống / A.
*&   6. Chồng hiệu lực với bảng giá có sẵn — SELECT TĨNH từng bảng A990 / A991 / A912 (không SQL động).
*&      Bỏ: bản đã đánh dấu xóa, chính KNUMH, bản đang hủy, kỳ đang bị thay / xóa trong cùng lần gọi.
*&   7. Trùng lắp giữa các dòng trong lần gọi (phần khóa trước KFRST + chồng hiệu lực).
*&   8. BAPI → E/A hoặc IV_TESTRUN → ROLLBACK; còn lại COMMIT WAIT, EV_COMMITTED = X khi đã commit.
*& Dòng hủy hiệu lực (INDIDELETE = X) và dòng 003: qua 0–4, bỏ 5–7 (luôn cho hủy).
*& TRY ... CATCH cx_root → ROLLBACK + message chung (không lộ text exception).
*&
*& Mã lỗi (TYPE E, ID ZRDM, PARAMETER = mã, ROW = dòng, FIELD = trường):
*&   001 PRICE_NO_AUTHORITY          002 PRICE_INVALID_MASTER_DATA   003 PRICE_INVALID_RELEASE
*&   004 PRICE_OVERLAP               005 PRICE_DUPLICATE_IN_REQUEST  006 PRICE_INVALID_TABLE
*&   008 PRICE_NOT_SUPPORTED         009 PRICE_INVALID_PERIOD        010 PRICE_KEY_MISMATCH
*&   011 PRICE_NOT_FOUND             012 PRICE_INVALID_STRUCTURE     014 PRICE_INVALID_KEY_FORMAT
*&   099 PRICE_UNEXPECTED_ERROR
*&
*& v4 so với v3:
*&   - Bước 6: SQL động (WHERE ghép chuỗi) → SELECT tĩnh từng bảng; không chặn nhầm khi cùng lần gọi
*&     rút ngắn / xóa kỳ record cũ và tạo record mới vào khoảng trống (v3 so với kỳ CŨ trong DB).
*&   - Bước 2 mới: phát hiện khóa gửi dạng hiển thị (100026, 100166) → PRICE_INVALID_KEY_FORMAT
*&     kèm giá trị đúng (v3 báo "không tồn tại" — khó hiểu). Không tự chuyển đổi: BAPI ghi đúng VARKEY
*&     được gửi, đổi riêng giá trị để kiểm tra sẽ làm kiểm tra và dữ liệu ghi lệch nhau.
*&   - Bước 5: cache theo (loại, giá trị) — mỗi giá trị chỉ đọc 1 lần (bảng customizing đã được
*&     buffer trên app server; MARA / KNA1 đọc DB). Giới hạn 1000 dòng / lần gọi.
*&   - Độ dài khóa kiểm tra 1..100 một lần ở bước 1 (VARKEY là CHAR100 cố định — cắt chuỗi trong 100
*&     ký tự không dump).
*&   - IV_DO_COMMIT trống (không chạy thử) bị chặn: BAPI ghi thẳng bảng, kết thúc phiên RFC stateless
*&     tự COMMIT DB → dữ liệu vẫn được ghi dù EV_COMMITTED trống (kiểm chứng DRF-930: KNUMH 0000119706).
*&---------------------------------------------------------------------*
FUNCTION z_esd016_prices_conditions.
*"----------------------------------------------------------------------
*"*"Local Interface:
*"  IMPORTING
*"     VALUE(PI_INITIALMODE) TYPE  CHAR1 OPTIONAL
*"     VALUE(PI_BLOCKNUMBER) TYPE  CHAR10 OPTIONAL
*"     VALUE(PI_PHYSICAL_DELETION) TYPE  CHAR1 OPTIONAL
*"     VALUE(IV_TESTRUN) TYPE  CHAR1 OPTIONAL
*"     VALUE(IV_DO_COMMIT) TYPE  CHAR1 DEFAULT 'X'
*"  EXPORTING
*"     VALUE(EV_COMMITTED) TYPE  CHAR1
*"  TABLES
*"      TI_BAPICONDCT STRUCTURE  BAPICONDCT
*"      TI_BAPICONDHD STRUCTURE  BAPICONDHD
*"      TI_BAPICONDIT STRUCTURE  BAPICONDIT
*"      TI_BAPICONDQS STRUCTURE  BAPICONDQS OPTIONAL
*"      TI_BAPICONDVS STRUCTURE  BAPICONDVS OPTIONAL
*"      TO_BAPIRET2 STRUCTURE  BAPIRET2
*"      TO_BAPIKNUMHS STRUCTURE  BAPIKNUMHS
*"      TO_MEM_INITIAL STRUCTURE  CND_MEM_INITIAL OPTIONAL
*"----------------------------------------------------------------------

  CONSTANTS: lc_varkey_max TYPE i VALUE 100,
             lc_max_rows   TYPE i VALUE 1000.

  TYPES: BEGIN OF ty_layout,
           kvewe   TYPE kvewe,
           kotabnr TYPE kotabnr,
           name    TYPE fieldname,
           offset  TYPE i,
           leng    TYPE i,
         END OF ty_layout,
         BEGIN OF ty_field,
           row   TYPE i,
           name  TYPE fieldname,
           value TYPE char100,
         END OF ty_field,
         BEGIN OF ty_info,
           row       TYPE i,
           keylen    TYPE i,       " độ dài phần khóa trong VARKEY (trước KFRST), 1..100
           vkorg     TYPE vkorg,
           vtweg     TYPE vtweg,
           has_vtweg TYPE c,
           actvt     TYPE activ_auth,
           check_md  TYPE c,       " X = kiểm tra master data / chồng hiệu lực (không phải dòng hủy / 003)
         END OF ty_info,
         BEGIN OF ty_auth,
           vkorg     TYPE vkorg,
           has_vtweg TYPE c,
           vtweg     TYPE vtweg,
           kschl     TYPE kscha,
           actvt     TYPE activ_auth,
           subrc     TYPE sysubrc,
         END OF ty_auth,
         BEGIN OF ty_md,           " cache kết quả kiểm tra master data
           kind  TYPE fieldname,
           value TYPE char100,
           found TYPE c,
           label TYPE string,
           check TYPE string,
         END OF ty_md,
         BEGIN OF ty_period,       " kỳ (KNUMH, từ, đến) có mặt trong lần gọi
           knumh TYPE knumh,
           datab TYPE kodatab,
           datbi TYPE kodatbi,
         END OF ty_period,
         BEGIN OF ty_hit,
           knumh TYPE knumh,
           datab TYPE kodatab,
           datbi TYPE kodatbi,
         END OF ty_hit.

  DATA: ls_ret        TYPE bapiret2,
        lv_error      TYPE c,
        lx_root       TYPE REF TO cx_root,
        lv_text       TYPE string,
        lv_msg        TYPE bapi_msg,
        ls_ct         TYPE bapicondct,
        ls_ct2        TYPE bapicondct,
        ls_hd         TYPE bapicondhd,
        ls_it         TYPE bapicondit,
        lv_row        TYPE i,
        lv_rowc       TYPE char10,
        lv_allowed    TYPE c,
        lv_found      TYPE c,
        lt_layout     TYPE STANDARD TABLE OF ty_layout,
        ls_layout     TYPE ty_layout,
        lt_t681e      TYPE STANDARD TABLE OF t681e,
        ls_t681e      TYPE t681e,
        lt_dfies      TYPE STANDARD TABLE OF dfies,
        ls_dfies      TYPE dfies,
        lv_tabname    TYPE ddobjname,
        lv_offset     TYPE i,
        lv_end        TYPE i,
        lt_fields     TYPE STANDARD TABLE OF ty_field,
        ls_field      TYPE ty_field,
        lt_info       TYPE STANDARD TABLE OF ty_info,
        ls_info       TYPE ty_info,
        ls_info2      TYPE ty_info,
        lt_auth       TYPE HASHED TABLE OF ty_auth WITH UNIQUE KEY vkorg has_vtweg vtweg kschl actvt,
        ls_auth       TYPE ty_auth,
        lt_md         TYPE HASHED TABLE OF ty_md WITH UNIQUE KEY kind value,
        ls_md         TYPE ty_md,
        lt_del        TYPE HASHED TABLE OF knumh WITH UNIQUE KEY table_line,
        lt_period     TYPE HASHED TABLE OF ty_period WITH UNIQUE KEY knumh datab datbi,
        ls_period     TYPE ty_period,
        lv_kvewe      TYPE kvewe,
        lv_kappl      TYPE kappl,
        lv_kschl      TYPE kscha,
        lv_kotabnr    TYPE kotabnr,
        lv_vakey      TYPE vakey,
        lv_label      TYPE string,
        lt_hits       TYPE STANDARD TABLE OF ty_hit,
        ls_hit        TYPE ty_hit,
        lv_loevm      TYPE loevm_ko,
        lv_from       TYPE char10,
        lv_to         TYPE char10,
        lv_vkorg      TYPE vkorg,
        lv_vtweg      TYPE vtweg,
        lv_werks      TYPE werks_d,
        lv_vsbed      TYPE vsbed,
        lv_inco1      TYPE inco1,
        lv_regio      TYPE regio,
        lv_matnr      TYPE matnr,
        lv_matnr_int  TYPE matnr,
        lv_kunnr      TYPE kunnr,
        lv_kunnr_int  TYPE kunnr,
        lv_kunag      TYPE kunag,
        lv_kunwe      TYPE kunwe,
        lv_lvorm      TYPE lvoma,
        lv_kloevm     TYPE loevm_x,
        lv_msehi      TYPE msehi,
        lv_waers      TYPE waers,
        lv_count      TYPE i,
        lv_incl       TYPE syrepid,
        lv_prog       TYPE syrepid,
        lv_line       TYPE i.

* Thêm 1 message lỗi: &1 số, &2 mã (PARAMETER), &3 text, &4 dòng, &5 field
  DEFINE add_error.
    clear ls_ret.
    ls_ret-type      = 'E'.
    ls_ret-id        = 'ZRDM'.
    ls_ret-number    = &1.
    ls_ret-parameter = &2.
    lv_msg           = &3.
    ls_ret-message   = lv_msg.
    ls_ret-row       = &4.
    ls_ret-field     = &5.
    append ls_ret to to_bapiret2.
  END-OF-DEFINITION.

  CLEAR ev_committed.
  REFRESH: to_bapiret2, to_bapiknumhs.

  TRY.
*----------------------------------------------------------------------
* 0. Cấu trúc dữ liệu vào
*----------------------------------------------------------------------
      lv_count = lines( ti_bapicondct ).
      IF lv_count = 0.
        add_error '012' 'PRICE_INVALID_STRUCTURE' 'TI_BAPICONDCT trống — không có gì để xử lý.' 0 'TI_BAPICONDCT'.
      ELSEIF lv_count > lc_max_rows.
        add_error '012' 'PRICE_INVALID_STRUCTURE' 'Quá 1000 dòng TI_BAPICONDCT / lần gọi — chia nhỏ.' 0 'TI_BAPICONDCT'.
      ENDIF.

*     Không commit trong hàm là KHÔNG an toàn: BAPI ghi thẳng KONH / KONP / Axxx, kết thúc phiên RFC
*     stateless tự COMMIT DB → dữ liệu vẫn được ghi trong khi EV_COMMITTED trống (DRF-930, KNUMH
*     0000119706). Chỉ cho phép IV_DO_COMMIT trống khi chạy thử (IV_TESTRUN = X → rollback).
      IF iv_do_commit IS INITIAL AND iv_testrun IS INITIAL.
        CONCATENATE 'IV_DO_COMMIT trống không được hỗ trợ: kết thúc phiên RFC'
                    'tự ghi dữ liệu dù chưa commit. Truyền X (mặc định).'
                    INTO lv_text SEPARATED BY space.
        add_error '012' 'PRICE_INVALID_STRUCTURE' lv_text 0 'IV_DO_COMMIT'.
      ENDIF.

      LOOP AT ti_bapicondit INTO ls_it WHERE indidelete = 'X'.
        INSERT ls_it-cond_no INTO TABLE lt_del.
      ENDLOOP.

      LOOP AT ti_bapicondct INTO ls_ct.
        lv_row = sy-tabix.
        IF ls_ct-operation <> '009' AND ls_ct-operation <> '004' AND ls_ct-operation <> '003'.
          add_error '012' 'PRICE_INVALID_STRUCTURE' 'OPERATION chỉ nhận 009 (tạo), 004 (sửa), 003 (xóa kỳ).' lv_row 'OPERATION'.
          CONTINUE.
        ENDIF.

*       Chỉ các loại điều kiện / bảng RDM được phép ghi (ZMIN chỉ theo A912 như VK11 nghiệp vụ).
*       Danh sách cố ý cố định: bước 6 đọc từng bảng bằng SELECT tĩnh — thêm bảng = thêm code + test.
        CLEAR lv_allowed.
        IF ls_ct-cond_usage = 'A' AND ls_ct-applicatio = 'V'.
          CASE ls_ct-cond_type.
            WHEN 'ZVLP' OR 'ZVDI'.
              IF ls_ct-table_no = '990'. lv_allowed = 'X'. ENDIF.
            WHEN 'ZVIV'.
              IF ls_ct-table_no = '991'. lv_allowed = 'X'. ENDIF.
            WHEN 'ZMIN'.
              IF ls_ct-table_no = '912'. lv_allowed = 'X'. ENDIF.
          ENDCASE.
        ENDIF.
        IF lv_allowed IS INITIAL.
          CONCATENATE 'Không hỗ trợ' ls_ct-cond_type '/ bảng' ls_ct-table_no
                      '(chỉ ZVLP, ZVDI - A990; ZVIV - A991; ZMIN - A912;'
                      'usage A, application V).'
                      INTO lv_text SEPARATED BY space.
          add_error '008' 'PRICE_NOT_SUPPORTED' lv_text lv_row 'COND_TYPE'.
          CONTINUE.
        ENDIF.

        IF ls_ct-valid_from IS INITIAL OR ls_ct-valid_to IS INITIAL OR ls_ct-valid_from > ls_ct-valid_to.
          add_error '009' 'PRICE_INVALID_PERIOD' 'Hiệu lực không hợp lệ (trống hoặc từ ngày > đến ngày).' lv_row 'VALID_FROM'.
        ENDIF.

        IF ls_ct-operation = '009'.
          IF ls_ct-cond_no IS INITIAL OR ls_ct-cond_no(1) <> '$'.
            add_error '012' 'PRICE_INVALID_STRUCTURE' 'Tạo mới (009) phải dùng số tạm dạng $000000001.' lv_row 'COND_NO'.
          ENDIF.
        ELSEIF ls_ct-cond_no IS INITIAL OR ls_ct-cond_no(1) = '$'.
          add_error '012' 'PRICE_INVALID_STRUCTURE' 'Sửa (004) / xóa kỳ (003) phải truyền KNUMH thật.' lv_row 'COND_NO'.
        ELSE.
*         Kỳ của record có sẵn xuất hiện trong lần gọi (004 / 003) → bước 6 không so với kỳ cũ này trong DB
          ls_period-knumh = ls_ct-cond_no.
          ls_period-datab = ls_ct-valid_from.
          ls_period-datbi = ls_ct-valid_to.
          INSERT ls_period INTO TABLE lt_period.
        ENDIF.
      ENDLOOP.

*     Dòng KONH / KONP phải thuộc 1 dòng TI_BAPICONDCT (cùng COND_NO) — chặn ghi lạc sang record khác
      LOOP AT ti_bapicondhd INTO ls_hd.
        lv_row = sy-tabix.
        READ TABLE ti_bapicondct TRANSPORTING NO FIELDS WITH KEY cond_no = ls_hd-cond_no.
        IF sy-subrc <> 0.
          add_error '012' 'PRICE_INVALID_STRUCTURE' 'Dòng TI_BAPICONDHD không có dòng TI_BAPICONDCT cùng COND_NO.' lv_row 'COND_NO'.
        ENDIF.
      ENDLOOP.
      LOOP AT ti_bapicondit INTO ls_it.
        lv_row = sy-tabix.
        READ TABLE ti_bapicondct TRANSPORTING NO FIELDS WITH KEY cond_no = ls_it-cond_no.
        IF sy-subrc <> 0.
          add_error '012' 'PRICE_INVALID_STRUCTURE' 'Dòng TI_BAPICONDIT không có dòng TI_BAPICONDCT cùng COND_NO.' lv_row 'COND_NO'.
        ENDIF.
      ENDLOOP.

*----------------------------------------------------------------------
* 1. Giải mã VARKEY theo T681E + DDIC (như SAP GET_VAKEY_OLD)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT ti_bapicondct INTO ls_ct.
          lv_row = sy-tabix.

          READ TABLE lt_layout TRANSPORTING NO FIELDS
            WITH KEY kvewe = ls_ct-cond_usage kotabnr = ls_ct-table_no.
          IF sy-subrc <> 0.
            REFRESH: lt_t681e, lt_dfies.
            SELECT * FROM t681e INTO TABLE lt_t681e
              WHERE kvewe = ls_ct-cond_usage AND kotabnr = ls_ct-table_no.
            SORT lt_t681e BY fsetyp fselnr.
            CONCATENATE ls_ct-cond_usage ls_ct-table_no INTO lv_tabname.
            CALL FUNCTION 'DDIF_NAMETAB_GET'
              EXPORTING
                tabname   = lv_tabname
              TABLES
                dfies_tab = lt_dfies
              EXCEPTIONS
                OTHERS    = 1.
            IF sy-subrc <> 0 OR lt_t681e[] IS INITIAL.
              CONCATENATE 'Không đọc được cấu trúc bảng điều kiện' lv_tabname INTO lv_text SEPARATED BY space.
              add_error '006' 'PRICE_INVALID_TABLE' lv_text lv_row 'TABLE_NO'.
              CONTINUE.
            ENDIF.

            CLEAR: lv_offset, lv_found.
            LOOP AT lt_t681e INTO ls_t681e.
              READ TABLE lt_dfies INTO ls_dfies WITH KEY fieldname = ls_t681e-sefeld.
              IF sy-subrc <> 0.
                lv_found = 'X'.   " trường T681E không có trong DDIC → không giải mã được an toàn
                EXIT.
              ENDIF.
              CLEAR ls_layout.
              ls_layout-kvewe   = ls_ct-cond_usage.
              ls_layout-kotabnr = ls_ct-table_no.
              ls_layout-name    = ls_t681e-sefeld.
              ls_layout-offset  = lv_offset.
              ls_layout-leng    = ls_dfies-leng.
              APPEND ls_layout TO lt_layout.
              lv_offset = lv_offset + ls_dfies-leng.
            ENDLOOP.
            IF lv_found IS NOT INITIAL OR lv_offset > lc_varkey_max.
              DELETE lt_layout WHERE kvewe = ls_ct-cond_usage AND kotabnr = ls_ct-table_no.
              CONCATENATE 'Cấu trúc khóa bảng' lv_tabname 'không khớp DDIC hoặc dài quá 100 ký tự.'
                          INTO lv_text SEPARATED BY space.
              add_error '006' 'PRICE_INVALID_TABLE' lv_text lv_row 'TABLE_NO'.
              CONTINUE.
            ENDIF.
          ENDIF.

*         VARKEY là CHAR100 cố định (DDIC BAPICONDCT-VARKEY: C 100) và tổng độ dài khóa <= 100
*         → cắt chuỗi không dump. KHÔNG dùng strlen( ): KFRST trống ở cuối (bản đã duyệt) bị bỏ qua.
          CLEAR: ls_info, lv_end.
          ls_info-row    = lv_row.
          ls_info-keylen = -1.
          LOOP AT lt_layout INTO ls_layout WHERE kvewe = ls_ct-cond_usage AND kotabnr = ls_ct-table_no.
            ls_field-row   = lv_row.
            ls_field-name  = ls_layout-name.
            ls_field-value = ls_ct-varkey+ls_layout-offset(ls_layout-leng).
            APPEND ls_field TO lt_fields.
            CASE ls_layout-name.
              WHEN 'KFRST'.
                ls_info-keylen = ls_layout-offset.
              WHEN 'VKORG' OR 'VKORGAU'.
                ls_info-vkorg = ls_field-value.
              WHEN 'VTWEG'.
                ls_info-vtweg     = ls_field-value.
                ls_info-has_vtweg = 'X'.
            ENDCASE.
            lv_end = ls_layout-offset + ls_layout-leng.
          ENDLOOP.
          IF ls_info-keylen < 0.
            ls_info-keylen = lv_end.
          ENDIF.
*         Chặn 1 lần tại đây — mọi phép cắt varkey(keylen) phía sau luôn trong 1..100
          IF ls_info-keylen <= 0 OR ls_info-keylen > lc_varkey_max.
            CONCATENATE 'Độ dài khóa bảng' lv_tabname 'không hợp lệ.' INTO lv_text SEPARATED BY space.
            add_error '006' 'PRICE_INVALID_TABLE' lv_text lv_row 'TABLE_NO'.
            CONTINUE.
          ENDIF.

*         ACTVT: V_KONH_VKO / VKS trên DRF-930 chỉ có 01 / 02 / 03 → hủy hiệu lực, xóa kỳ = sửa (02)
          IF ls_ct-operation = '009'.
            ls_info-actvt = '01'.
          ELSE.
            ls_info-actvt = '02'.
          ENDIF.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH TABLE KEY table_line = ls_ct-cond_no.
          IF sy-subrc <> 0 AND ls_ct-operation <> '003'.
            ls_info-check_md = 'X'.
          ENDIF.
          APPEND ls_info TO lt_info.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 2. Định dạng nội bộ của khóa (conversion exit MATN1 / ALPHA) — chỉ phát hiện, KHÔNG tự đổi.
*    VARKEY được BAPI ghi nguyên văn: nếu chỉ đổi giá trị để kiểm tra thì kiểm tra pass nhưng
*    bảng giá ghi ra lại mang khóa sai (VD MATNR '100026            ' → định giá không bao giờ khớp).
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_fields INTO ls_field WHERE value IS NOT INITIAL.
          CLEAR lv_text.
          CASE ls_field-name.
            WHEN 'MATNR'.
              lv_matnr = ls_field-value.
              CLEAR lv_matnr_int.
              CALL FUNCTION 'CONVERSION_EXIT_MATN1_INPUT'
                EXPORTING
                  input        = lv_matnr
                IMPORTING
                  output       = lv_matnr_int
                EXCEPTIONS
                  error_message = 1
                  OTHERS        = 2.
              IF sy-subrc <> 0.
                CONCATENATE 'Mã vật tư' lv_matnr 'không đúng định dạng.' INTO lv_text SEPARATED BY space.
              ELSEIF lv_matnr_int <> lv_matnr.
                CONCATENATE 'Mã vật tư phải ở dạng nội bộ:' lv_matnr_int INTO lv_text SEPARATED BY space.
              ENDIF.
            WHEN 'KUNAG' OR 'KUNWE' OR 'KUNNR'.
              lv_kunnr = ls_field-value.
              CLEAR lv_kunnr_int.
              CALL FUNCTION 'CONVERSION_EXIT_ALPHA_INPUT'
                EXPORTING
                  input         = lv_kunnr
                IMPORTING
                  output        = lv_kunnr_int
                EXCEPTIONS
                  error_message = 1
                  OTHERS        = 2.
              IF sy-subrc <> 0.
                CONCATENATE 'Mã khách hàng' lv_kunnr 'không đúng định dạng.' INTO lv_text SEPARATED BY space.
              ELSEIF lv_kunnr_int <> lv_kunnr.
                CONCATENATE 'Mã khách hàng phải ở dạng nội bộ:' lv_kunnr_int INTO lv_text SEPARATED BY space.
              ENDIF.
          ENDCASE.
          IF lv_text IS NOT INITIAL.
            add_error '014' 'PRICE_INVALID_KEY_FORMAT' lv_text ls_field-row ls_field-name.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 3. Quyền theo tổ chức bán hàng + loại điều kiện (mọi dòng, kể cả hủy / xóa kỳ)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_info INTO ls_info.
          READ TABLE ti_bapicondct INTO ls_ct INDEX ls_info-row.
          CLEAR ls_auth.
          ls_auth-vkorg     = ls_info-vkorg.
          ls_auth-has_vtweg = ls_info-has_vtweg.
          ls_auth-vtweg     = ls_info-vtweg.
          ls_auth-kschl     = ls_ct-cond_type.
          ls_auth-actvt     = ls_info-actvt.
          READ TABLE lt_auth INTO ls_auth
            WITH TABLE KEY vkorg = ls_auth-vkorg has_vtweg = ls_auth-has_vtweg vtweg = ls_auth-vtweg
                           kschl = ls_auth-kschl actvt = ls_auth-actvt.
          IF sy-subrc <> 0.
*           Trường không có trong khóa bảng (SPART; VTWEG của A991) → DUMMY
            IF ls_auth-has_vtweg = 'X'.
              AUTHORITY-CHECK OBJECT 'V_KONH_VKO'
                ID 'VKORG' FIELD ls_auth-vkorg
                ID 'VTWEG' FIELD ls_auth-vtweg
                ID 'SPART' DUMMY
                ID 'ACTVT' FIELD ls_auth-actvt.
            ELSE.
              AUTHORITY-CHECK OBJECT 'V_KONH_VKO'
                ID 'VKORG' FIELD ls_auth-vkorg
                ID 'VTWEG' DUMMY
                ID 'SPART' DUMMY
                ID 'ACTVT' FIELD ls_auth-actvt.
            ENDIF.
            ls_auth-subrc = sy-subrc.
            IF ls_auth-subrc = 0.
              AUTHORITY-CHECK OBJECT 'V_KONH_VKS'
                ID 'KSCHL' FIELD ls_auth-kschl
                ID 'ACTVT' FIELD ls_auth-actvt.
              ls_auth-subrc = sy-subrc.
            ENDIF.
            INSERT ls_auth INTO TABLE lt_auth.
          ENDIF.

          IF ls_auth-subrc <> 0.
            IF ls_auth-actvt = '01'.
              lv_label = 'tạo'.
            ELSE.
              lv_label = 'sửa / hủy'.
            ENDIF.
            CONCATENATE 'User RFC không có quyền' lv_label 'bảng giá' ls_auth-kschl 'tại sales org' ls_auth-vkorg
                        '(V_KONH_VKO / V_KONH_VKS).' INTO lv_text SEPARATED BY space.
            add_error '001' 'PRICE_NO_AUTHORITY' lv_text ls_info-row 'COND_TYPE'.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 4. Sửa / xóa kỳ: KNUMH phải tồn tại và đúng loại / bảng / khóa (chặn sửa nhầm record)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_info INTO ls_info.
          READ TABLE ti_bapicondct INTO ls_ct INDEX ls_info-row.
          CHECK ls_ct-operation <> '009'.
          CLEAR: lv_kvewe, lv_kappl, lv_kschl, lv_kotabnr, lv_vakey.
          SELECT SINGLE kvewe kappl kschl kotabnr vakey FROM konh
            INTO (lv_kvewe, lv_kappl, lv_kschl, lv_kotabnr, lv_vakey)
            WHERE knumh = ls_ct-cond_no.
          IF sy-subrc <> 0.
            CONCATENATE 'Không tìm thấy bảng giá' ls_ct-cond_no '(KONH).' INTO lv_text SEPARATED BY space.
            add_error '011' 'PRICE_NOT_FOUND' lv_text ls_info-row 'COND_NO'.
            CONTINUE.
          ENDIF.
*         KONH-VAKEY có KFRST ở cuối → chỉ so phần khóa trước KFRST (keylen đã kiểm tra 1..100)
          IF lv_kvewe <> ls_ct-cond_usage OR lv_kappl <> ls_ct-applicatio OR lv_kschl <> ls_ct-cond_type
             OR lv_kotabnr <> ls_ct-table_no
             OR lv_vakey(ls_info-keylen) <> ls_ct-varkey(ls_info-keylen).
            CONCATENATE 'Bảng giá' ls_ct-cond_no 'không khớp loại / bảng / khóa truyền vào — không sửa.'
                        INTO lv_text SEPARATED BY space.
            add_error '010' 'PRICE_KEY_MISMATCH' lv_text ls_info-row 'VARKEY'.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 5. Master data — SELECT tĩnh, cache theo (loại, giá trị): mỗi giá trị chỉ đọc 1 lần.
*    Bảng customizing (TVKO, TVTW, TVKOV, T001W, TVSB, TINC, T005S, T006, TCURC) được buffer
*    trên app server; MARA / KNA1 đọc DB.
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_fields INTO ls_field.
          READ TABLE lt_info INTO ls_info WITH KEY row = ls_field-row.
          CHECK sy-subrc = 0 AND ls_info-check_md = 'X'.

          IF ls_field-name = 'KFRST'.
            IF ls_field-value IS NOT INITIAL AND ls_field-value <> 'A'.
              CONCATENATE 'Release status' ls_field-value 'không hợp lệ (chỉ trống hoặc A).' INTO lv_text SEPARATED BY space.
              add_error '003' 'PRICE_INVALID_RELEASE' lv_text ls_field-row 'KFRST'.
            ENDIF.
            CONTINUE.
          ENDIF.

          IF ls_field-value IS INITIAL.
            CONCATENATE 'Trường khóa' ls_field-name 'trống.' INTO lv_text SEPARATED BY space.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
            CONTINUE.
          ENDIF.

          CLEAR ls_md.
          CASE ls_field-name.
            WHEN 'VKORGAU'.
              ls_md-kind = 'VKORG'.
            WHEN 'KUNAG' OR 'KUNWE'.
              ls_md-kind = 'KUNNR'.
            WHEN OTHERS.
              ls_md-kind = ls_field-name.
          ENDCASE.
          ls_md-value = ls_field-value.
          READ TABLE lt_md INTO ls_md WITH TABLE KEY kind = ls_md-kind value = ls_md-value.
          IF sy-subrc <> 0.
            CASE ls_md-kind.
              WHEN 'VKORG'.
                ls_md-label = 'Sales org'. ls_md-check = 'TVKO'.
                lv_vkorg = ls_md-value.
                SELECT SINGLE vkorg FROM tvko INTO lv_vkorg WHERE vkorg = lv_vkorg.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'VTWEG'.
                ls_md-label = 'Distribution channel'. ls_md-check = 'TVTW'.
                lv_vtweg = ls_md-value.
                SELECT SINGLE vtweg FROM tvtw INTO lv_vtweg WHERE vtweg = lv_vtweg.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'WERKS'.
                ls_md-label = 'Plant'. ls_md-check = 'T001W'.
                lv_werks = ls_md-value.
                SELECT SINGLE werks FROM t001w INTO lv_werks WHERE werks = lv_werks.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'VSBED'.
                ls_md-label = 'Shipping condition'. ls_md-check = 'TVSB'.
                lv_vsbed = ls_md-value.
                SELECT SINGLE vsbed FROM tvsb INTO lv_vsbed WHERE vsbed = lv_vsbed.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'INCO1'.
                ls_md-label = 'Incoterms'. ls_md-check = 'TINC'.
                lv_inco1 = ls_md-value.
                SELECT SINGLE inco1 FROM tinc INTO lv_inco1 WHERE inco1 = lv_inco1.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'REGIO'.
                ls_md-label = 'Region (VN)'. ls_md-check = 'T005S'.
                lv_regio = ls_md-value.
                SELECT SINGLE bland FROM t005s INTO lv_regio WHERE land1 = 'VN' AND bland = lv_regio.
                IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              WHEN 'MATNR'.
                ls_md-label = 'Material'. ls_md-check = 'MARA'.
                lv_matnr = ls_md-value.
                CLEAR lv_lvorm.
                SELECT SINGLE lvorm FROM mara INTO lv_lvorm WHERE matnr = lv_matnr.
                IF sy-subrc = 0.
                  IF lv_lvorm IS INITIAL.
                    ls_md-found = 'X'.
                  ELSE.
                    ls_md-check = 'MARA-LVORM: đã đánh dấu xóa'.
                  ENDIF.
                ENDIF.
              WHEN 'KUNNR'.
                ls_md-label = 'Customer'. ls_md-check = 'KNA1'.
                lv_kunnr = ls_md-value.
                CLEAR lv_kloevm.
                SELECT SINGLE loevm FROM kna1 INTO lv_kloevm WHERE kunnr = lv_kunnr.
                IF sy-subrc = 0.
                  IF lv_kloevm IS INITIAL.
                    ls_md-found = 'X'.
                  ELSE.
                    ls_md-check = 'KNA1-LOEVM: đã đánh dấu xóa'.
                  ENDIF.
                ENDIF.
              WHEN OTHERS.
*               Thận trọng: trường khóa chưa có bảng kiểm tra → không ghi
                ls_md-label = 'Trường'. ls_md-check = 'chưa có bảng kiểm tra'.
            ENDCASE.
            INSERT ls_md INTO TABLE lt_md.
          ENDIF.

          IF ls_md-found IS INITIAL.
            CONCATENATE ls_md-label ' ''' ls_field-value ''' (' ls_field-name ') không hợp lệ trên SAP (' ls_md-check ').'
                        INTO lv_text.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
          ENDIF.
        ENDLOOP.

*       Kênh phân phối phải được gán cho sales org (TVKOV)
        LOOP AT lt_info INTO ls_info WHERE check_md = 'X' AND has_vtweg = 'X'.
          CLEAR ls_md.
          ls_md-kind = 'TVKOV'.
          CONCATENATE ls_info-vkorg ls_info-vtweg INTO ls_md-value RESPECTING BLANKS.
          READ TABLE lt_md INTO ls_md WITH TABLE KEY kind = ls_md-kind value = ls_md-value.
          IF sy-subrc <> 0.
            SELECT SINGLE vkorg FROM tvkov INTO lv_vkorg
              WHERE vkorg = ls_info-vkorg AND vtweg = ls_info-vtweg.
            IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
            INSERT ls_md INTO TABLE lt_md.
          ENDIF.
          IF ls_md-found IS INITIAL.
            CONCATENATE 'Kênh' ls_info-vtweg 'chưa gán cho sales org' ls_info-vkorg '(TVKOV).' INTO lv_text SEPARATED BY space.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_info-row 'VTWEG'.
          ENDIF.
        ENDLOOP.

*       Đơn vị / tiền tệ của dòng điều kiện (bỏ dòng hủy hiệu lực / 003)
        LOOP AT ti_bapicondit INTO ls_it WHERE operation <> '003' AND indidelete IS INITIAL.
          lv_row = sy-tabix.
          IF ls_it-cond_unit IS NOT INITIAL.
            CLEAR ls_md.
            ls_md-kind  = '$UNIT'.
            ls_md-value = ls_it-cond_unit.
            READ TABLE lt_md INTO ls_md WITH TABLE KEY kind = ls_md-kind value = ls_md-value.
            IF sy-subrc <> 0.
              SELECT SINGLE msehi FROM t006 INTO lv_msehi WHERE msehi = ls_it-cond_unit.
              IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              INSERT ls_md INTO TABLE lt_md.
            ENDIF.
            IF ls_md-found IS INITIAL.
              CONCATENATE 'Đơn vị ''' ls_it-cond_unit ''' không tồn tại trên SAP (T006).' INTO lv_text.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'COND_UNIT'.
            ENDIF.
          ENDIF.
          IF ls_it-condcurr IS NOT INITIAL AND ls_it-condcurr <> '%'.
            CLEAR ls_md.
            ls_md-kind  = '$CURR'.
            ls_md-value = ls_it-condcurr.
            READ TABLE lt_md INTO ls_md WITH TABLE KEY kind = ls_md-kind value = ls_md-value.
            IF sy-subrc <> 0.
              SELECT SINGLE waers FROM tcurc INTO lv_waers WHERE waers = ls_it-condcurr.
              IF sy-subrc = 0. ls_md-found = 'X'. ENDIF.
              INSERT ls_md INTO TABLE lt_md.
            ENDIF.
            IF ls_md-found IS INITIAL.
              CONCATENATE 'Tiền tệ ''' ls_it-condcurr ''' không tồn tại trên SAP (TCURC).' INTO lv_text.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'CONDCURR'.
            ENDIF.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 6. Chồng hiệu lực với bảng giá có sẵn — SELECT TĨNH từng bảng (không SQL động).
*    Bỏ qua: chính KNUMH đang sửa; record đang hủy (lt_del); bản đã đánh dấu xóa (KONP-LOEVM_KO);
*    kỳ đang bị thay / xóa trong cùng lần gọi (lt_period: 004 / 003 cùng KNUMH + từ + đến) — kỳ MỚI
*    của các record đó nằm trong lần gọi và được so ở bước 7.
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_info INTO ls_info WHERE check_md = 'X'.
          READ TABLE ti_bapicondct INTO ls_ct INDEX ls_info-row.

          CLEAR: lv_vkorg, lv_vtweg, lv_kunag, lv_kunwe, lv_matnr, lv_vsbed, lv_werks, lv_inco1.
          LOOP AT lt_fields INTO ls_field WHERE row = ls_info-row.
            CASE ls_field-name.
              WHEN 'VKORG' OR 'VKORGAU'. lv_vkorg = ls_field-value.
              WHEN 'VTWEG'.              lv_vtweg = ls_field-value.
              WHEN 'KUNAG'.              lv_kunag = ls_field-value.
              WHEN 'KUNWE'.              lv_kunwe = ls_field-value.
              WHEN 'MATNR'.              lv_matnr = ls_field-value.
              WHEN 'VSBED'.              lv_vsbed = ls_field-value.
              WHEN 'WERKS'.              lv_werks = ls_field-value.
              WHEN 'INCO1'.              lv_inco1 = ls_field-value.
            ENDCASE.
          ENDLOOP.

*         Không lọc KFRST: bản chờ duyệt và đã duyệt cùng khóa đều tính là chồng
          REFRESH lt_hits.
          CASE ls_ct-table_no.
            WHEN '990'.
              SELECT knumh datab datbi FROM a990 INTO CORRESPONDING FIELDS OF TABLE lt_hits
                WHERE kappl = ls_ct-applicatio AND kschl = ls_ct-cond_type
                  AND vkorg = lv_vkorg AND vtweg = lv_vtweg AND kunag = lv_kunag AND kunwe = lv_kunwe
                  AND matnr = lv_matnr AND vsbed = lv_vsbed AND werks = lv_werks
                  AND datab <= ls_ct-valid_to AND datbi >= ls_ct-valid_from.
            WHEN '991'.
              SELECT knumh datab datbi FROM a991 INTO CORRESPONDING FIELDS OF TABLE lt_hits
                WHERE kappl = ls_ct-applicatio AND kschl = ls_ct-cond_type
                  AND vkorgau = lv_vkorg AND werks = lv_werks AND matnr = lv_matnr
                  AND datab <= ls_ct-valid_to AND datbi >= ls_ct-valid_from.
            WHEN '912'.
              SELECT knumh datab datbi FROM a912 INTO CORRESPONDING FIELDS OF TABLE lt_hits
                WHERE kappl = ls_ct-applicatio AND kschl = ls_ct-cond_type
                  AND vkorg = lv_vkorg AND vtweg = lv_vtweg AND matnr = lv_matnr AND inco1 = lv_inco1
                  AND datab <= ls_ct-valid_to AND datbi >= ls_ct-valid_from.
          ENDCASE.

          LOOP AT lt_hits INTO ls_hit.
            CHECK ls_hit-knumh <> ls_ct-cond_no.                          " chính record đang sửa
            READ TABLE lt_del TRANSPORTING NO FIELDS WITH TABLE KEY table_line = ls_hit-knumh.
            CHECK sy-subrc <> 0.                                         " đang hủy trong cùng lần gọi
            READ TABLE lt_period TRANSPORTING NO FIELDS
              WITH TABLE KEY knumh = ls_hit-knumh datab = ls_hit-datab datbi = ls_hit-datbi.
            CHECK sy-subrc <> 0.                                         " kỳ đang bị thay / xóa
            CLEAR lv_loevm.
            SELECT SINGLE loevm_ko FROM konp INTO lv_loevm WHERE knumh = ls_hit-knumh AND kopos = '01'.
            CHECK lv_loevm IS INITIAL.                                   " bản đã đánh dấu xóa
            WRITE ls_hit-datab TO lv_from DD/MM/YYYY.
            WRITE ls_hit-datbi TO lv_to DD/MM/YYYY.
            CONCATENATE 'Chồng hiệu lực với bảng giá' ls_hit-knumh '(' lv_from '-' lv_to ').' INTO lv_text SEPARATED BY space.
            add_error '004' 'PRICE_OVERLAP' lv_text ls_info-row 'VALID_FROM'.
          ENDLOOP.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 7. Trùng lắp giữa các dòng trong lần gọi — chỉ dòng tạo / sửa còn hiệu lực (check_md: đã loại
*    dòng hủy và 003); các kỳ của cùng 1 KNUMH thật không so với nhau.
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_info INTO ls_info WHERE check_md = 'X'.
          READ TABLE ti_bapicondct INTO ls_ct INDEX ls_info-row.
          LOOP AT lt_info INTO ls_info2 WHERE check_md = 'X' AND row > ls_info-row.
            READ TABLE ti_bapicondct INTO ls_ct2 INDEX ls_info2-row.
            CHECK ls_ct2-cond_no <> ls_ct-cond_no OR ls_ct-cond_no(1) = '$'.
            CHECK ls_ct2-cond_usage = ls_ct-cond_usage AND ls_ct2-table_no = ls_ct-table_no
              AND ls_ct2-cond_type = ls_ct-cond_type AND ls_ct2-applicatio = ls_ct-applicatio.
            CHECK ls_ct-varkey(ls_info-keylen) = ls_ct2-varkey(ls_info-keylen).
            CHECK ls_ct-valid_from <= ls_ct2-valid_to AND ls_ct2-valid_from <= ls_ct-valid_to.
            WRITE ls_info-row TO lv_rowc NO-GROUPING LEFT-JUSTIFIED.
            CONCATENATE 'Trùng khóa và chồng hiệu lực với dòng' lv_rowc 'trong cùng lần gọi.' INTO lv_text SEPARATED BY space.
            add_error '005' 'PRICE_DUPLICATE_IN_REQUEST' lv_text ls_info2-row 'VALID_FROM'.
          ENDLOOP.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* Có lỗi kiểm tra → không gọi BAPI (rollback cho chắc LUW sạch)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS NOT INITIAL.
        CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
        RETURN.
      ENDIF.

*----------------------------------------------------------------------
* 8. Gọi BAPI → commit / rollback
*----------------------------------------------------------------------
      CALL FUNCTION 'BAPI_PRICES_CONDITIONS'
        EXPORTING
          pi_initialmode       = pi_initialmode
          pi_blocknumber       = pi_blocknumber
          pi_physical_deletion = pi_physical_deletion
        TABLES
          ti_bapicondct        = ti_bapicondct
          ti_bapicondhd        = ti_bapicondhd
          ti_bapicondit        = ti_bapicondit
          ti_bapicondqs        = ti_bapicondqs
          ti_bapicondvs        = ti_bapicondvs
          to_bapiret2          = to_bapiret2
          to_bapiknumhs        = to_bapiknumhs
          to_mem_initial       = to_mem_initial
        EXCEPTIONS
          error_message        = 98
          OTHERS               = 99.

      IF sy-subrc <> 0.
        CLEAR ls_ret.
        ls_ret-type       = 'E'.
        ls_ret-id         = sy-msgid.
        ls_ret-number     = sy-msgno.
        ls_ret-message_v1 = sy-msgv1.
        ls_ret-message_v2 = sy-msgv2.
        ls_ret-message_v3 = sy-msgv3.
        ls_ret-message_v4 = sy-msgv4.
        IF sy-msgid IS NOT INITIAL.
          MESSAGE ID sy-msgid TYPE 'S' NUMBER sy-msgno
                  WITH sy-msgv1 sy-msgv2 sy-msgv3 sy-msgv4 INTO ls_ret-message.
        ELSE.
          ls_ret-id        = 'ZRDM'.
          ls_ret-number    = '099'.
          ls_ret-parameter = 'PRICE_UNEXPECTED_ERROR'.
          ls_ret-message   = 'BAPI_PRICES_CONDITIONS báo lỗi không có message — đã rollback.'.
        ENDIF.
        APPEND ls_ret TO to_bapiret2.
      ENDIF.

      LOOP AT to_bapiret2 INTO ls_ret WHERE type CA 'EA'.
        lv_error = 'X'.
        EXIT.
      ENDLOOP.

      IF lv_error IS NOT INITIAL OR iv_testrun IS NOT INITIAL.
        CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
        RETURN.
      ENDIF.

*     IV_DO_COMMIT trống đã bị chặn ở bước 0 → tới đây luôn commit; EV_COMMITTED = X chỉ khi commit thật
      CLEAR ls_ret.
      CALL FUNCTION 'BAPI_TRANSACTION_COMMIT'
        EXPORTING
          wait   = 'X'
        IMPORTING
          return = ls_ret.
      IF ls_ret-type CA 'EA'.
        APPEND ls_ret TO to_bapiret2.
        RETURN.
      ENDIF.

      ev_committed = 'X'.

    CATCH cx_root INTO lx_root.
*     Lỗi bất ngờ → hủy LUW, message chung (không lộ text exception ra ngoài).
*     Tra cứu: MESSAGE_V1 = lớp exception, MESSAGE_V2 / V3 = include / dòng phát sinh.
      CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
      CLEAR ev_committed.
      add_error '099' 'PRICE_UNEXPECTED_ERROR' 'Lỗi không lường trước khi xử lý bảng giá — đã rollback, chưa ghi SAP.' 0 space.
      lx_root->get_source_position( IMPORTING program_name = lv_prog include_name = lv_incl source_line = lv_line ).
      READ TABLE to_bapiret2 INTO ls_ret INDEX lines( to_bapiret2 ).
      ls_ret-message_v1 = cl_abap_classdescr=>get_class_name( lx_root ).
      ls_ret-message_v2 = lv_incl.
      ls_ret-message_v3 = lv_line.
      MODIFY to_bapiret2 FROM ls_ret INDEX lines( to_bapiret2 ).
  ENDTRY.

ENDFUNCTION.
