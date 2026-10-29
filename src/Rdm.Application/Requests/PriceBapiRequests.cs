using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Tạo 1 hoặc nhiều bảng giá qua BAPI_PRICES_CONDITIONS (OPERATION 009). Luôn chặn trùng / chồng hiệu lực với bảng giá
/// có sẵn cùng khóa (kể cả bản chờ duyệt) và giữa các dòng trong request. Kiểm tra lỗi 1 dòng → không ghi dòng nào.
/// Mỗi dòng 1 lần gọi BAPI + commit.
/// </summary>
public sealed class PriceBapiCreateRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>true = tạo ở trạng thái chờ duyệt (release status A); false = có hiệu lực ngay.</summary>
    public bool PendingApproval { get; set; }

    [Required, MinLength(1), MaxLength(PriceBapiLimits.BulkMaxItems)]
    public List<PriceCreateItem> Items { get; set; } = new();
}

/// <summary>Sửa 1 hoặc nhiều bảng giá theo KNUMH qua BAPI_PRICES_CONDITIONS (OPERATION 004). Chỉ field được truyền mới bị đổi.</summary>
public sealed class PriceBapiChangeRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(PriceBapiLimits.BulkMaxItems)]
    public List<PriceChangeItem> Items { get; set; } = new();
}

/// <summary>Danh sách bảng giá (KNUMH) cho hủy hiệu lực / duyệt / gỡ duyệt.</summary>
public sealed class PriceBapiRecordsRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Danh sách số condition record (KNUMH).</summary>
    /// <example>["0000119650"]</example>
    [Required, MinLength(1), MaxLength(PriceBapiLimits.BulkMaxItems)]
    public List<string> ConditionRecordNumbers { get; set; } = new();

    /// <summary>Người thực hiện / ghi chú trên RDM (lưu ở nhật ký API).</summary>
    [StringLength(100)]
    public string? ChangedBy { get; set; }
}

/// <summary>
/// Giới hạn số dòng / request bảng giá qua BAPI. Đồng bộ (POST /api/sd/prices-bapi[/...]) tối đa <see cref="SyncMaxItems"/>;
/// khai báo giá hàng loạt (đầu năm) dùng xử lý nền POST /api/sd/prices-bapi/jobs/... tối đa <see cref="BulkMaxItems"/>.
/// </summary>
public static class PriceBapiLimits
{
    public const int SyncMaxItems = 500;
    public const int BulkMaxItems = 20000;
}
