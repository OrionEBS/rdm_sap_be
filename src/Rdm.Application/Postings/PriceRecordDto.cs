using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Rdm.Application.Postings;

/// <summary>1 cột của bảng giá: tên thuộc tính JSON, field SAP, nhãn.</summary>
public sealed record PriceColumn(string Name, string SapField, string Label);

/// <summary>Cấu trúc cột của 1 bảng điều kiện: cột khóa (khác nhau theo bảng) + cột chi tiết (Axxx + KONP).</summary>
public sealed record PriceTableLayout(
    string ConditionTable, string Description, IReadOnlyList<PriceColumn> KeyColumns, IReadOnlyList<PriceColumn> DetailColumns);

/// <summary>Kết quả đọc bảng giá: cấu trúc cột từng bảng điều kiện có trong kết quả + các dòng.</summary>
public sealed record PriceListResponse(
    string ConditionType,
    string SalesOrganization,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    int Count,
    IReadOnlyList<PriceTableLayout> Tables,
    IReadOnlyList<PriceRecordDto> Items);

/// <summary>
/// 1 kỳ hiệu lực bảng giá trên SAP (1 dòng bảng Axxx + KONP dòng 01).
/// Mỗi bảng điều kiện có cột khóa khác nhau: dòng chỉ có các cột khóa thuộc bảng của nó (xem PriceListResponse.Tables);
/// cột không thuộc bảng không xuất hiện trong JSON.
/// </summary>
public sealed record PriceRecordDto(
    string ConditionRecordNumber,
    string ConditionType,
    string ConditionTable,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> Key,
    string ReleaseStatus,
    bool PendingApproval,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    bool Deleted,
    decimal? Amount,
    string? Currency,
    int? PricingUnit,
    string? Unit)
{
    /// <summary>VKORG (A990 / A912 / A919) hoặc VKORGAU — sales org của đơn hàng (A991 / ZVIV).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SalesOrganization { get; init; }
    /// <summary>VTWEG.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DistributionChannel { get; init; }
    /// <summary>KUNAG (A990).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoldTo { get; init; }
    /// <summary>KUNWE (A990).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShipTo { get; init; }
    /// <summary>MATNR dạng SAP (18 ký tự).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Material { get; init; }
    /// <summary>Mã vật tư hiển thị (bỏ 0 đầu nếu toàn số).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaterialCode { get; init; }
    /// <summary>VSBED (A990).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShippingCondition { get; init; }
    /// <summary>WERKS (A990 / A991).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plant { get; init; }
    /// <summary>INCO1 (A912).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Incoterms { get; init; }
    /// <summary>REGIO (A919).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; init; }
    /// <summary>KBSTAT — processing status của condition record (vd. 01, 02).</summary>
    public string? ProcessingStatus { get; init; }
    /// <summary>KONP-KRECH — calculation type (C = theo số lượng, A = %...).</summary>
    public string? CalculationType { get; init; }
}

/// <summary>Bộ lọc đọc bảng giá. Bắt buộc: ConditionType, SalesOrganization, ValidFrom, ValidTo; còn lại tùy chọn.</summary>
public sealed class PriceQuery : IValidatableObject
{
    /// <summary>ZVLP / ZVDI (bảng A990), ZVIV (A991), ZMIN (A912 + A919).</summary>
    /// <example>ZVLP</example>
    [Required]
    public string ConditionType { get; set; } = string.Empty;

    /// <summary>Sales org (với ZVIV là sales org của đơn hàng — VKORGAU).</summary>
    /// <example>G210</example>
    [Required, StringLength(4)]
    public string SalesOrganization { get; set; } = string.Empty;

    /// <summary>Lấy các kỳ có hiệu lực giao với khoảng [ValidFrom, ValidTo].</summary>
    /// <example>2026-01-01</example>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <example>2026-12-31</example>
    [Required]
    public DateOnly? ValidTo { get; set; }

    [StringLength(2)]
    public string? DistributionChannel { get; set; }
    [StringLength(10)]
    public string? SoldTo { get; set; }
    [StringLength(10)]
    public string? ShipTo { get; set; }
    [StringLength(18)]
    public string? Material { get; set; }
    [StringLength(2)]
    public string? ShippingCondition { get; set; }
    [StringLength(4)]
    public string? Plant { get; set; }
    /// <summary>ZMIN: truyền Incoterms → chỉ đọc A912.</summary>
    [StringLength(3)]
    public string? Incoterms { get; set; }
    /// <summary>ZMIN: truyền Region → chỉ đọc A919.</summary>
    [StringLength(3)]
    public string? Region { get; set; }
    /// <summary>true = chỉ bản chờ duyệt (KFRST = A); false = chỉ bản đã duyệt; bỏ trống = tất cả.</summary>
    public bool? PendingApproval { get; set; }
    public bool IncludeDeleted { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValidFrom is { } from && ValidTo is { } to && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });
    }
}
