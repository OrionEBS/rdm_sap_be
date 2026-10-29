*&---------------------------------------------------------------------*
*& Function Z_ESD016_PRICES_CONDITIONS  (Remote-Enabled Module)
*&---------------------------------------------------------------------*
*& Vỏ RFC cho BAPI_PRICES_CONDITIONS — BAPI này KHÔNG remote-enabled trên
*& DRF-930 và KHÔNG kiểm tra master data / chồng hiệu lực (là cổng nhập
*& hàng loạt CND_PRICES_INBOUND: đã tạo được bảng giá với đơn vị "ZZZ").
*&
*& Nguyên tắc thận trọng — kiểm tra TUẦN TỰ, bước nào lỗi thì dừng, KHÔNG gọi BAPI:
*&   1. Quyền: S_TCODE VK11 (có dòng 009) / VK12 (có dòng 004 / 003).
*&   2. Master data tồn tại: trường khóa trong VARKEY (giải mã theo T681E +
*&      DDIC), đơn vị (T006), tiền tệ (TCURC), release status (trống / A).
*&   3. Chồng hiệu lực / trùng lắp: với bảng giá có sẵn cùng khóa (bỏ qua bản
*&      đã đánh dấu xóa và chính KNUMH đang sửa) + giữa các dòng trong lần gọi.
*&   4. Gọi BAPI → có E/A hoặc IV_TESTRUN = 'X' → ROLLBACK, ngược lại COMMIT.
*& Toàn bộ trong TRY ... CATCH cx_root: lỗi bất ngờ → ROLLBACK + message E, không dump.
*&
*& Message lỗi của vỏ RFC: TO_BAPIRET2 TYPE = 'E', ID = 'ZRDM',
*&   PARAMETER = mã lỗi (để API đọc), ROW = dòng TI_BAPICONDCT / TI_BAPICONDIT, FIELD = trường.
*&   001 PRICE_NO_AUTHORITY        002 PRICE_INVALID_MASTER_DATA
*&   003 PRICE_INVALID_RELEASE     004 PRICE_OVERLAP
*&   005 PRICE_DUPLICATE_IN_REQUEST 006 PRICE_INVALID_TABLE
*&   099 PRICE_UNEXPECTED_ERROR
*&---------------------------------------------------------------------*
FUNCTION z_esd016_prices_conditions.
*"----------------------------------------------------------------------
*"*"Local Interface:
*"  IMPORTING
*"     VALUE(PI_INITIALMODE) TYPE  CHAR1 OPTIONAL
*"     VALUE(PI_BLOCKNUMBER) TYPE  CHAR10 OPTIONAL
*"     VALUE(PI_PHYSICAL_DELETION) TYPE  CHAR1 OPTIONAL
*"     VALUE(IV_TESTRUN) TYPE  CHAR1 OPTIONAL
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

  TYPES: BEGIN OF ty_field,
           row   TYPE i,
           name  TYPE fieldname,
           value TYPE char100,
         END OF ty_field,
         BEGIN OF ty_layout,
           kotabnr TYPE kotabnr,
           name    TYPE fieldname,
           offset  TYPE i,
           leng    TYPE i,
         END OF ty_layout,
         BEGIN OF ty_hit,
           knumh TYPE knumh,
           datab TYPE kodatab,
           datbi TYPE kodatbi,
         END OF ty_hit.

  DATA: ls_ret      TYPE bapiret2,
        lv_error    TYPE c,
        lx_root     TYPE REF TO cx_root,
        lv_text     TYPE string,
        lv_msg      TYPE bapi_msg,
        ls_ct       TYPE bapicondct,
        ls_ct2      TYPE bapicondct,
        ls_it       TYPE bapicondit,
        lv_row      TYPE i,
        lv_row2     TYPE i,
        lv_need_vk11 TYPE c,
        lv_need_vk12 TYPE c,
        lt_layout   TYPE STANDARD TABLE OF ty_layout,
        ls_layout   TYPE ty_layout,
        lt_t681e    TYPE STANDARD TABLE OF t681e,
        ls_t681e    TYPE t681e,
        lt_dfies    TYPE STANDARD TABLE OF dfies,
        ls_dfies    TYPE dfies,
        lv_tabname  TYPE ddobjname,
        lv_offset   TYPE i,
        lt_fields   TYPE STANDARD TABLE OF ty_field,
        ls_field    TYPE ty_field,
        lv_table    TYPE tabname,
        lv_column   TYPE fieldname,
        lv_extra    TYPE string,
        lv_where    TYPE string,
        lv_value    TYPE string,
        lv_dummy    TYPE char100,
        lv_found    TYPE c,
        lv_label    TYPE string,
        lv_vkey1    TYPE char100,
        lv_vkey2    TYPE char100,
        lv_kfrst_off TYPE i,
        lt_hits     TYPE STANDARD TABLE OF ty_hit,
        ls_hit      TYPE ty_hit,
        lv_loevm    TYPE loevm_ko,
        lv_from     TYPE char10,
        lv_to       TYPE char10,
        lt_del      TYPE STANDARD TABLE OF knumh.

  FIELD-SYMBOLS: <lv_any> TYPE any.

* Thêm 1 message lỗi: &1 số, &2 mã (PARAMETER), &3 text, &4 dòng, &5 field
  DEFINE add_error.
    CLEAR ls_ret.
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
* 1. Quyền
*----------------------------------------------------------------------
      LOOP AT ti_bapicondct INTO ls_ct.
        IF ls_ct-operation = '009'.
          lv_need_vk11 = 'X'.
        ELSE.
          lv_need_vk12 = 'X'.
        ENDIF.
      ENDLOOP.
      IF lv_need_vk11 IS NOT INITIAL.
        AUTHORITY-CHECK OBJECT 'S_TCODE' ID 'TCD' FIELD 'VK11'.
        IF sy-subrc <> 0.
          add_error '001' 'PRICE_NO_AUTHORITY' 'User RFC không có quyền tạo bảng giá (VK11).' 0 space.
        ENDIF.
      ENDIF.
      IF lv_need_vk12 IS NOT INITIAL.
        AUTHORITY-CHECK OBJECT 'S_TCODE' ID 'TCD' FIELD 'VK12'.
        IF sy-subrc <> 0.
          add_error '001' 'PRICE_NO_AUTHORITY' 'User RFC không có quyền sửa bảng giá (VK12).' 0 space.
        ENDIF.
      ENDIF.

*----------------------------------------------------------------------
* 2. Master data
*----------------------------------------------------------------------
*       Dòng hủy hiệu lực (INDIDELETE = X) không kiểm tra master data / chồng hiệu lực — luôn cho hủy.
      LOOP AT ti_bapicondit INTO ls_it WHERE indidelete = 'X'.
        APPEND ls_it-cond_no TO lt_del.
      ENDLOOP.
      IF to_bapiret2[] IS INITIAL.
*       2a. Giải mã VARKEY theo danh mục trường T681E + độ dài DDIC (như SAP GET_VAKEY_OLD)
        LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
          lv_row = sy-tabix.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
          CHECK sy-subrc <> 0.                              " dòng hủy hiệu lực: không chặn
          READ TABLE lt_layout TRANSPORTING NO FIELDS WITH KEY kotabnr = ls_ct-table_no.
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
            CLEAR lv_offset.
            LOOP AT lt_t681e INTO ls_t681e.
              READ TABLE lt_dfies INTO ls_dfies WITH KEY fieldname = ls_t681e-sefeld.
              IF sy-subrc <> 0.
                EXIT.
              ENDIF.
              ls_layout-kotabnr = ls_ct-table_no.
              ls_layout-name    = ls_t681e-sefeld.
              ls_layout-offset  = lv_offset.
              ls_layout-leng    = ls_dfies-leng.
              APPEND ls_layout TO lt_layout.
              lv_offset = lv_offset + ls_dfies-leng.
            ENDLOOP.
          ENDIF.

          LOOP AT lt_layout INTO ls_layout WHERE kotabnr = ls_ct-table_no.
            ls_field-row   = lv_row.
            ls_field-name  = ls_layout-name.
            ls_field-value = ls_ct-varkey+ls_layout-offset(ls_layout-leng).
            APPEND ls_field TO lt_fields.
          ENDLOOP.
        ENDLOOP.

*       2b. Từng trường khóa → bảng kiểm tra
        LOOP AT lt_fields INTO ls_field.
          CLEAR: lv_table, lv_column, lv_extra, lv_label.
          CASE ls_field-name.
            WHEN 'KFRST'.
              IF ls_field-value IS NOT INITIAL AND ls_field-value <> 'A'.
                CONCATENATE 'Release status' ls_field-value 'không hợp lệ (chỉ trống hoặc A).' INTO lv_text SEPARATED BY space.
                add_error '003' 'PRICE_INVALID_RELEASE' lv_text ls_field-row 'KFRST'.
              ENDIF.
              CONTINUE.
            WHEN 'VKORG' OR 'VKORGAU'.
              lv_table = 'TVKO'.  lv_column = 'VKORG'. lv_label = 'Sales org'.
            WHEN 'VTWEG'.
              lv_table = 'TVTW'.  lv_column = 'VTWEG'. lv_label = 'Distribution channel'.
            WHEN 'WERKS'.
              lv_table = 'T001W'. lv_column = 'WERKS'. lv_label = 'Plant'.
            WHEN 'VSBED'.
              lv_table = 'TVSB'.  lv_column = 'VSBED'. lv_label = 'Shipping condition'.
            WHEN 'INCO1'.
              lv_table = 'TINC'.  lv_column = 'INCO1'. lv_label = 'Incoterms'.
            WHEN 'REGIO'.
              lv_table = 'T005S'. lv_column = 'BLAND'. lv_label = 'Region'.
              lv_extra = ` AND LAND1 = 'VN'`.
            WHEN 'MATNR'.
              lv_table = 'MARA'.  lv_column = 'MATNR'. lv_label = 'Material'.
            WHEN 'KUNAG' OR 'KUNWE' OR 'KUNNR'.
              lv_table = 'KNA1'.  lv_column = 'KUNNR'. lv_label = 'Customer'.
            WHEN OTHERS.
              CONTINUE.   " trường khác: không có bảng kiểm tra đã biết
          ENDCASE.
          IF ls_field-value IS INITIAL.
            CONCATENATE lv_label '(' ls_field-name ') trống.' INTO lv_text SEPARATED BY space.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
            CONTINUE.
          ENDIF.
          lv_value = ls_field-value.
          REPLACE ALL OCCURRENCES OF '''' IN lv_value WITH ''''''.
          CONCATENATE lv_column ' = ''' lv_value '''' lv_extra INTO lv_where.
          CLEAR lv_dummy.
          SELECT SINGLE (lv_column) FROM (lv_table) INTO lv_dummy WHERE (lv_where).
          IF sy-subrc <> 0.
            CONCATENATE lv_label '''' ls_field-value ''' không tồn tại trên SAP (' lv_table ').' INTO lv_text.
            add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text ls_field-row ls_field-name.
          ENDIF.
        ENDLOOP.

*       2c. Đơn vị / tiền tệ của dòng điều kiện
        LOOP AT ti_bapicondit INTO ls_it WHERE operation <> '003' AND indidelete IS INITIAL.
          lv_row = sy-tabix.
          IF ls_it-cond_unit IS NOT INITIAL.
            SELECT SINGLE msehi FROM t006 INTO lv_dummy WHERE msehi = ls_it-cond_unit.
            IF sy-subrc <> 0.
              CONCATENATE 'Đơn vị ''' ls_it-cond_unit ''' không tồn tại trên SAP (T006).' INTO lv_text.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'COND_UNIT'.
            ENDIF.
          ENDIF.
          IF ls_it-condcurr IS NOT INITIAL AND ls_it-condcurr <> '%'.
            SELECT SINGLE waers FROM tcurc INTO lv_dummy WHERE waers = ls_it-condcurr.
            IF sy-subrc <> 0.
              CONCATENATE 'Tiền tệ ''' ls_it-condcurr ''' không tồn tại trên SAP (TCURC).' INTO lv_text.
              add_error '002' 'PRICE_INVALID_MASTER_DATA' lv_text lv_row 'CONDCURR'.
            ENDIF.
          ENDIF.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* 3. Chồng hiệu lực / trùng lắp (chỉ khi master data hợp lệ)
*----------------------------------------------------------------------
      IF to_bapiret2[] IS INITIAL.
*       3a. Giữa các dòng trong lần gọi (khác KNUMH, cùng bảng / loại / khóa, chồng hiệu lực)
        LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
          lv_row = sy-tabix.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
          CHECK sy-subrc <> 0.                              " dòng hủy hiệu lực: không chặn
          LOOP AT ti_bapicondct INTO ls_ct2 WHERE operation <> '003'.
            lv_row2 = sy-tabix.
            READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct2-cond_no.
            CHECK sy-subrc <> 0.
            CHECK lv_row2 > lv_row.
            CHECK ls_ct2-cond_no <> ls_ct-cond_no OR ls_ct-cond_no(1) = '$'.
            CHECK ls_ct2-table_no = ls_ct-table_no AND ls_ct2-cond_type = ls_ct-cond_type
              AND ls_ct2-applicatio = ls_ct-applicatio.
*           So khóa không gồm KFRST (bản chờ duyệt và đã duyệt cùng khóa vẫn là trùng)
            lv_vkey1 = ls_ct-varkey.
            lv_vkey2 = ls_ct2-varkey.
            CLEAR lv_kfrst_off.
            LOOP AT lt_layout INTO ls_layout WHERE kotabnr = ls_ct-table_no AND name = 'KFRST'.
              lv_kfrst_off = ls_layout-offset.
              lv_vkey1+lv_kfrst_off(1) = space.
              lv_vkey2+lv_kfrst_off(1) = space.
            ENDLOOP.
            CHECK lv_vkey1 = lv_vkey2.
            CHECK ls_ct-valid_from <= ls_ct2-valid_to AND ls_ct2-valid_from <= ls_ct-valid_to.
            WRITE lv_row TO lv_from NO-GROUPING LEFT-JUSTIFIED.
            CONCATENATE 'Trùng khóa và chồng hiệu lực với dòng' lv_from 'trong cùng lần gọi.' INTO lv_text SEPARATED BY space.
            add_error '005' 'PRICE_DUPLICATE_IN_REQUEST' lv_text lv_row2 'VALID_FROM'.
          ENDLOOP.
        ENDLOOP.

*       3b. Với bảng giá có sẵn (đọc thẳng bảng Axxx theo trường khóa, bỏ KFRST)
        LOOP AT ti_bapicondct INTO ls_ct WHERE operation <> '003'.
          lv_row = sy-tabix.
          READ TABLE lt_del TRANSPORTING NO FIELDS WITH KEY table_line = ls_ct-cond_no.
          CHECK sy-subrc <> 0.                              " dòng hủy hiệu lực: không chặn
          CONCATENATE ls_ct-cond_usage ls_ct-table_no INTO lv_table.
          CONCATENATE 'KAPPL = ''' ls_ct-applicatio ''' AND KSCHL = ''' ls_ct-cond_type '''' INTO lv_where.
          LOOP AT lt_fields INTO ls_field WHERE row = lv_row.
            CHECK ls_field-name <> 'KFRST'.
            lv_value = ls_field-value.
            REPLACE ALL OCCURRENCES OF '''' IN lv_value WITH ''''''.
            CONCATENATE lv_where ' AND ' ls_field-name ' = ''' lv_value '''' INTO lv_where RESPECTING BLANKS.
          ENDLOOP.
          CONCATENATE lv_where ' AND DATAB <= ''' ls_ct-valid_to ''' AND DATBI >= ''' ls_ct-valid_from '''' INTO lv_where RESPECTING BLANKS.

          REFRESH lt_hits.
          SELECT knumh datab datbi FROM (lv_table) INTO CORRESPONDING FIELDS OF TABLE lt_hits WHERE (lv_where).
          LOOP AT lt_hits INTO ls_hit.
            CHECK ls_hit-knumh <> ls_ct-cond_no.          " chính record đang sửa
            CLEAR lv_loevm.
            SELECT SINGLE loevm_ko FROM konp INTO lv_loevm
              WHERE knumh = ls_hit-knumh AND kopos = '01'.
            CHECK lv_loevm IS INITIAL.                    " bỏ qua bản đã đánh dấu xóa
            WRITE ls_hit-datab TO lv_from DD/MM/YYYY.
            WRITE ls_hit-datbi TO lv_to DD/MM/YYYY.
            CONCATENATE 'Chồng hiệu lực với bảng giá' ls_hit-knumh '(' lv_from '-' lv_to ').' INTO lv_text SEPARATED BY space.
            add_error '004' 'PRICE_OVERLAP' lv_text lv_row 'VALID_FROM'.
          ENDLOOP.
        ENDLOOP.
      ENDIF.

*----------------------------------------------------------------------
* Có lỗi kiểm tra → không gọi BAPI
*----------------------------------------------------------------------
      IF to_bapiret2[] IS NOT INITIAL.
        CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
        RETURN.
      ENDIF.

*----------------------------------------------------------------------
* 4. Gọi BAPI
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
          ls_ret-message   = 'BAPI_PRICES_CONDITIONS báo lỗi không có message.'.
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
*     Lỗi bất ngờ (lỗi runtime bắt được, SQL động...) → hủy LUW, trả message thay vì dump.
      CALL FUNCTION 'BAPI_TRANSACTION_ROLLBACK'.
      CLEAR ev_committed.
*     Message an toàn: không đưa text exception ra ngoài; MESSAGE_V1 = tên lớp exception để tra ST22 / SLG1.
      add_error '099' 'PRICE_UNEXPECTED_ERROR' 'Lỗi không lường trước khi xử lý bảng giá — đã rollback, chưa ghi SAP.' 0 space.
      READ TABLE to_bapiret2 INTO ls_ret INDEX lines( to_bapiret2 ).
      ls_ret-message_v1 = cl_abap_classdescr=>get_class_name( lx_root ).
      MODIFY to_bapiret2 FROM ls_ret INDEX lines( to_bapiret2 ).
  ENDTRY.

ENDFUNCTION.
