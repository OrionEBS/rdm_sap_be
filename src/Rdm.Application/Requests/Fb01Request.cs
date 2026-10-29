using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>Mọi request ghi SAP phải có requestId để chống tạo chứng từ trùng khi client gửi lại.</summary>
public interface IPostingRequest
{
    string RequestId { get; }
}

/// <summary>
/// Z_EFI011_CREATE_FB01 (structure ZSFI_FB01). SAP post qua BAPI_ACC_DOCUMENT_POST 2 dòng:
/// dòng 1 GL, dòng 2 khách hàng (số tiền dòng 2 SAP tự đổi dấu âm). Tiền tệ cố định VND phía SAP.
/// </summary>
public sealed class Fb01Request : IPostingRequest
{
    /// <summary>Khóa idempotency do client sinh (vd. mã chứng từ nguồn). Gửi lại cùng requestId sẽ không tạo chứng từ mới.</summary>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>BUKRS — Company code.</summary>
    [Required, StringLength(4)]
    public string CompanyCode { get; set; } = string.Empty;

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

    /// <summary>BKTXT — Header text.</summary>
    [StringLength(25)]
    public string? HeaderText { get; set; }

    /// <summary>KKBER — Credit control area.</summary>
    [StringLength(4)]
    public string? CreditControlArea { get; set; }

    /// <summary>Dòng 1 — GL.</summary>
    [Required]
    public Fb01GlLine? GlLine { get; set; }

    /// <summary>Dòng 2 — Khách hàng.</summary>
    [Required]
    public Fb01CustomerLine? CustomerLine { get; set; }
}

public sealed class Fb01GlLine
{
    /// <summary>ZZBSCHL_1 — Posting key (vd. 40).</summary>
    [Required, StringLength(2)]
    public string PostingKey { get; set; } = string.Empty;

    /// <summary>ZZHKONT_1 — GL account.</summary>
    [Required, StringLength(10)]
    public string GlAccount { get; set; } = string.Empty;

    /// <summary>ZZWRBTR_1 — Amount (VND).</summary>
    [Range(typeof(decimal), "0.01", "99999999999.99")]
    public decimal Amount { get; set; }

    /// <summary>ZZKOSTL_1 — Cost center.</summary>
    [StringLength(10)]
    public string? CostCenter { get; set; }

    /// <summary>ZZSGTXT_1 — Item text.</summary>
    [StringLength(50)]
    public string? ItemText { get; set; }
}

public sealed class Fb01CustomerLine
{
    /// <summary>ZZBSCHL_2 — Posting key (vd. 19).</summary>
    [Required, StringLength(2)]
    public string PostingKey { get; set; } = string.Empty;

    /// <summary>KUNNR — Customer.</summary>
    [Required, StringLength(10)]
    public string Customer { get; set; } = string.Empty;

    /// <summary>ZZUMSKZ_2 — Special G/L indicator.</summary>
    [StringLength(1)]
    public string? SpecialGlIndicator { get; set; }

    /// <summary>ZZWRBTR_2 — Amount (VND, gửi số dương; SAP tự đổi dấu).</summary>
    [Range(typeof(decimal), "0.01", "99999999999.99")]
    public decimal Amount { get; set; }

    /// <summary>ZZSGTXT_2 — Item text.</summary>
    [StringLength(50)]
    public string? ItemText { get; set; }
}
