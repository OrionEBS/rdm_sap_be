FUNCTION z_esd016_prices_conditions.
*"----------------------------------------------------------------------
*"*"Local Interface:
*"  IMPORTING
*"     VALUE(pi_initialmode) TYPE  char1 OPTIONAL
*"     VALUE(pi_blocknumber) TYPE  char10 OPTIONAL
*"     VALUE(pi_physical_deletion) TYPE  char1 OPTIONAL
*"     VALUE(iv_testrun) TYPE  char1 OPTIONAL
*"     VALUE(iv_do_commit) TYPE  char1 DEFAULT 'X'
*"  EXPORTING
*"     VALUE(ev_committed) TYPE  char1
*"  TABLES
*"      ti_bapicondct STRUCTURE  bapicondct
*"      ti_bapicondhd STRUCTURE  bapicondhd
*"      ti_bapicondit STRUCTURE  bapicondit
*"      ti_bapicondqs STRUCTURE  bapicondqs OPTIONAL
*"      ti_bapicondvs STRUCTURE  bapicondvs OPTIONAL
*"      to_bapiret2 STRUCTURE  bapiret2
*"      to_bapiknumhs STRUCTURE  bapiknumhs
*"      to_mem_initial STRUCTURE  cnd_mem_initial OPTIONAL
*"----------------------------------------------------------------------

  TYPES: BEGIN OF ty_layout,
           kvewe   TYPE kvewe,
           kotabnr TYPE kotabnr,
           name    TYPE fieldname,
           offset  TYPE i,
           leng    TYPE i,
           convext TYPE convexit,
         END OF ty_layout,
         BEGIN OF ty_field,
           row   TYPE i,
           name  TYPE fieldname,
           value TYPE char100,
         END OF ty_field,
         BEGIN OF ty_auth_cache,
           vkorg TYPE vkorg,
           vtweg TYPE vtweg,
           spart TYPE spart,
           kschl TYPE kscha,
           subrc TYPE sysubrc,
         END OF ty_auth_cache.

  DATA: ls_ret         TYPE bapiret2,
        lv_error       TYPE c,
        lx_root        TYPE REF TO cx_root,
        lv_text        TYPE string,
        lv_msg         TYPE bapi_msg,
        ls_ct          TYPE bapicondct,
        ls_ct2         TYPE bapicondct,
        ls_it          TYPE bapicondit,
        lv_row         TYPE i,
        lv_row2        TYPE i,
        lt_layout      TYPE HASHED TABLE OF ty_layout
                            WITH UNIQUE KEY kvewe kotabnr name,
        lt_layout_tab  TYPE STANDARD TABLE OF ty_layout,
        ls_layout      TYPE ty_layout,
        lt_t681e       TYPE STANDARD TABLE OF t681e,
        ls_t681e       TYPE t681e,
        lt_dfies       TYPE STANDARD TABLE OF dfies,
        ls_dfies       TYPE dfies,
        lv_tabname     TYPE ddobjname,
        lv_offset      TYPE i,
        lv_varkey_len  TYPE i,
        lt_fields      TYPE STANDARD TABLE OF ty_field,
        ls_field       TYPE ty_field,
        lv_fm_conv     TYPE rs38l_fnam,
        lv_val_input   TYPE char100,
        lv_val_output  TYPE char100,
        lv_vkey1       TYPE char100,
        lv_vkey2       TYPE char100,
        lv_kfrst_off   TYPE i,
        lv_from        TYPE char10,
        lt_del         TYPE HASHED TABLE OF knumh WITH UNIQUE KEY table_line,
        lt_auth_cache  TYPE HASHED TABLE OF ty_auth_cache
                            WITH UNIQUE KEY vkorg vtweg spart kschl,
        ls_auth_cache  TYPE ty_auth_cache,
        lv_vkorg       TYPE vkorg,
        lv_vtweg       TYPE vtweg,
        lv_spart       TYPE spart,
        lv_dummy       TYPE char1.

  DEFINE add_error.
    CLEAR ls_ret.
    ls_ret-type       = 'E'.
    ls_ret-id         = 'ZRDM'.
    ls_ret-number     = &1.
    ls_ret-parameter  = &2.
    lv_msg            = &3.
    ls_ret-message    = lv_msg.
    ls_ret-row        = &4.
    ls_ret-field      = &5.
    APPEND ls_ret TO to_bapiret2.
  END-OF-DEFINITION.

  CLEAR ev_committed.
  REFRESH: to_bapiret2, to_bapiknumhs.

  TRY.
*----------------------------------------------------------------------
* 1. Phân loại record xóa & Pre-check điều kiện
*----------------------------------------------------------------------
      LOOP AT ti_bapicondit INTO ls_it WHERE indidelete = 'X'.
        INSERT ls_it-cond_no INTO TABLE lt_del.
      ENDLOOP.

*----------------------------------------------------------------------
* 2. Giải mã VARKEY an toàn & Đọc Master Data
*----------------------------------------------------------------------
      LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
        lv_row = sy-tabix.
        READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
        CHECK sy-subrc <> 0. " Bỏ qua các record bị xóa

        " Đọc Metadata của bảng điều kiện (Axxx) có cơ chế Cache
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
            CONCATENATE 'Không đọc được cấu trúc bảng điều kiện' lv_tabname
              INTO lv_text SEPARATED BY space.
            add_error '006' 'PRICE_INVALID_TABLE' lv_text lv_row 'TABLE_NO'.
            CONTINUE.
          ENDIF.

          CLEAR lv_offset.
          LOOP AT lt_t681e INTO ls_t681e.
            READ TABLE lt_dfies INTO ls_dfies WITH KEY fieldname = ls_t681e-sefeld.
            IF sy-subrc <> 0.
              EXIT.
            ENDIF.
            CLEAR ls_layout.
            ls_layout-kvewe   = ls_ct-cond_usage.
            ls_layout-kotabnr = ls_ct-table_no.
            ls_layout-name    = ls_t681e-sefeld.
            ls_layout-offset  = lv_offset.
            ls_layout-leng    = ls_dfies-leng.
            ls_layout-convext = ls_dfies-convexit.
            INSERT ls_layout INTO TABLE lt_layout.
            lv_offset = lv_offset + ls_dfies-leng.
          ENDLOOP.
        ENDIF.

        " Trích xuất Layout cho bảng hiện tại
        CLEAR lt_layout_tab.
        LOOP AT lt_layout INTO ls_layout
          WHERE kvewe = ls_ct-cond_usage AND kotabnr = ls_ct-table_no.
          APPEND ls_layout TO lt_layout_tab.
        ENDLOOP.

        " Kiểm tra an toàn độ dài VARKEY trước khi cắt chuỗi tránh ST22 Dump
        lv_varkey_len = strlen( ls_ct-varkey ).

        LOOP AT lt_layout_tab INTO ls_layout.
          IF lv_varkey_len < ( ls_layout-offset + ls_layout-leng ).
            CONCATENATE 'Chuỗi VARKEY không đủ độ dài cho trường' ls_layout-name
              INTO lv_text SEPARATED BY space.
            add_error '007' 'PRICE_INVALID_VARKEY_LEN' lv_text lv_row ls_layout-name.
            CONTINUE.
          ENDIF.

          ls_field-row   = lv_row.
          ls_field-name  = ls_layout-name.
          ls_field-value = ls_ct-varkey+ls_layout-offset(ls_layout-leng).

          " Xử lý Conversion Exit chuẩn (ví dụ ALPHA cho MATNR, KUNNR)
          IF ls_layout-convext IS NOT INITIAL AND ls_field-value IS NOT INITIAL.
            CONCATENATE 'CONVERSION_EXIT_' ls_layout-convext '_INPUT' INTO lv_fm_conv.
            lv_val_input = ls_field-value.
            CALL FUNCTION lv_fm_conv
              EXPORTING
                input  = lv_val_input
              IMPORTING
                output = lv_val_output
              EXCEPTIONS
                OTHERS = 1.
            IF sy-subrc = 0.
              ls_field-value = lv_val_output.
            ENDIF.
          ENDIF.

          APPEND ls_field TO lt_fields.
        ENDLOOP.
      ENDLOOP.

*----------------------------------------------------------------------
* 3. Kiểm tra phân quyền chi tiết (SD Condition Authorization)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
          lv_row = sy-tabix.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
          CHECK sy-subrc <> 0.

          CLEAR: lv_vkorg, lv_vtweg, lv_spart.
          " Trích xuất Org units đã giải mã từ lt_fields
          READ TABLE lt_fields INTO ls_field WITH KEY row = lv_row name = 'VKORG'.
          IF sy-subrc = 0. lv_vkorg = ls_field-value. ENDIF.
          READ TABLE lt_fields INTO ls_field WITH KEY row = lv_row name = 'VTWEG'.
          IF sy-subrc = 0. lv_vtweg = ls_field-value. ENDIF.
          READ TABLE lt_fields INTO ls_field WITH KEY row = lv_row name = 'SPART'.
          IF sy-subrc = 0. lv_spart = ls_field-value. ENDIF.

          " Kiểm tra Cache phân quyền
          READ TABLE lt_auth_cache INTO ls_auth_cache
            WITH KEY vkorg = lv_vkorg vtweg = lv_vtweg spart = lv_spart kschl = ls_ct-cond_type.
          IF sy-subrc <> 0.
            AUTHORITY-CHECK OBJECT 'V_KONH_VKO'
              ID 'VKORG' FIELD lv_vkorg
              ID 'VTWEG' FIELD lv_vtweg
              ID 'SPART' FIELD lv_spart
              ID 'KSCHL' FIELD ls_ct-cond_type
              ID 'ACTVT' FIELD '02'.

            ls_auth_cache-vkorg = lv_vkorg.
            ls_auth_cache-vtweg = lv_vtweg.
            ls_auth_cache-spart = lv_spart.
            ls_auth_cache-kschl = ls_ct-cond_type.
            ls_auth_cache-subrc = sy-subrc.
            INSERT ls_auth_cache INTO TABLE lt_auth_cache.
          ENDIF.

          IF ls_auth_cache-subrc <> 0.
            CONCATENATE 'Không có quyền cập nhật bảng giá loại' ls_ct-cond_type
              'tại Sales Org' lv_vkorg INTO lv_text SEPARATED BY space.
            add_error '001' 'PRICE_NO_AUTHORITY' lv_text lv_row 'COND_TYPE'.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 4. Kiểm tra Master Data (Dùng Static Types, ngăn ngừa SQL Injection)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT lt_fields INTO ls_field.
          IF ls_field-value IS INITIAL AND ls_field-name <> 'KFRST'.
            CONCATENATE 'Trường khóa' ls_field-name 'bị trống.' INTO lv_text SEPARATED BY space.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
            CONTINUE.
          ENDIF.

          CASE ls_field-name.
            WHEN 'KFRST'.
              IF ls_field-value IS NOT INITIAL AND ls_field-value <> 'A'.
                add_error '003' 'PRICE_INVALID_RELEASE'
                          'Release status không hợp lệ (chỉ chấp nhận rỗng hoặc A).'
                          ls_field-row 'KFRST'.
              ENDIF.

            WHEN 'VKORG' OR 'VKORGAU'.
              SELECT SINGLE vkorg FROM tvko INTO @DATA(lv_chk_vkorg) WHERE vkorg = @ls_field-value.
              IF sy-subrc <> 0.
                CONCATENATE 'Sales Org' ls_field-value 'không tồn tại.' INTO lv_text SEPARATED BY space.
                add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
              ENDIF.

            WHEN 'VTWEG'.
              SELECT SINGLE vtweg FROM tvtw INTO @DATA(lv_chk_vtweg) WHERE vtweg = @ls_field-value.
              IF sy-subrc <> 0.
                CONCATENATE 'Dist. Channel' ls_field-value 'không tồn tại.' INTO lv_text SEPARATED BY space.
                add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
              ENDIF.

            WHEN 'WERKS'.
              SELECT SINGLE werks FROM t001w INTO @DATA(lv_chk_werks) WHERE werks = @ls_field-value.
              IF sy-subrc <> 0.
                CONCATENATE 'Plant' ls_field-value 'không tồn tại.' INTO lv_text SEPARATED BY space.
                add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
              ENDIF.

            WHEN 'MATNR'.
              SELECT SINGLE matnr FROM mara INTO @DATA(lv_chk_matnr) WHERE matnr = @ls_field-value.
              IF sy-subrc <> 0.
                CONCATENATE 'Material' ls_field-value 'không tồn tại.' INTO lv_text SEPARATED BY space.
                add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
              ENDIF.

            WHEN 'KUNAG' OR 'KUNWE' OR 'KUNNR'.
              SELECT SINGLE kunnr FROM kna1 INTO @DATA(lv_chk_kunnr) WHERE kunnr = @ls_field-value.
              IF sy-subrc <> 0.
                CONCATENATE 'Customer' ls_field-value 'không tồn tại.' INTO lv_text SEPARATED BY space.
                add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
              ENDIF.
          ENDCASE.
        ENDLOOP.

        " Validate Currency và Unit of Measure
        LOOP AT ti_bapicondit INTO ls_it WHERE operation <> '003' AND indidelete IS INITIAL.
          lv_row = sy-tabix.
          IF ls_it-cond_unit IS NOT INITIAL.
            SELECT SINGLE msehi FROM t006 INTO @DATA(lv_chk_uom) WHERE msehi = @ls_it-cond_unit.
            IF sy-subrc <> 0.
              CONCATENATE 'Đơn vị tính' ls_it-cond_unit 'không hợp lệ.' INTO lv_text SEPARATED BY space.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'COND_UNIT'.
            ENDIF.
          ENDIF.
          IF ls_it-condcurr IS NOT INITIAL AND ls_it-condcurr <> '%'.
            SELECT SINGLE waers FROM tcurc INTO @DATA(lv_chk_curr) WHERE waers = @ls_it-condcurr.
            IF sy-subrc <> 0.
              CONCATENATE 'Loại tiền tệ' ls_it-condcurr 'không hợp lệ.' INTO lv_text SEPARATED BY space.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'CONDCURR'.
            ENDIF.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 5. Kiểm tra xung đột dữ liệu ngay trong Payload gọi lên
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
        LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
          lv_row = sy-tabix.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
          CHECK sy-subrc <> 0.

          LOOP AT ti_bapicondct INTO ls_ct2 WHERE operation <> '003'.
            lv_row2 = sy-tabix.
            READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct2-cond_no.
            CHECK sy-subrc <> 0 AND lv_row2 > lv_row.

            CHECK ls_ct2-cond_no <> ls_ct-cond_no OR ls_ct-cond_no(1) = '$'.
            CHECK ls_ct2-table_no = ls_ct-table_no
              AND ls_ct2-cond_type = ls_ct-cond_type
              AND ls_ct2-applicatio = ls_ct-applicatio.

            lv_vkey1 = ls_ct-varkey.
            lv_vkey2 = ls_ct2-varkey.

            " Bỏ qua trường KFRST khi kiểm tra trùng Key
            READ TABLE lt_layout INTO ls_layout
              WITH KEY kvewe = ls_ct-cond_usage kotabnr = ls_ct-table_no name = 'KFRST'.
            IF sy-subrc = 0 AND strlen( lv_vkey1 ) >= ( ls_layout-offset + ls_layout-leng )
                            AND strlen( lv_vkey2 ) >= ( ls_layout-offset + ls_layout-leng ).
              lv_vkey1+ls_layout-offset(ls_layout-leng) = space.
              lv_vkey2+ls_layout-offset(ls_layout-leng) = space.
            ENDIF.

            CHECK lv_vkey1 = lv_vkey2.
            " Kiểm tra giao thoa thời gian (Validity overlap)
            IF ls_ct-valid_from <= ls_ct2-valid_to AND ls_ct2-valid_from <= ls_ct-valid_to.
              WRITE lv_row TO lv_from NO-GROUPING LEFT-JUSTIFIED.
              CONCATENATE 'Trùng lặp khóa và chồng chéo khoảng hiệu lực với dòng' lv_from
                INTO lv_text SEPARATED BY space.
              add_error '005' 'PRICE_DUPLICATE_IN_REQUEST' lv_text lv_row2 'VALID_FROM'.
            ENDIF.
          ENDLOOP.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 6. Chặn thực thi nếu có lỗi tiền kiểm tra
*----------------------------------------------------------------------
      IF to_bapiret2[] IS NOT INITIAL.
        RETURN.
      ENDIF.

*----------------------------------------------------------------------
* 7. Gọi SAP Standard BAPI
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
          ls_ret-message   = 'Lỗi không xác định từ BAPI_PRICES_CONDITIONS.'.
        ENDIF.
        APPEND ls_ret TO to_bapiret2.
      ENDIF.

      " Kiểm tra kết quả trả về từ BAPI
      LOOP AT to_bapiret2 INTO ls_ret WHERE type CA 'EA'.
        lv_error = 'X'.
        EXIT.
      ENDLOOP.

      IF lv_error IS NOT INITIAL OR iv_testrun IS NOT INITIAL.
        CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
        RETURN.
      ENDIF.

*----------------------------------------------------------------------
* 8. Quản lý Commit LUW
*----------------------------------------------------------------------
      IF iv_do_commit = 'X'.
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
      ENDIF.

      ev_committed = 'X'.

    CATCH cx_root INTO lx_root.
      CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
      CLEAR ev_committed.
      lv_text = lx_root->get_text( ).
      add_error '099' 'PRICE_CRITICAL_EXCEPTION' lv_text 0 space.
      READ TABLE to_bapiret2 INTO ls_ret INDEX lines( to_bapiret2 ).
      ls_ret-message_v1 = cl_abap_classdescr=>get_class_name( lx_root ).
      MODIFY to_bapiret2 FROM ls_ret INDEX lines( to_bapiret2 ).
  ENDTRY.

ENDFUNCTION.