using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>Chế độ xử lý khi bảng giá mới chồng hiệu lực với bảng giá đã duyệt (KFRST trống) cùng khóa.</summary>
public enum PriceOverlapMode
{
    /// <summary>Mọi chồng hiệu lực đều là lỗi PRICE_OVERLAP.</summary>
    Reject,
    /// <summary>
    /// Cho phép chồng với bảng giá đã duyệt: khi tạo, SAP (VK11) tự cắt / tách hiệu lực bản cũ (trả cảnh báo).
    /// Vẫn lỗi nếu chồng với bảng giá đang chờ duyệt hoặc trùng nguyên kỳ.
    /// </summary>
    Replace
}

/// <summary>
/// Khóa bảng giá. Trường cần truyền tùy loại điều kiện (bảng điều kiện trên DRF-930):
///   ZVLP / ZVDI → A990: SalesOrganization, DistributionChannel, SoldTo, ShipTo, Material, ShippingCondition, Plant
///   ZVIV        → A991: SalesOrganization (= sales org của đơn hàng, VKORGAU), Plant, Material
///   ZMIN        → A912 (khi có Incoterms): SalesOrganization, DistributionChannel, Material, Incoterms
///                 A919 (Region): CHỈ ĐỌC — tạo ZMIN chỉ theo A912 (nghiệp vụ / VK11 không dùng Region; DRF-930 không có region VN)
/// </summary>
public class PriceKeyInput
{
    /// <summary>KSCHL — ZVLP (giá niêm yết), ZVDI (chiết khấu), ZMIN (giá sàn), ZVIV (giá nội bộ).</summary>
    /// <example>ZVLP</example>
    [Required, StringLength(4)]
    public string ConditionType { get; set; } = string.Empty;

    /// <summary>VKORG (VKORGAU với ZVIV).</summary>
    /// <example>G210</example>
    [StringLength(4)]
    public string? SalesOrganization { get; set; }

    /// <summary>VTWEG.</summary>
    /// <example>10</example>
    [StringLength(2)]
    public string? DistributionChannel { get; set; }

    /// <summary>KUNAG — Sold-to.</summary>
    [StringLength(10)]
    public string? SoldTo { get; set; }

    /// <summary>KUNWE — Ship-to.</summary>
    [StringLength(10)]
    public string? ShipTo { get; set; }

    /// <summary>MATNR.</summary>
    [StringLength(18)]
    public string? Material { get; set; }

    /// <summary>VSBED — Shipping condition.</summary>
    [StringLength(2)]
    public string? ShippingCondition { get; set; }

    /// <summary>WERKS — Plant.</summary>
    [StringLength(4)]
    public string? Plant { get; set; }

    /// <summary>INCO1 — Incoterms (ZMIN theo A912).</summary>
    [StringLength(3)]
    public string? Incoterms { get; set; }

    /// <summary>REGIO — Region (ZMIN theo A919): chỉ dùng để đọc; tạo bảng giá ZMIN truyền Region → PRICE_INVALID_KEY.</summary>
    [StringLength(3)]
    public string? Region { get; set; }
}

/// <summary>1 bảng giá cần tạo.</summary>
public sealed class PriceCreateItem : PriceKeyInput
{
    /// <summary>
    /// KBETR — Giá theo dấu của SAP: ZVLP / ZMIN / ZVIV dương; ZVDI (chiết khấu) âm. Số thực (VND không có số lẻ).
    /// </summary>
    /// <example>1250000</example>
    public decimal Amount { get; set; }

    /// <summary>KONWA — Tiền tệ.</summary>
    [StringLength(5)]
    public string Currency { get; set; } = "VND";

    /// <summary>KPEIN — Pricing unit (giá cho bao nhiêu đơn vị).</summary>
    [Range(1, 99999)]
    public int PricingUnit { get; set; } = 1;

    /// <summary>KMEIN — Đơn vị tính (mã nội bộ, vd. TO, BAG).</summary>
    /// <example>TO</example>
    [Required, StringLength(3)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>DATAB — Hiệu lực từ.</summary>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <summary>DATBI — Hiệu lực đến (bỏ trống = 31.12.9999).</summary>
    public DateOnly? ValidTo { get; set; }
}

/// <summary>
/// Tạo 1 hoặc nhiều bảng giá (Z_ESD015_PRICE_UPLOAD, VK11). Bảng giá có hiệu lực ngay — việc duyệt đã làm trên RDM.
/// Kiểm tra lỗi 1 dòng → không tạo dòng nào. SAP ghi từng dòng (commit riêng): lỗi SAP ở vài dòng → PRICE_PARTIAL.
/// </summary>
public sealed class PriceCreateRequest : IPostingRequest
{
    public const int MaxItems = 200;

    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Xử lý chồng hiệu lực với bảng giá đã duyệt. Mặc định Reject.</summary>
    public PriceOverlapMode OverlapMode { get; set; } = PriceOverlapMode.Reject;

    [Required, MinLength(1), MaxLength(MaxItems)]
    public List<PriceCreateItem> Items { get; set; } = new();
}

/// <summary>Sửa 1 bảng giá (theo số condition record KNUMH, VK12). Chỉ field được truyền mới bị đổi. Không đổi được hiệu lực từ.</summary>
public sealed class PriceChangeItem
{
    /// <summary>KNUMH — Số condition record.</summary>
    /// <example>0000119650</example>
    [Required, StringLength(10)]
    public string ConditionRecordNumber { get; set; } = string.Empty;

    /// <summary>Giá mới (theo dấu SAP).</summary>
    public decimal? Amount { get; set; }

    [Range(1, 99999)]
    public int? PricingUnit { get; set; }

    [StringLength(3)]
    public string? Unit { get; set; }

    /// <summary>Hiệu lực đến mới.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>
    /// Ngày bắt đầu của kỳ cần đổi ValidTo — chỉ cần khi record đã bị SAP tách thành nhiều kỳ (xem GET /api/sd/prices).
    /// </summary>
    public DateOnly? PeriodStart { get; set; }
}

/// <summary>Sửa 1 hoặc nhiều bảng giá trong 1 giao dịch.</summary>
public sealed class PriceChangeRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    public PriceOverlapMode OverlapMode { get; set; } = PriceOverlapMode.Reject;

    [Required, MinLength(1), MaxLength(PriceCreateRequest.MaxItems)]
    public List<PriceChangeItem> Items { get; set; } = new();
}

/// <summary>
/// Duyệt 1 hoặc nhiều bảng giá đang chờ duyệt (KFRST = A, nhập tay trên SAP) — VK12 đặt processing status 02 như ZSD_VN_PRICE_APR.
/// Bảng giá tạo qua API đã có hiệu lực ngay nên không cần duyệt. Bản ghi đã duyệt → bỏ qua (không lỗi), nên gửi lại an toàn.
/// </summary>
public sealed class PriceApproveRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Danh sách KNUMH.</summary>
    [Required, MinLength(1), MaxLength(PriceCreateRequest.MaxItems)]
    public List<string> ConditionRecordNumbers { get; set; } = new();

    /// <summary>Người duyệt trên RDM (lưu ở nhật ký API).</summary>
    [StringLength(100)]
    public string? ApprovedBy { get; set; }
}

/// <summary>
/// Tạo bảng giá mới từ bảng giá cũ theo VAT%:
///   - Có OldVatPercent (giá đã gồm VAT, đổi thuế suất):  giá mới = giá cũ / (1 + OldVat%) × (1 + VatPercent%)
///   - Không có OldVatPercent (giá chưa VAT → gồm VAT):   giá mới = giá cũ × (1 + VatPercent%)
/// Giá được làm tròn theo RoundingUnit. Nguồn = bảng giá đã duyệt, còn hiệu lực tại SourceDate, khớp bộ lọc.
/// </summary>
public sealed class PriceCopyWithVatRequest : IPostingRequest, IValidatableObject
{
    public const int MaxSources = 200;

    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Bộ lọc nguồn: ConditionType + SalesOrganization bắt buộc; các trường khóa khác tùy chọn.</summary>
    [Required]
    public PriceKeyInput Source { get; set; } = new();

    /// <summary>Ngày lấy bảng giá nguồn (bỏ trống = ValidFrom - 1 ngày).</summary>
    public DateOnly? SourceDate { get; set; }

    /// <summary>Thuế suất VAT mới (%), vd. 10.</summary>
    /// <example>10</example>
    [Range(typeof(decimal), "0", "100")]
    public decimal VatPercent { get; set; }

    /// <summary>Thuế suất VAT đang có trong giá cũ (%), vd. 8. Bỏ trống = giá cũ chưa gồm VAT.</summary>
    /// <example>8</example>
    [Range(typeof(decimal), "0", "100")]
    public decimal? OldVatPercent { get; set; }

    /// <summary>Hiệu lực từ của bảng giá mới.</summary>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <summary>Hiệu lực đến (bỏ trống = giữ hiệu lực đến của bản nguồn).</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>Làm tròn giá mới tới bội số này (VND), vd. 1, 10, 100, 1000.</summary>
    [Range(typeof(decimal), "0.01", "1000000")]
    public decimal RoundingUnit { get; set; } = 1m;

    /// <summary>Mặc định Replace: bảng giá mới thay bảng giá cũ từ ValidFrom (SAP cắt hiệu lực bản cũ khi tạo).</summary>
    public PriceOverlapMode OverlapMode { get; set; } = PriceOverlapMode.Replace;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Source is null) yield break;
        if (string.IsNullOrWhiteSpace(Source.SalesOrganization))
            yield return new ValidationResult("Source.SalesOrganization bắt buộc.", new[] { nameof(Source) });
        if (ValidTo is { } to && ValidFrom is { } from && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });
    }
}
