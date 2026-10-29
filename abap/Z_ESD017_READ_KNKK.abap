*&---------------------------------------------------------------------*
*& Function Z_ESD017_READ_KNKK  (Remote-Enabled Module)
*&---------------------------------------------------------------------*
*& Đọc hạn mức tín dụng khách hàng (KNKK) theo mã khách hàng (KUNNR) và
*& credit control area (KKBER) — thay cho RFC_READ_TABLE trên KNKK:
*&   - Số tiền trả về GIÁ TRỊ THỰC theo số lẻ tiền tệ (TCURX), không phải dạng
*&     lưu nội bộ: VND KLIMK lưu 112941.20 → trả 11294120.
*&   - Kèm tiền tệ của credit control area (T014-WAERS).
*&   - Kiểm tra quyền F_KNKA_KKB (ACTVT 03, KKBER) — RFC_READ_TABLE chỉ có
*&     S_TABU_DIS, không theo credit control area.
*&   - Khách hàng nhập dạng hiển thị (100166) tự chuyển ALPHA (0000100166).
*& Chỉ đọc, không ghi. Cú pháp tương thích SAP_BASIS 702 (DRF-930: 702 SP16).
*& Toàn bộ code nằm trong FUNCTION (không FORM) — dán thẳng vào tab Source code.
*&
*& Tham số:
*&   IV_KUNNR    trống = mọi khách hàng
*&   IV_KKBER    trống = mọi credit control area
*&   IV_MAX_ROWS 0 = không giới hạn
*&   ET_KNKK     ZSRDM_S_KNKK (tạo trong SE11 — xem Z_ESD017_READ_KNKK.md)
*&
*& Mã lỗi (ET_RETURN: ID ZRDM, PARAMETER = mã, FIELD = trường):
*&   101 CREDIT_CUSTOMER_NOT_FOUND  E  KUNNR không có trong KNA1
*&   102 CREDIT_AREA_NOT_FOUND      E  KKBER không có trong T014
*&   103 CREDIT_NO_AUTHORITY        E  không có quyền F_KNKA_KKB cho KKBER yêu cầu
*&                                  W  (đọc mọi area) bỏ qua area không có quyền
*&   104 CREDIT_NOT_FOUND           I  không có dòng KNKK phù hợp
*&   105 CREDIT_ROWS_LIMITED        W  dừng ở IV_MAX_ROWS dòng
*&   199 CREDIT_UNEXPECTED_ERROR    E  lỗi không lường trước (không lộ text exception)
*&---------------------------------------------------------------------*
FUNCTION z_esd017_read_knkk.
*"----------------------------------------------------------------------
*"*"Local Interface:
*"  IMPORTING
*"     VALUE(IV_KUNNR) TYPE  KUNNR OPTIONAL
*"     VALUE(IV_KKBER) TYPE  KKBER OPTIONAL
*"     VALUE(IV_MAX_ROWS) TYPE  INT4 DEFAULT 0
*"  EXPORTING
*"     VALUE(EV_COUNT) TYPE  INT4
*"  TABLES
*"      ET_KNKK STRUCTURE  ZSRDM_S_KNKK
*"      ET_RETURN STRUCTURE  BAPIRET2
*"----------------------------------------------------------------------

  TYPES: BEGIN OF ty_knkk,
           kunnr TYPE knkk-kunnr,
           kkber TYPE knkk-kkber,
           klimk TYPE knkk-klimk,
           knkli TYPE knkk-knkli,
           sauft TYPE knkk-sauft,
           skfor TYPE knkk-skfor,
           ssobl TYPE knkk-ssobl,
           uedat TYPE knkk-uedat,
           ernam TYPE knkk-ernam,
           erdat TYPE knkk-erdat,
           ctlpc TYPE knkk-ctlpc,
           kraus TYPE knkk-kraus,
           aedat TYPE knkk-aedat,
           aenam TYPE knkk-aenam,
           cashd TYPE knkk-cashd,
           casha TYPE knkk-casha,
           cashc TYPE knkk-cashc,
         END OF ty_knkk,
         BEGIN OF ty_area,
           kkber   TYPE kkber,
           waers   TYPE waers,
           allowed TYPE c,
         END OF ty_area,
         BEGIN OF ty_factor,       " số tiền nội bộ x factor = giá trị thực (TCURX)
           waers  TYPE waers,
           factor TYPE p LENGTH 16 DECIMALS 6,
         END OF ty_factor.

  DATA: lt_knkk    TYPE STANDARD TABLE OF ty_knkk,
        ls_knkk    TYPE ty_knkk,
        ls_out     LIKE LINE OF et_knkk,
        lt_area    TYPE HASHED TABLE OF ty_area WITH UNIQUE KEY kkber,
        ls_area    TYPE ty_area,
        lt_factor  TYPE HASHED TABLE OF ty_factor WITH UNIQUE KEY waers,
        ls_factor  TYPE ty_factor,
        lt_curr    TYPE SORTED TABLE OF waers WITH UNIQUE KEY table_line,
        lv_curr    TYPE waers,
        lt_denied  TYPE SORTED TABLE OF kkber WITH UNIQUE KEY table_line,
        lt_t014    TYPE STANDARD TABLE OF t014,
        ls_t014    TYPE t014,
        lr_kunnr   TYPE RANGE OF kunnr,
        lrs_kunnr  LIKE LINE OF lr_kunnr,
        lr_kkber   TYPE RANGE OF kkber,
        lrs_kkber  LIKE LINE OF lr_kkber,
        lv_kunnr   TYPE kunnr,
        lv_kkber   TYPE kkber,
        lv_dummy   TYPE kunnr,
        lv_currdec TYPE tcurx-currdec,
        lv_shift   TYPE i,
        lv_pay_cur TYPE waers,
        lv_limited TYPE c,
        lv_text    TYPE string,
        ls_ret     TYPE bapiret2,
        lx_root    TYPE REF TO cx_root.

* add_ret <type> <số> <mã RDM> <field> <text>
  DEFINE add_ret.
    clear ls_ret.
    ls_ret-type      = &1.
    ls_ret-id        = 'ZRDM'.
    ls_ret-number    = &2.
    ls_ret-parameter = &3.
    ls_ret-field     = &4.
    ls_ret-message   = &5.
    append ls_ret to et_return.
  END-OF-DEFINITION.

  CLEAR ev_count.
  REFRESH: et_knkk, et_return.

  TRY.
*--- 1. Khách hàng (ALPHA) phải có trong KNA1 ---------------------------
      IF iv_kunnr IS NOT INITIAL.
        CALL FUNCTION 'CONVERSION_EXIT_ALPHA_INPUT'
          EXPORTING
            input  = iv_kunnr
          IMPORTING
            output = lv_kunnr.
        SELECT SINGLE kunnr FROM kna1 INTO lv_dummy WHERE kunnr = lv_kunnr.
        IF sy-subrc <> 0.
          CONCATENATE 'Khách hàng' lv_kunnr 'không tồn tại (KNA1).'
                 INTO lv_text SEPARATED BY space.
          add_ret 'E' '101' 'CREDIT_CUSTOMER_NOT_FOUND' 'KUNNR' lv_text.
          RETURN.
        ENDIF.
        lrs_kunnr-sign = 'I'.
        lrs_kunnr-option = 'EQ'.
        lrs_kunnr-low = lv_kunnr.
        APPEND lrs_kunnr TO lr_kunnr.
      ENDIF.

*--- 2. Credit control area (T014) + quyền F_KNKA_KKB -------------------
      SELECT * FROM t014 INTO TABLE lt_t014.
      LOOP AT lt_t014 INTO ls_t014.
        CLEAR ls_area.
        ls_area-kkber = ls_t014-kkber.
        ls_area-waers = ls_t014-waers.
        AUTHORITY-CHECK OBJECT 'F_KNKA_KKB'
          ID 'ACTVT' FIELD '03'
          ID 'KKBER' FIELD ls_t014-kkber.
        IF sy-subrc = 0.
          ls_area-allowed = 'X'.
        ENDIF.
        INSERT ls_area INTO TABLE lt_area.
      ENDLOOP.

      IF iv_kkber IS NOT INITIAL.
        lv_kkber = iv_kkber.
        TRANSLATE lv_kkber TO UPPER CASE.
        READ TABLE lt_area INTO ls_area WITH TABLE KEY kkber = lv_kkber.
        IF sy-subrc <> 0.
          CONCATENATE 'Credit control area' lv_kkber 'không tồn tại (T014).'
                 INTO lv_text SEPARATED BY space.
          add_ret 'E' '102' 'CREDIT_AREA_NOT_FOUND' 'KKBER' lv_text.
          RETURN.
        ENDIF.
        IF ls_area-allowed IS INITIAL.
          CONCATENATE 'Không có quyền xem credit control area' lv_kkber
                      '(F_KNKA_KKB).' INTO lv_text SEPARATED BY space.
          add_ret 'E' '103' 'CREDIT_NO_AUTHORITY' 'KKBER' lv_text.
          RETURN.
        ENDIF.
        lrs_kkber-sign = 'I'.
        lrs_kkber-option = 'EQ'.
        lrs_kkber-low = lv_kkber.
        APPEND lrs_kkber TO lr_kkber.
      ENDIF.

*--- 3. Đọc KNKK (SELECT tĩnh theo khóa KUNNR / KKBER) ------------------
      IF iv_max_rows > 0.
        SELECT kunnr kkber klimk knkli sauft skfor ssobl uedat ernam erdat
               ctlpc kraus aedat aenam cashd casha cashc
          FROM knkk
          INTO TABLE lt_knkk
          UP TO iv_max_rows ROWS
          WHERE kunnr IN lr_kunnr
            AND kkber IN lr_kkber
          ORDER BY kunnr kkber.
        IF lines( lt_knkk ) >= iv_max_rows.
          lv_limited = 'X'.
        ENDIF.
      ELSE.
        SELECT kunnr kkber klimk knkli sauft skfor ssobl uedat ernam erdat
               ctlpc kraus aedat aenam cashd casha cashc
          FROM knkk
          INTO TABLE lt_knkk
          WHERE kunnr IN lr_kunnr
            AND kkber IN lr_kkber
          ORDER BY kunnr kkber.
      ENDIF.

*--- 4. Hệ số quy đổi cho mọi tiền tệ dùng tới: 10 ^ (2 - TCURX-CURRDEC) -
*       (không có trong TCURX = 2 số lẻ → hệ số 1)
      LOOP AT lt_area INTO ls_area WHERE waers IS NOT INITIAL.
        INSERT ls_area-waers INTO TABLE lt_curr.
      ENDLOOP.
      LOOP AT lt_knkk INTO ls_knkk WHERE cashc IS NOT INITIAL.
        INSERT ls_knkk-cashc INTO TABLE lt_curr.
      ENDLOOP.
      LOOP AT lt_curr INTO lv_curr.
        CLEAR ls_factor.
        ls_factor-waers = lv_curr.
        ls_factor-factor = 1.
        SELECT SINGLE currdec FROM tcurx INTO lv_currdec WHERE currkey = lv_curr.
        IF sy-subrc = 0.
          lv_shift = 2 - lv_currdec.
          WHILE lv_shift > 0.
            ls_factor-factor = ls_factor-factor * 10.
            lv_shift = lv_shift - 1.
          ENDWHILE.
          WHILE lv_shift < 0.
            ls_factor-factor = ls_factor-factor / 10.
            lv_shift = lv_shift + 1.
          ENDWHILE.
        ENDIF.
        INSERT ls_factor INTO TABLE lt_factor.
      ENDLOOP.

*--- 5. Kết quả -----------------------------------------------------------
      LOOP AT lt_knkk INTO ls_knkk.
        READ TABLE lt_area INTO ls_area WITH TABLE KEY kkber = ls_knkk-kkber.
        IF sy-subrc <> 0.
          CLEAR ls_area.                       " KNKK trỏ area đã xóa khỏi T014
          ls_area-kkber = ls_knkk-kkber.
        ELSEIF ls_area-allowed IS INITIAL.
          " Đọc mọi area: bỏ dòng của area không có quyền, báo 1 lần / area.
          READ TABLE lt_denied TRANSPORTING NO FIELDS WITH TABLE KEY table_line = ls_knkk-kkber.
          IF sy-subrc <> 0.
            INSERT ls_knkk-kkber INTO TABLE lt_denied.
            CONCATENATE 'Bỏ qua credit control area' ls_knkk-kkber
                        '- không có quyền F_KNKA_KKB.' INTO lv_text SEPARATED BY space.
            add_ret 'W' '103' 'CREDIT_NO_AUTHORITY' 'KKBER' lv_text.
          ENDIF.
          CONTINUE.
        ENDIF.

        CLEAR ls_out.
        ls_out-kunnr = ls_knkk-kunnr.
        ls_out-kkber = ls_knkk-kkber.
        ls_out-waers = ls_area-waers.
        ls_out-knkli = ls_knkk-knkli.
        ls_out-uedat = ls_knkk-uedat.
        ls_out-ernam = ls_knkk-ernam.
        ls_out-erdat = ls_knkk-erdat.
        ls_out-ctlpc = ls_knkk-ctlpc.
        ls_out-kraus = ls_knkk-kraus.
        ls_out-aedat = ls_knkk-aedat.
        ls_out-aenam = ls_knkk-aenam.
        ls_out-cashd = ls_knkk-cashd.
        ls_out-cashc = ls_knkk-cashc.

        READ TABLE lt_factor INTO ls_factor WITH TABLE KEY waers = ls_area-waers.
        IF sy-subrc <> 0.
          ls_factor-factor = 1.
        ENDIF.
        ls_out-klimk = ls_knkk-klimk * ls_factor-factor.
        ls_out-sauft = ls_knkk-sauft * ls_factor-factor.
        ls_out-skfor = ls_knkk-skfor * ls_factor-factor.
        ls_out-ssobl = ls_knkk-ssobl * ls_factor-factor.

        " CASHA theo CASHC; chưa có thanh toán (CASHC trống) → tiền tệ của area.
        lv_pay_cur = ls_knkk-cashc.
        IF lv_pay_cur IS INITIAL.
          lv_pay_cur = ls_area-waers.
        ENDIF.
        READ TABLE lt_factor INTO ls_factor WITH TABLE KEY waers = lv_pay_cur.
        IF sy-subrc <> 0.
          ls_factor-factor = 1.
        ENDIF.
        ls_out-casha = ls_knkk-casha * ls_factor-factor.

        APPEND ls_out TO et_knkk.
      ENDLOOP.

      ev_count = lines( et_knkk ).
      IF ev_count = 0.
        add_ret 'I' '104' 'CREDIT_NOT_FOUND' space 'Không có dữ liệu KNKK phù hợp.'.
      ENDIF.
      IF lv_limited = 'X'.
        lv_text = iv_max_rows.
        CONDENSE lv_text.
        CONCATENATE 'Kết quả dừng ở' lv_text 'dòng (IV_MAX_ROWS).'
               INTO lv_text SEPARATED BY space.
        add_ret 'W' '105' 'CREDIT_ROWS_LIMITED' 'IV_MAX_ROWS' lv_text.
      ENDIF.

    CATCH cx_root INTO lx_root.
      REFRESH et_knkk.
      CLEAR ev_count.
      add_ret 'E' '199' 'CREDIT_UNEXPECTED_ERROR' space
              'Lỗi không lường trước khi đọc KNKK - xem ST22 / SM21.'.
  ENDTRY.

ENDFUNCTION.
