REPORT  zcsd001_pricing_upld NO STANDARD PAGE HEADING
                              LINE-SIZE 255
                              LINE-COUNT 65
                              MESSAGE-ID zmm.

*Types/Internal table/Data Declaration
TYPES: BEGIN OF ty_upload,
       zoption(4) TYPE c,       "Option (1-3).
       vkorg  TYPE komg-vkorg,  "Sales Organization
       vtweg  TYPE komg-vtweg,  "Distribution Channel
       kunag  TYPE komg-kunag,  "Sold-to party
       kunwe  TYPE komg-kunwe,  "Ship-to party
       matnr  TYPE komg-matnr,  "Material Number
       inco1  TYPE komg-inco1,  "Incoterms (Part 1)
       kbetr(25) TYPE c,        "Rate
       konwa  TYPE konp-konwa,  "Rate unit
       kpein  TYPE konp-kpein,  "Condition pricing unit
       kmein  TYPE konp-kmein,  "Condition unit (UOM)
       datab(10) TYPE c,       "Validity start date of the condition record (DD.MM.YYYY)
       datbi(10) TYPE c,       "Validity end date of the condition record (DD.MM.YYYY)
*Begin ins by wcfoong ERDK911022 16/07/2019
*       konda TYPE komg-konda,   "Price Group
       vsbed TYPE komg-vsbed,   "Shipping Conditions
       werks TYPE komg-werks,   "Plant
       VKORGAU TYPE komg-VKORGAU,   "Plant
***       kdgrp TYPE komg-kdgrp,   "Customer Group
***       regio TYPE komg-regio, "Region
*End ins by wcfoong ERDK911022 16/07/2019
END OF ty_upload.
TYPES tty_upload TYPE STANDARD TABLE OF ty_upload.
DATA: gw_upload TYPE ty_upload.
DATA: gt_upload TYPE tty_upload.


TYPES: BEGIN OF ty_log,
       zno TYPE i, "row
       zoption(4) TYPE c,       "Option (1-3).
       vkorg  TYPE komg-vkorg,  "Sales Organization
       vtweg  TYPE komg-vtweg,  "Distribution Channel
       kunag  TYPE komg-kunag,  "Sold-to party
       kunwe  TYPE komg-kunwe,  "Ship-to party
       matnr  TYPE komg-matnr,  "Material Number
       inco1  TYPE komg-inco1,  "Incoterms (Part 1)
       kbetr  TYPE konp-kbetr,  "Rate
       konwa  TYPE konp-konwa,  "Rate unit
       kpein  TYPE konp-kpein,  "Condition pricing unit
       kmein  TYPE konp-kmein,  "Condition unit (UOM)
       krech  TYPE konp-krech,  "Calculation type for condition
       datab(10) TYPE c, "Validity start date of the condition record
       datbi(10) TYPE c, "Validity end date of the condition record
*Begin ins by wcfoong ERDK911022 16/07/2019
*       konda TYPE komg-konda,   "Price Group
       vsbed TYPE komg-vsbed,   "Shipping Conditions
       vsart TYPE komg-vsart,   "Ship Type
       werks TYPE komg-werks,   "Plant
       kdgrp TYPE komg-kdgrp,   "Customer Group
       regio TYPE komg-regio, "Region
*End ins by wcfoong ERDK911022 16/07/2019
       ztype(1)  TYPE c,       "Msg type
       zmsg(150) TYPE c,       "Error Message
END OF ty_log.
TYPES tty_log TYPE STANDARD TABLE OF ty_log.
DATA: gw_log TYPE ty_log.
DATA: gt_log TYPE tty_log.


*ALV
DATA: gt_fieldcat TYPE slis_t_fieldcat_alv,
      gw_fieldcat TYPE slis_fieldcat_alv,
      gt_layout   TYPE slis_layout_alv,
      gt_print    TYPE slis_print_alv,
      gv_print    TYPE slis_print_alv,
      wa_event    TYPE slis_alv_event,
      gt_events   TYPE slis_t_event.

*BDC
DATA: gt_bdcdata TYPE STANDARD TABLE OF bdcdata,
      gw_bdcdata TYPE bdcdata.

DATA: gv_ctype   TYPE char04,  "Ins by wcfoong ERDK911022 17/07/2019
      gv_error   TYPE i. "Ins by wcfoong ERDK911022 17/07/2019

CONSTANTS: c_x(1) TYPE c VALUE 'X',
           c_vk11(4) TYPE c VALUE 'VK11',
           c_vk12(4) TYPE c VALUE 'VK12'.

*---------------------------------------------------------------------*
* SELECTION-SCREEN                                                    *
*---------------------------------------------------------------------*
SELECTION-SCREEN : BEGIN OF BLOCK a WITH FRAME TITLE text-s02.
PARAMETERS : p_path LIKE rlgrap-filename OBLIGATORY.  "Upload file path
SELECTION-SCREEN END OF BLOCK a.


SELECTION-SCREEN: BEGIN OF BLOCK b1 WITH FRAME TITLE text-s03.
PARAMETERS :
***             rb_zlsp TYPE c RADIOBUTTON GROUP g1 DEFAULT 'X' , "ZLSP
***             rb_zlp1 TYPE c RADIOBUTTON GROUP g1,  "ZLP1  "Ins by wcfoong  ERDK911561 11/09/2019
***             rb_zlmp TYPE c RADIOBUTTON GROUP g1, "ZLMP
***             rb_zdis TYPE c RADIOBUTTON GROUP g1,  "ZDIS
***             rb_zld1 TYPE c RADIOBUTTON GROUP g1,  "ZLD1  "Ins by wcfoong  ERDK911561 11/09/2019
****Begin ins by wcfoong ERDK911022 17/07/2019
***             rb_zlsl TYPE c RADIOBUTTON GROUP g1,  "ZLSL
***
***             rb_zlcd TYPE c RADIOBUTTON GROUP g1,  "ZLCD
***             rb_zldl TYPE c RADIOBUTTON GROUP g1,  "ZLDL
***             rb_zlsd TYPE c RADIOBUTTON GROUP g1,  "ZLSD
***             rb_zlps TYPE c RADIOBUTTON GROUP g1,  "ZLPS
             rb_zvlp TYPE c RADIOBUTTON GROUP g1  DEFAULT 'X' ,  "zvlp
             rb_ZVDI TYPE c RADIOBUTTON GROUP g1,  "ZVDI
             rb_zmin TYPE c RADIOBUTTON GROUP g1 ,  "ZMIN
             rb_ZVIV TYPE c RADIOBUTTON GROUP g1.  "ZVIV

*End ins by wcfoong ERDK911022 17/07/2019

SELECTION-SCREEN: END OF BLOCK b1.


SELECTION-SCREEN: BEGIN OF BLOCK c1 WITH FRAME TITLE text-s01.
PARAMETERS : rb_cre TYPE c RADIOBUTTON GROUP g2 DEFAULT 'X', "Create
             rb_del TYPE c RADIOBUTTON GROUP g2 . "Delete
SELECTION-SCREEN: END OF BLOCK c1.


AT SELECTION-SCREEN ON VALUE-REQUEST FOR p_path.
  PERFORM f_get_file_name USING p_path.

*---------------------------------------------------------------------*
* Start Of Selection                                                  *
*---------------------------------------------------------------------*

START-OF-SELECTION.
  DATA: lv_answer(1) TYPE c. "ins by wcfoong ERDK911022 16/07/2019

  PERFORM popup_confirm CHANGING lv_answer. "ins by wcfoong ERDK911022 16/07/2019

  IF lv_answer EQ '1'. "ins by wcfoong ERDK911022 16/07/2019
    PERFORM f_read_file.
    PERFORM f_post.
    PERFORM f_report.
  ENDIF. "ins by wcfoong ERDK911022 16/07/2019

END-OF-SELECTION.

*&---------------------------------------------------------------------*
*&      Form  F_GET_FILE_NAME
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*      -->PI_PATH  text
*----------------------------------------------------------------------*
FORM f_get_file_name  USING    pi_path.


*Browse for local pc file path
  CALL FUNCTION 'KD_GET_FILENAME_ON_F4'
    EXPORTING
      static        = 'X'
    CHANGING
      file_name     = pi_path
    EXCEPTIONS
      mask_tolv_long.

  IF sy-subrc NE 0.

  ENDIF.

ENDFORM.                    " F_GET_FILE_NAME

*&---------------------------------------------------------------------*
*&      Form  F_READ_FILE
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*  -->  p1        text
*  <--  p2        text
*----------------------------------------------------------------------*
FORM f_read_file .

  DATA : lt_itab TYPE alsmex_tabline OCCURS 0 WITH HEADER LINE,
          l_idxcount(4) TYPE c,    "Row Num.
          l_col TYPE i VALUE 1,
          l_index TYPE i,
          l_count TYPE i.

  FIELD-SYMBOLS  <fs>.

  CLEAR: gw_upload,
         lt_itab.

  REFRESH: lt_itab,
           gt_upload.

  IF NOT p_path IS INITIAL.
*  Data Transfer from Excel File to an Internal Table.
* ( Raw internal table Structure: ROW, COL, VALUE)
* This function allows max field size of up to 50 chars.
    CALL FUNCTION 'ALSM_EXCEL_TO_INTERNAL_TABLE'
      EXPORTING
        filename                = p_path
        i_begin_col             = '1'
        i_begin_row             = '04'  "start read data from 4th row
        i_end_col               = '100'
        i_end_row               = '65536'
      TABLES
        intern                  = lt_itab
      EXCEPTIONS
        inconsistent_parameters = 1
        upload_ole              = 2
        OTHERS                  = 3.

    IF sy-subrc <> 0.
      MESSAGE i033 WITH 'Error reading upload file'.
    ENDIF.


*  Converting Raw Internal table to Internal table.
    CHECK NOT lt_itab[] IS INITIAL.
    SORT lt_itab BY row col.

    LOOP AT lt_itab.
      MOVE : lt_itab-row TO l_idxcount,
             lt_itab-col TO l_index.
      ASSIGN COMPONENT l_index OF STRUCTURE gw_upload TO <fs>.
      MOVE lt_itab-value TO <fs>.

      AT END OF row.
        APPEND gw_upload TO gt_upload.
        CLEAR  gw_upload.
      ENDAT.
    ENDLOOP.
  ENDIF.

ENDFORM.                    " F_READ_FILE

*&---------------------------------------------------------------------*
*&      Form  f_bdc_dynpro
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*      -->P_pogram   Progran name
*      -->P_dynpro   Screen no
*----------------------------------------------------------------------*
FORM f_bdc_dynpro  USING p_program TYPE bdc_prog
                         p_dynpro  TYPE bdc_dynr.

  CLEAR gw_bdcdata.
  gw_bdcdata-program  = p_program.
  gw_bdcdata-dynpro   = p_dynpro.
  gw_bdcdata-dynbegin = c_x.
  APPEND gw_bdcdata TO gt_bdcdata.

ENDFORM.                    " f_bdc_dynpro
*&---------------------------------------------------------------------*
*&      Form  f_bdc_field
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*      -->P_fnam   Field name
*      -->P_fval   Field value
*----------------------------------------------------------------------*
FORM f_bdc_field USING p_fnam TYPE fnam_____4
                       p_fval.

  CLEAR gw_bdcdata.
  gw_bdcdata-fnam  = p_fnam.
  gw_bdcdata-fval  = p_fval.
  APPEND gw_bdcdata TO gt_bdcdata.

ENDFORM.                    " f_bdc_field

*&---------------------------------------------------------------------*
*&      Form  F_REPORT
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*  -->  p1        text
*  <--  p2        text
*----------------------------------------------------------------------*
FORM f_report .

  DATA: lv_report TYPE sy-repid.

  lv_report = sy-repid.

  PERFORM f_build_fieldcat.

*To hide print info
  gv_print-no_print_listinfos = 'X'.

  gt_layout-colwidth_optimize = 'X'.

  CALL FUNCTION 'REUSE_ALV_GRID_DISPLAY'
    EXPORTING
      i_callback_program     = lv_report
      is_layout              = gt_layout
      i_callback_top_of_page = 'F_TOP_OF_PAGE'
      it_fieldcat            = gt_fieldcat
      i_save                 = 'A'
      is_print               = gv_print
    TABLES
      t_outtab               = gt_log
    EXCEPTIONS
      program_error          = 1
      OTHERS                 = 2.

  IF sy-subrc <> 0.
* Implement suitable error handling here
  ENDIF.

ENDFORM.                    " F_REPORT
*&---------------------------------------------------------------------*
*&      Form  F_BUILD_FIELDCAT
*&---------------------------------------------------------------------*
*       text
*----------------------------------------------------------------------*
*  -->  p1        text
*  <--  p2        text
*----------------------------------------------------------------------*
FORM f_build_fieldcat .

  DATA: lv_count TYPE i.

  CLEAR: lv_count,
         gt_fieldcat.

  REFRESH: gt_fieldcat.

*Begin mod by wcfoong ERDK911022 16/07/2019
  "Zno
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'ZNO'  'No.' '14'.

  "Pricing Upload Option
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'ZOPTION'  'Upload Option' '14'.

  "Message Type
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'ZTYPE'  'Message Type' '14'.

  "Error Message
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'ZMSG'  'Message' '100'.

  "Sales Organization
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'VKORG'  'Sales Organization' '14'.

  "Distribution Channel
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'VTWEG'  'Distribution Channel' '15'.

  "Sold-to party
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KUNAG'  'Sold-to party' '14'.

  "Ship-to party
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KUNWE'  'Ship-to party' '14'.

  "Material Number
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'MATNR'  'Material Number' '18'.

  "Incoterms (Part 1)
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'INCO1'  'Incoterms' '14'.

  "Rate
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KBETR'  'Rate' '25'.

  "Rate unit
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KONWA'  'Rate unit' '10'.

  "Condition pricing unit
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KPEIN'  'Condition pricing unit' '20'.

  "Condition unit (UOM)
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KMEIN'  'Condition unit (UOM)' '25'.

  "Validity start date of the condition record
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'DATAB'  'Valid From' '12'.

  "Validity end date of the condition record
  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'DATBI'  'Valid To' '12'.

*  lv_count = lv_count + 1.
*  PERFORM f_build_fieldcat1 USING lv_count 'KONDA'  'Price Group' '12'.

  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'VSBED'  'Shipping Conditions' '12'.

  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'VSART'  'Ship Type' '12'.

  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'WERKS'  'Plant' '12'.

  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'KDGRP'  'Customer Group' '12'.

  lv_count = lv_count + 1.
  PERFORM f_build_fieldcat1 USING lv_count 'REGIO'  'Region' '12'.


*End mod by wcfoong ERDK911022 16/07/2019

ENDFORM. 