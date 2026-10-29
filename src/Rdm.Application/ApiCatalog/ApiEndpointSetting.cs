namespace Rdm.Application.ApiCatalog;

/// <summary>
/// 1 API trong bảng cấu hình rdm.ApiEndpoints. Khóa = "METHOD route-template" (VD "POST api/sd/prices-bapi/jobs/create").
/// IsActive = false → API không hiện trên Swagger và trả 404 như URL không tồn tại. IsLocked = luôn active (đăng nhập, quản trị).
/// </summary>
public class ApiEndpointSetting
{
    public string EndpointKey { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RouteTemplate { get; set; } = string.Empty;
    /// <summary>Nhóm = tên controller (VD PricesBapi, Fi, MasterData) — bật / tắt cả nhóm.</summary>
    public string GroupName { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    /// <summary>Mô tả NỘI BỘ (kỹ thuật, có chi tiết SAP) — tự lấy từ XML doc lúc khởi động; dùng cho tài liệu TS / Swagger nội bộ.</summary>
    public string? Description { get; set; }
    /// <summary>Tóm tắt cho ĐỐI TÁC (Swagger công khai) — admin soạn, không chứa thông tin kỹ thuật SAP. Trống = không hiện mô tả.</summary>
    public string? PublicSummary { get; set; }
    /// <summary>Mô tả chi tiết cho ĐỐI TÁC (Swagger công khai, markdown).</summary>
    public string? PublicDescription { get; set; }
    /// <summary>true = ẩn khỏi Swagger đối tác (API vẫn gọi được nếu active) — VD API chỉ dùng nội bộ.</summary>
    public bool PublicHidden { get; set; }
    public string? DocUpdatedBy { get; set; }
    public DateTime? DocUpdatedAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsLocked { get; set; }
    public string? Note { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Lần cuối API còn trong code (đồng bộ lúc khởi động). Cũ hơn lần khởi động gần nhất = API đã bị xóa khỏi code.</summary>
    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>API phát hiện trong code lúc khởi động.</summary>
public sealed record ApiEndpointInfo(
    string EndpointKey, string HttpMethod, string RouteTemplate, string GroupName, string ActionName, bool IsLocked, string? Description = null);

public interface IApiEndpointRepository
{
    Task<IReadOnlyList<ApiEndpointSetting>> GetAllAsync(CancellationToken ct);
    /// <summary>Thêm API mới (IsActive = defaultActive), cập nhật thông tin + LastSeenAtUtc của API còn trong code; API khóa luôn active.</summary>
    Task SyncAsync(IReadOnlyCollection<ApiEndpointInfo> endpoints, bool defaultActive, DateTime nowUtc, CancellationToken ct);
    /// <summary>Bật / tắt theo danh sách khóa và / hoặc nhóm; bỏ qua API khóa. Trả các khóa đã đổi.</summary>
    /// <summary>Lưu mô tả đối tác của 1 API. false = không có khóa.</summary>
    Task<bool> SetPublicDocAsync(string key, string? publicSummary, string? publicDescription, bool publicHidden, string updatedBy, DateTime nowUtc,
        CancellationToken ct);
    Task<IReadOnlyList<string>> SetActiveAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string> groups, bool isActive,
        string updatedBy, string? note, DateTime nowUtc, CancellationToken ct);
}
