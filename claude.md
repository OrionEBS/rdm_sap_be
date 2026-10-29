
# Xây dựng backend api gọi SAP Ecc 6.0 

các API gọi BAPI có sẳn cần viết API để gọi 
1. Z_EFI011_CREATE_FB01 
2.  Z_EFI009_CREATE_FB75 
3 . BAPI_ACC_DOCUMENT_REV_POST
4 . Z_ESD014_CREATE_VA01
5. Z_ESD012_CREATE_VBN1
6 Z_ESD013_CREATE_VA41
7. Master data SD: Sold-to, Ship-to, Sales District, Sales Office, Shipping Condition, Sales Org, Plant, Payment Method, Vehicle Type, Customer Group 3

 Solto-to:  lấy mã kh, tẹn kh, name 1, name2, name3, mã số thuế , loại khách hàng, trạng thái chưa xóa. các bảng còn lại lấy đủ. dùng RFC_READ_TABLE để đọc bảng
 Mổi bảng tương ứng 1 API nhé 

8. RFC_READ_TABLE  đọc bảng KNKK ( KUNNR,KKBER,KLIMK,	KNKLI,SAUFT,	SKFOR,	SSOBL,	UEDAT,	ERNAM,	ERDAT,CTLPC,	KRAUS,	AEDAT	,AENAM	,CASHD	,CASHA,CASHC) lấy tất cả khách hàng

9. dùng RFC_READ_TABLE  đọc bảng KNKK  ( KUNNR,KKBER,KLIMK,	KNKLI,SAUFT,	SKFOR,	SSOBL,	UEDAT,	ERNAM,	ERDAT,CTLPC,	KRAUS,	AEDAT	,AENAM	,CASHD	,CASHA,CASHC) với tham số truyền vào là KUNNR,KKBER.

10. Lấy thông bảng Master data {Material , Material group, Material type, Division, Plant }. và bảng danh Material ( mã vật tư, tên vt tiếng anh, tiếng việt, đơn vị tính )  và từng bảng Material group, Material type, Division, Plant
Tách từng API 

11. lấy danh mục tài khoản BAPI_GL_ACC_GETLIST 
danh mục costcenter BAPI_COSTCENTER_GETLIST1;
lưu ý tài khoản chỉ lấy COA : 1000
Tương tự cost center chỉ lấy CO_AREA G100 

12. Viết API cho BAPI BAPI_MATERIAL_SAVEDATA 
mục tiêu sửa TAXM1 của bảng MLAN thay vì dùng MM17. truyền vào danh sách mã vật tư, giá trị TAXM1 trong bảng TAXCLASSIFICATIONS. 

13. Viết API cho BAPI BAPI_PRICES_CONDITIONS. 
-tạo 1 bảng giá , tạo nhiều bảng giá 
-sửa 1 bảng giá , sửa nhiều bảng giá
-duyệt 1 bảng giá, duyệt nhiều bảng giá. 
-tạo bảng giá mới dựa trên bảng giá cũ và tham số VAT% truyền vào 
-lấy bảng giá 

lưu ý tham số Condition type được truyền vào ZVLP, ZVDI, ZMIN, ZVIV 
Đảm bảo rằng kiểm tra bảng giá tồn tại có bị trùng lắp hay overlaping không , trả về mã lỗi đúng.

# thông tin db đích 

# Phân tích yêu cầu tích hợp  API SAP . dự án liên quan kế toán bán hàng
nội dung file Tafico_RDM-SAP_API_Business_Scope_v1.1_20261008_VN-EN_goivung.xlsx  sheet"Phạm vi - Scope". 
Hãy cùng phân tích nghiệp vụ từng yêu cầu đưa bảng câu hỏi theo từng yêu cầu 
Vai trò là SAP FI consultance hãy đưa câu hỏi nghiệp vụ liên quan kế toán bán hàng, các bút toán chi tiết
sau đó phân tích khả năng sử dụng lại các BAPI này   Z_EFI011_CREATE_FB01 
2.  Z_EFI009_CREATE_FB75 
3 . BAPI_ACC_DOCUMENT_REV_POST
4 . Z_ESD014_CREATE_VA01
5. Z_ESD012_CREATE_VBN1
6 Z_ESD013_CREATE_VA41
7. BAPI_ACC_DOCUMENT_POST 
8. Dùng hàm BAPI chuẩn của SAP trong việc tạo Sale contract các loại 

9. Xử lý bảng giá 
ZVDI( solto, shipto, material,shipping cont, plant, release ,status, valid from , valid to) 
ZVLP => a990 , detail  : KONP 
ZMIN A912  ( sale org, dis channel, region , material ) 
ZVIV : A991 ( Sale org, plant, material , release status, valid from , valid to ) 


Trả ra két quả phân tích : 
- sử dụng hàm BAPI
- cách làm trên SAP chi tiết postibg key, doctype, tài khoản, đối tượng. 
-Đưa ra những câu hỏi sâu về chuyên môn để loại trù rủi ro, sai sót nghiệp vụ.

 Trả ra tài liệu docx nhé



Thông tin dblocal :
ip: localhost , port 43310 
 user: rdmuser . Password: Tafico$2026 
DB name : trdm

khi lưu xuống DB phải đi qua procedure không ghi trực tiếp bảng 

Thông tin master data 
Sale org : G210, Sale division : 10
mã khách hàng test : 100166 
distribution channel : 10 
Division : 10,11



# Tech stack & ràng buộc quan trọng

thông tin kết nối SAP DEV930
IP 10.72.1.39 , instant : 01 , system number :DRF , client 930
user : tuanna 
pass : khangquan

| Thành phần       | Lựa chọn                                  |
|------------------|-------------------------------------------|
| Runtime/API      | ASP.NET Core Web API (.NET 8)             |
| Kết nối SAP      | SAP Connector for Microsoft .NET (NCo 3.1)|
| Database         | SQL Server (2019+)                        |
| Data access      | EF Core (schema + truy vấn) + `SqlBulkCopy` (ghi bulk khi sync) |
| API docs         | Swashbuckle (Swagger / OpenAPI)           |
| Lập lịch         | Hangfire (job store dùng chính SQL Server)|
| Logging          | Serilog (sinks: Console, File rolling, MSSqlServer) |
| Resilience       | Polly (retry/timeout/circuit-breaker quanh lời gọi SAP) |
| Test             | xUnit + FluentAssertions                  |

### ⚠️ Ràng buộc bắt buộc kiểm chứng trước khi code: SAP NCo runtime

SAP NCo 3.x **theo truyền thống chỉ hỗ trợ .NET Framework**. NCo 3.1 có bản hỗ trợ runtime mới hơn, nhưng phải xác nhận bản NCo cụ thái có trong tay trước khi quyết định target framework.

- **Trước khi tạo project**: kiểm tra DLL `sapnco.dll` / `sapnco_utils.dll` đang có → xác định bản hỗ trợ .NET Framework 4.x hay .NET 6+.
- **Nếu NCo chỉ chạy .NET Framework**: KHÔNG ép NCo vào tiến trình .NET 8. Dùng **bridge pattern**: tách phần gọi SAP thành một process riêng target .NET Framework, expose nội bộ qua một self-hosted `HttpListener` / named pipe; API .NET 8 gọi sang process bridge này. Đây là pattern đã được dùng thành công ở các dự án trước (tham chiếu `WBAPI_STO`).

# đóng gói và biên dịch 
Tạo các script đóng gói và biên dịch , trước khi làm tắt tiến trình đang mở
Tạo script start , restart , start với port chỉ định.
Bổ sung script đóng gói ở dạng chạy console 
