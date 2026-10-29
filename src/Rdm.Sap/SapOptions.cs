namespace Rdm.Sap;

/// <summary>Cấu hình kết nối SAP (section "Sap").</summary>
public sealed class SapOptions
{
    public const string SectionName = "Sap";

    public string DestinationName { get; set; } = "DRF";
    public string AppServerHost { get; set; } = string.Empty;
    public string SystemNumber { get; set; } = "00";
    public string? SystemId { get; set; }
    public string Client { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Language { get; set; } = "EN";

    /// <summary>Tùy chọn — logon qua message server thay vì application server.</summary>
    public string? MessageServerHost { get; set; }
    public string? LogonGroup { get; set; }
    /// <summary>Tùy chọn — SAProuter string (/H/host/S/port/H/).</summary>
    public string? SapRouter { get; set; }

    public int PoolSize { get; set; } = 10;
    public int MaxPoolWaitTimeMs { get; set; } = 30_000;
    public int IdleTimeoutSeconds { get; set; } = 600;

    /// <summary>Số lần retry khi mở kết nối (trước khi gửi lệnh ghi — an toàn để retry).</summary>
    public int ConnectRetryCount { get; set; } = 2;
    /// <summary>Số dòng mỗi lần gọi RFC_READ_TABLE (đọc nhiều lần bằng ROWSKIPS nếu bảng lớn hơn).</summary>
    public int ReadTableBatchSize { get; set; } = 5000;

    /// <summary>
    /// Account group (KNA1-KTOKD) được tính là sold-to khi đồng bộ master data.
    /// Mặc định loại Z102 Bill-to, Z103 Payer, Z104 Ship-to, Z106 Sales Employee (trên SAP các khách này cũng có partner AG trỏ về chính nó).
    /// </summary>
    public string[] SoldToAccountGroups { get; set; } = { "Z101", "Z001", "Z002", "Z004", "Z005", "Z007", "Z108", "Z109" };

    /// <summary>Loại vật tư (MARA-MTART) được đồng bộ về DB local. Mặc định chỉ ZFNP (Finished Goods).</summary>
    public string[] MaterialTypes { get; set; } = { "ZFNP" };

    /// <summary>Quốc gia của plant (T001W-LAND1) được đồng bộ về DB local. Mặc định chỉ VN.</summary>
    public string[] PlantCountries { get; set; } = { "VN" };

    /// <summary>Chart of accounts dùng khi đồng bộ tài khoản GL (chỉ company code có T001-KTOPL này).</summary>
    public string ChartOfAccounts { get; set; } = "1000";

    /// <summary>Controlling area: đồng bộ cost center của CO area này; tài khoản GL lấy theo company code gán vào CO area này (TKA02).</summary>
    public string ControllingArea { get; set; } = "G100";

    /// <summary>Loại sale contract (AUART) API được phép tạo / sửa / duyệt. Mặc định ZVCQ VN Sales Contract, ZVCF VN Free Remain.</summary>
    public string[] ContractTypes { get; set; } = { "ZVCQ", "ZVCF" };

    /// <summary>Delivery block header đánh dấu hợp đồng chờ duyệt (TVLS). Mặc định 30 "Price Chg. Approval".</summary>
    public string ContractApprovalDeliveryBlock { get; set; } = "30";

    /// <summary>Billing block header đánh dấu hợp đồng chờ duyệt (TVFS). Mặc định 30 "Price Chg. Approval".</summary>
    public string ContractApprovalBillingBlock { get; set; } = "30";

    /// <summary>Hàm RFC ghi bảng giá (VK11 / VK12 qua BDC). Source: abap/Z_ESD015_PRICE_UPLOAD.abap.</summary>
    public string PriceConditionFunction { get; set; } = "Z_ESD015_PRICE_UPLOAD";

    /// <summary>
    /// Vỏ RFC của BAPI_PRICES_CONDITIONS (BAPI này không remote-enabled) — source abap/Z_ESD016_PRICES_CONDITIONS.abap.
    /// Dùng cho API /api/sd/prices-bapi.
    /// </summary>
    public string PriceBapiFunction { get; set; } = "Z_ESD016_PRICES_CONDITIONS";

    /// <summary>
    /// Số bảng giá gom vào 1 lần gọi Z_ESD016 (1 commit). Lô bị SAP từ chối → ghi lại từng bảng giá của lô để biết dòng lỗi.
    /// Vỏ RFC v4 giới hạn 1000 dòng TI_BAPICONDCT / lần gọi.
    /// </summary>
    public int PriceBapiChunkSize { get; set; } = 50;

    /// <summary>Hàm RFC đọc KNKK theo khách hàng / credit control area — source abap/Z_ESD017_READ_KNKK.abap.</summary>
    public string CustomerCreditFunction { get; set; } = "Z_ESD017_READ_KNKK";

    /// <summary>
    /// Nguồn đọc KNKK: Auto (mặc định) = dùng <see cref="CustomerCreditFunction"/> nếu đã có trên SAP, chưa có thì RFC_READ_TABLE;
    /// Function = luôn dùng hàm (chưa có → lỗi CREDIT_RFC_MISSING); ReadTable = luôn RFC_READ_TABLE.
    /// </summary>
    public string CustomerCreditSource { get; set; } = "Auto";

    /// <summary>Thời gian circuit breaker mở sau chuỗi lỗi kết nối.</summary>
    public int CircuitBreakSeconds { get; set; } = 30;
}
