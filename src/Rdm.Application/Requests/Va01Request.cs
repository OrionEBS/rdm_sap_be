using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Z_ESD014_CREATE_VA01 (structure ZSSD_VA01) — Credit memo request 1 item.
/// Phía SAP chỉ chấp nhận loại đơn ZVCR; giá đi qua condition ZVAD (VND).
/// </summary>
public sealed class Va01Request : IPostingRequest
{
    /// <summary>Khóa idempotency do client sinh.</summary>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>AUART — Sales document type (SAP chỉ nhận ZVCR).</summary>
    [Required, StringLength(4)]
    public string OrderType { get; set; } = "ZVCR";

    /// <summary>VKORG — Sales organization.</summary>
    [Required, StringLength(4)]
    public string SalesOrganization { get; set; } = string.Empty;

    /// <summary>VTWEG — Distribution channel.</summary>
    [Required, StringLength(2)]
    public string DistributionChannel { get; set; } = string.Empty;

    /// <summary>SPART — Division.</summary>
    [Required, StringLength(2)]
    public string Division { get; set; } = string.Empty;

    /// <summary>KUNAG — Sold-to party.</summary>
    [Required, StringLength(10)]
    public string SoldTo { get; set; } = string.Empty;

    /// <summary>KUNWE — Ship-to party.</summary>
    [Required, StringLength(10)]
    public string ShipTo { get; set; } = string.Empty;

    /// <summary>BSTKD — Customer PO number.</summary>
    [StringLength(35)]
    public string? CustomerPoNumber { get; set; }

    /// <summary>FKDAT — Billing date.</summary>
    [Required]
    public DateOnly? BillingDate { get; set; }

    /// <summary>AUGRU — Order reason.</summary>
    [StringLength(3)]
    public string? OrderReason { get; set; }

    /// <summary>TEXT_LINE — Header text (text id 0001).</summary>
    [StringLength(132)]
    public string? HeaderText { get; set; }

    /// <summary>MATNR — Material.</summary>
    [Required, StringLength(18)]
    public string Material { get; set; } = string.Empty;

    /// <summary>ZMENG — Target quantity.</summary>
    [Range(typeof(decimal), "0.001", "9999999999.999")]
    public decimal Quantity { get; set; }

    /// <summary>ZIEME — Target quantity UoM (mã nội bộ SAP, vd. KG, ST).</summary>
    [Required, StringLength(3)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>WERKS — Plant.</summary>
    [Required, StringLength(4)]
    public string Plant { get; set; } = string.Empty;

    /// <summary>VSTEL — Shipping point.</summary>
    [StringLength(4)]
    public string? ShippingPoint { get; set; }

    /// <summary>KBETR — Rate của condition ZVAD (VND).</summary>
    [Range(typeof(decimal), "0", "99999999999.99")]
    public decimal Rate { get; set; }
}
