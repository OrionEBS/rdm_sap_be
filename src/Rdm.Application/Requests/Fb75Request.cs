using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Z_EFI009_CREATE_FB75 (structure ZSFI_FB75) — Customer credit memo.
/// SAP post 3 dòng qua BAPI_ACC_DOCUMENT_POST: khách hàng (-Amount), GL (TaxBaseAmount), thuế. Tiền tệ cố định VND.
/// </summary>
public sealed class Fb75Request : IPostingRequest
{
    /// <summary>Khóa idempotency do client sinh.</summary>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>BUKRS — Company code.</summary>
    [Required, StringLength(4)]
    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>BUSCS — Business transaction. Bắt buộc "G": F_CREATE_FB75 chỉ post khi BUSCS = 'G', giá trị khác SAP bỏ qua và trả RETURN rỗng.</summary>
    [Required, RegularExpression("^[Gg]$", ErrorMessage = "BusinessTransaction phải là 'G' (Z_EFI009_CREATE_FB75 chỉ xử lý BUSCS = 'G').")]
    public string BusinessTransaction { get; set; } = "G";

    /// <summary>ACCNT — Customer.</summary>
    [Required, StringLength(10)]
    public string Customer { get; set; } = string.Empty;

    /// <summary>BLART — Document type.</summary>
    [Required, StringLength(2)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>BLDAT — Document date.</summary>
    [Required]
    public DateOnly? DocumentDate { get; set; }

    /// <summary>BUDAT — Posting date.</summary>
    [Required]
    public DateOnly? PostingDate { get; set; }

    /// <summary>XBLNR — Reference.</summary>
    [StringLength(16)]
    public string? Reference { get; set; }

    /// <summary>WRBTR — Tổng tiền (gồm thuế), VND.</summary>
    [Range(typeof(decimal), "0.01", "99999999999.99")]
    public decimal Amount { get; set; }

    /// <summary>FWBAS — Tiền trước thuế, VND.</summary>
    [Range(typeof(decimal), "0", "99999999999.99")]
    public decimal TaxBaseAmount { get; set; }

    /// <summary>XMWST — Tự tính thuế.</summary>
    public bool CalculateTax { get; set; }

    /// <summary>MWSKZ — Tax code.</summary>
    [StringLength(2)]
    public string? TaxCode { get; set; }

    /// <summary>WMWST — Tiền thuế, VND.</summary>
    [Range(typeof(decimal), "0", "99999999999.99")]
    public decimal TaxAmount { get; set; }

    /// <summary>SGTXT — Item text.</summary>
    [StringLength(50)]
    public string? ItemText { get; set; }

    /// <summary>HKONT — GL account.</summary>
    [Required, StringLength(10)]
    public string GlAccount { get; set; } = string.Empty;

    /// <summary>SHKZG — Debit/credit indicator (S/H).</summary>
    [StringLength(1)]
    public string? DebitCreditIndicator { get; set; }

    /// <summary>KOSTL — Cost center.</summary>
    [StringLength(10)]
    public string? CostCenter { get; set; }

    /// <summary>PRCTR — Profit center.</summary>
    [StringLength(10)]
    public string? ProfitCenter { get; set; }

    /// <summary>KKBER — Credit control area.</summary>
    [StringLength(4)]
    public string? CreditControlArea { get; set; }
}
