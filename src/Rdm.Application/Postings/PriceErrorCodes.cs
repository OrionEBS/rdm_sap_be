namespace Rdm.Application.Postings;

/// <summary>
/// Mã lỗi / cảnh báo theo từng dòng bảng giá — nằm ở <see cref="SapReturnMessage.Number"/>, <see cref="SapReturnMessage.Item"/> = số dòng
/// (create / change: thứ tự 1..n trong request; approve: KNUMH). Request có dòng lỗi → ErrorCode = PRICE_VALIDATION, HTTP 422, không ghi SAP.
/// </summary>
public static class PriceErrorCodes
{
    /// <summary>Mã lỗi chung của request khi có ít nhất 1 dòng lỗi kiểm tra.</summary>
    public const string Validation = "PRICE_VALIDATION";

    /// <summary>Loại điều kiện không thuộc ZVLP / ZVDI / ZMIN / ZVIV.</summary>
    public const string InvalidConditionType = "PRICE_INVALID_CONDITION_TYPE";
    /// <summary>Thiếu trường khóa bắt buộc của bảng điều kiện, hoặc truyền trường không thuộc bảng.</summary>
    public const string InvalidKey = "PRICE_INVALID_KEY";
    /// <summary>Hiệu lực không hợp lệ (đến &lt; từ).</summary>
    public const string InvalidPeriod = "PRICE_INVALID_PERIOD";
    /// <summary>Giá sai dấu (ZVDI phải âm, loại khác phải dương) hoặc bằng 0.</summary>
    public const string InvalidAmount = "PRICE_INVALID_AMOUNT";
    /// <summary>2 dòng trong cùng request trùng khóa và chồng hiệu lực.</summary>
    public const string DuplicateInRequest = "PRICE_DUPLICATE_IN_REQUEST";
    /// <summary>Đã có bảng giá cùng khóa, cùng nguyên kỳ hiệu lực (dùng API sửa thay vì tạo).</summary>
    public const string SamePeriodExists = "PRICE_SAME_PERIOD_EXISTS";
    /// <summary>Chồng hiệu lực với bảng giá đang chờ duyệt (KFRST = A) cùng khóa — duyệt hoặc sửa bản đó trước.</summary>
    public const string OverlapPending = "PRICE_OVERLAP_PENDING";
    /// <summary>Chồng hiệu lực với bảng giá đã duyệt cùng khóa (OverlapMode = Reject).</summary>
    public const string Overlap = "PRICE_OVERLAP";
    /// <summary>Cảnh báo (OverlapMode = Replace): bảng giá đã duyệt sẽ bị SAP cắt / tách / thay khi tạo bảng giá mới.</summary>
    public const string WillAdjustExisting = "PRICE_WILL_ADJUST_EXISTING";
    /// <summary>Dòng sửa không có field nào thay đổi.</summary>
    public const string NoChange = "PRICE_NO_CHANGE";
    /// <summary>Không tìm thấy condition record.</summary>
    public const string NotFound = "PRICE_NOT_FOUND";
    /// <summary>Condition record đã bị đánh dấu xóa.</summary>
    public const string Deleted = "PRICE_DELETED";
    /// <summary>Condition record không thuộc ZVLP / ZVDI / ZMIN / ZVIV.</summary>
    public const string NotSupported = "PRICE_NOT_SUPPORTED";
    /// <summary>Đổi hiệu lực đến của record đã bị SAP tách nhiều kỳ mà không truyền PeriodStart (hoặc PeriodStart không khớp kỳ nào).</summary>
    public const string SplitRecord = "PRICE_SPLIT_RECORD";
    /// <summary>Thông tin (approve): record đã duyệt — bỏ qua.</summary>
    public const string AlreadyApproved = "PRICE_ALREADY_APPROVED";
    /// <summary>Copy theo VAT: không có bảng giá nguồn khớp bộ lọc.</summary>
    public const string SourceNotFound = "PRICE_SOURCE_NOT_FOUND";
    /// <summary>Copy theo VAT: quá nhiều bảng giá nguồn — thu hẹp bộ lọc.</summary>
    public const string TooManySources = "PRICE_TOO_MANY_SOURCES";
    /// <summary>Thành công từng dòng (create: MessageV1 = KNUMH mới).</summary>
    public const string Ok = "PRICE_OK";
    /// <summary>SAP ghi được một phần (Z_ESD015 commit từng dòng): xem PRICE_OK / lỗi từng dòng; gửi lại chỉ các dòng lỗi.</summary>
    public const string Partial = "PRICE_PARTIAL";
    /// <summary>Hàm RFC Z_ESD015_PRICE_UPLOAD / Z_ESD016_PRICES_CONDITIONS chưa có trên SAP.</summary>
    public const string FunctionMissing = "PRICE_RFC_MISSING";
    /// <summary>Thông tin (gỡ duyệt): record đang chờ duyệt — bỏ qua.</summary>
    public const string AlreadyPending = "PRICE_ALREADY_PENDING";
    /// <summary>Thông tin (hủy hiệu lực): record đã bị đánh dấu xóa — bỏ qua.</summary>
    public const string AlreadyDeleted = "PRICE_ALREADY_DELETED";
    /// <summary>SAP báo thành công nhưng đọc lại không thấy trạng thái mới (duyệt / gỡ duyệt / hủy hiệu lực).</summary>
    public const string NotApplied = "PRICE_NOT_APPLIED";
    /// <summary>Record lỗi phía SAP (BAPI trả E/A) — chi tiết ở message SAP cùng Item (Id / Number = mã lỗi SAP).</summary>
    public const string SapError = "PRICE_SAP_ERROR";
    /// <summary>
    /// Mã master data không tồn tại trên SAP (đơn vị, tiền tệ, sales org, kênh, plant, shipping condition, incoterms, region,
    /// vật tư, khách hàng). BAPI_PRICES_CONDITIONS KHÔNG tự kiểm tra (khác VK11) nên API kiểm tra trước khi ghi.
    /// </summary>
    public const string InvalidMasterData = "PRICE_INVALID_MASTER_DATA";
    /// <summary>Chưa xử lý do mất kết nối ở record trước.</summary>
    public const string NotProcessed = "PRICE_NOT_PROCESSED";
    /// <summary>
    /// Lỗi kỹ thuật khi đang kiểm tra (đọc SAP lỗi, dữ liệu không chuyển được sang kiểu SAP...) — chưa ghi SAP.
    /// Message chỉ có tên bước + mã tham chiếu; chi tiết exception nằm trong log (tìm theo mã tham chiếu).
    /// </summary>
    public const string ValidationError = "PRICE_VALIDATION_ERROR";
    /// <summary>Vỏ RFC Z_ESD016: user RFC thiếu quyền VK11 / VK12.</summary>
    public const string NoAuthority = "PRICE_NO_AUTHORITY";
    /// <summary>Vỏ RFC Z_ESD016: lỗi không lường trước (đã rollback, chưa ghi SAP).</summary>
    public const string UnexpectedError = "PRICE_UNEXPECTED_ERROR";
    /// <summary>Vỏ RFC Z_ESD016: release status (KFRST) trong VARKEY không phải trống / A.</summary>
    public const string InvalidRelease = "PRICE_INVALID_RELEASE";
    /// <summary>Vỏ RFC Z_ESD016: không đọc được cấu trúc khóa bảng điều kiện (T681E / DDIC).</summary>
    public const string InvalidTable = "PRICE_INVALID_TABLE";
    /// <summary>Vỏ RFC Z_ESD016 v3: KNUMH sửa / xóa kỳ không khớp loại / bảng / khóa truyền vào.</summary>
    public const string KeyMismatch = "PRICE_KEY_MISMATCH";
    /// <summary>Vỏ RFC Z_ESD016 v3: dữ liệu vào sai cấu trúc (OPERATION, số tạm / KNUMH, dòng HD / IT không khớp CT).</summary>
    public const string InvalidStructure = "PRICE_INVALID_STRUCTURE";
    /// <summary>Vỏ RFC Z_ESD016 v4: khóa không ở dạng nội bộ (MATNR 18 số có 0 đầu, KUNNR ALPHA) — message có giá trị đúng.</summary>
    public const string InvalidKeyFormat = "PRICE_INVALID_KEY_FORMAT";
}
