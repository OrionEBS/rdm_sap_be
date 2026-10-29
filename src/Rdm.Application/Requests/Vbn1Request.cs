using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Z_ESD012_CREATE_VBN1 (structure ZSSD_VBN1) — tạo condition record free goods (VBN1) bằng batch input.
/// SAP chỉ chấp nhận loại điều kiện ZA00. Key combination theo KeyCombination:
///   1 = Sales org / Distr. channel / Customer / Material / Plant / Shipping cond. (cần Customer)
///   2 = Sales org / Distr. channel / Sales office / Customer group 3 / Material / Plant / Shipping cond. (cần SalesOffice + CustomerGroup3)
/// Thành công không có số chứng từ (condition record), chỉ có message "Condition records for Free goods saved".
/// </summary>
public sealed class Vbn1Request : IPostingRequest, IValidatableObject
{
    /// <summary>Khóa idempotency do client sinh.</summary>
    /// <example>VBN1-0001</example>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>KSCHL — Condition type (SAP chỉ nhận ZA00).</summary>
    /// <example>ZA00</example>
    [Required, StringLength(4)]
    public string ConditionType { get; set; } = "ZA00";

    /// <summary>SELKZ — Key combination: "1" (theo khách hàng) hoặc "2" (theo sales office + customer group 3).</summary>
    /// <example>1</example>
    [Required, RegularExpression("^[12]$", ErrorMessage = "KeyCombination phải là '1' hoặc '2'.")]
    public string KeyCombination { get; set; } = "1";

    /// <summary>VKORG — Sales organization.</summary>
    /// <example>G210</example>
    [Required, StringLength(4)]
    public string SalesOrganization { get; set; } = string.Empty;

    /// <summary>VTWEG — Distribution channel.</summary>
    /// <example>10</example>
    [Required, StringLength(2)]
    public string DistributionChannel { get; set; } = string.Empty;

    /// <summary>KUNNR — Customer (bắt buộc khi KeyCombination = 1).</summary>
    /// <example>100166</example>
    [StringLength(10)]
    public string? Customer { get; set; }

    /// <summary>VKBUR — Sales office (bắt buộc khi KeyCombination = 2).</summary>
    [StringLength(4)]
    public string? SalesOffice { get; set; }

    /// <summary>ZZKVGR3 — Customer group 3 (bắt buộc khi KeyCombination = 2).</summary>
    [StringLength(3)]
    public string? CustomerGroup3 { get; set; }

    /// <summary>ZZEXCLUSIVE — Exclusive (chỉ dùng khi KeyCombination = 2).</summary>
    public bool Exclusive { get; set; }

    /// <summary>DATAB — Valid from.</summary>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <summary>DATBI — Valid to.</summary>
    [Required]
    public DateOnly? ValidTo { get; set; }

    /// <summary>MATNR — Material.</summary>
    [Required, StringLength(18)]
    public string Material { get; set; } = string.Empty;

    /// <summary>WERKS — Plant.</summary>
    [Required, StringLength(4)]
    public string Plant { get; set; } = string.Empty;

    /// <summary>VSBED — Shipping conditions.</summary>
    /// <example>PK</example>
    [Required, StringLength(2)]
    public string ShippingCondition { get; set; } = string.Empty;

    /// <summary>DISPKNRMM — Free goods: minimum quantity.</summary>
    [Range(typeof(decimal), "0", "99999999.999")]
    public decimal MinimumQuantity { get; set; }

    /// <summary>DISPKNRNM — Free goods: free goods quantity ("from").</summary>
    [Range(typeof(decimal), "0.001", "99999999.999")]
    public decimal FreeGoodsQuantity { get; set; }

    /// <summary>KNRME — Đơn vị của minimum quantity / free goods quantity.</summary>
    [Required, StringLength(3)]
    public string QuantityUnit { get; set; } = string.Empty;

    /// <summary>DISPKNRZM — Free goods: additional quantity.</summary>
    [Range(typeof(decimal), "0", "99999999.999")]
    public decimal AdditionalQuantity { get; set; }

    /// <summary>KNREZ — Đơn vị của additional quantity.</summary>
    [StringLength(3)]
    public string? AdditionalQuantityUnit { get; set; }

    /// <summary>KNRRR — Calculation type (rule) cho free goods quantity, vd. 1.</summary>
    [Range(0, 999)]
    public int CalculationRule { get; set; }

    /// <summary>KNRDD — Free goods category (vd. 1 = inclusive, 2 = exclusive...).</summary>
    [Required, StringLength(1)]
    public string FreeGoodsCategory { get; set; } = string.Empty;

    /// <summary>KNRMAT — Material tặng kèm (bỏ trống = cùng material).</summary>
    [StringLength(18)]
    public string? FreeGoodsMaterial { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (KeyCombination == "1" && string.IsNullOrWhiteSpace(Customer))
            yield return new ValidationResult("KeyCombination = 1 cần Customer.", new[] { nameof(Customer) });
        if (KeyCombination == "2" && (string.IsNullOrWhiteSpace(SalesOffice) || string.IsNullOrWhiteSpace(CustomerGroup3)))
            yield return new ValidationResult("KeyCombination = 2 cần SalesOffice và CustomerGroup3.",
                new[] { nameof(SalesOffice), nameof(CustomerGroup3) });
        if (ValidFrom is { } from && ValidTo is { } to && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });
    }
}
