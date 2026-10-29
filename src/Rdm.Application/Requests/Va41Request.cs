using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Z_ESD013_CREATE_VA41 (structure ZSSD_VA41) — Free goods contract 1 item.
/// SAP gọi BAPI_CONTRACT_CREATEFROMDATA rồi tự COMMIT; chỉ chấp nhận loại hợp đồng ZVCF.
/// </summary>
public sealed class Va41Request : IPostingRequest, IValidatableObject
{
    /// <summary>Khóa idempotency do client sinh.</summary>
    /// <example>VA41-0001</example>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>AUART — Contract type (SAP chỉ nhận ZVCF).</summary>
    /// <example>ZVCF</example>
    [Required, StringLength(4)]
    public string ContractType { get; set; } = "ZVCF";

    /// <summary>VKORG — Sales organization.</summary>
    /// <example>G210</example>
    [Required, StringLength(4)]
    public string SalesOrganization { get; set; } = string.Empty;

    /// <summary>VTWEG — Distribution channel.</summary>
    /// <example>10</example>
    [Required, StringLength(2)]
    public string DistributionChannel { get; set; } = string.Empty;

    /// <summary>SPART — Division.</summary>
    /// <example>11</example>
    [Required, StringLength(2)]
    public string Division { get; set; } = string.Empty;

    /// <summary>KUNAG — Sold-to party.</summary>
    /// <example>100166</example>
    [Required, StringLength(10)]
    public string SoldTo { get; set; } = string.Empty;

    /// <summary>KUNWE — Ship-to party.</summary>
    /// <example>100166</example>
    [Required, StringLength(10)]
    public string ShipTo { get; set; } = string.Empty;

    /// <summary>BSTKD — Customer PO number.</summary>
    [StringLength(35)]
    public string? CustomerPoNumber { get; set; }

    /// <summary>GUEBG — Valid from.</summary>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <summary>GUEEN — Valid to.</summary>
    [Required]
    public DateOnly? ValidTo { get; set; }

    /// <summary>MATNR — Material.</summary>
    [Required, StringLength(18)]
    public string Material { get; set; } = string.Empty;

    /// <summary>ZMENG — Target quantity.</summary>
    [Range(typeof(decimal), "0.001", "9999999999.999")]
    public decimal Quantity { get; set; }

    /// <summary>ZIEME — Target quantity UoM (mã nội bộ SAP).</summary>
    [Required, StringLength(3)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>WERKS — Plant.</summary>
    [Required, StringLength(4)]
    public string Plant { get; set; } = string.Empty;

    /// <summary>VSTEL — Shipping point.</summary>
    [StringLength(4)]
    public string? ShippingPoint { get; set; }

    /// <summary>VSBED — Shipping conditions.</summary>
    /// <example>PK</example>
    [StringLength(2)]
    public string? ShippingCondition { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValidFrom is { } from && ValidTo is { } to && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });
    }
}
