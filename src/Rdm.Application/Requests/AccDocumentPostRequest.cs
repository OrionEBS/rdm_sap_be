using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// BAPI_ACC_DOCUMENT_POST — chứng từ FI nhiều dòng (thay các bút toán bị giới hạn bởi Z_EFI011_CREATE_FB01):
/// cấn trừ chiết khấu (ZRDM-013), chiết khấu thanh toán nhiều hóa đơn (016), lãi quá hạn (019), trích trước (020).
/// BAPI không nhận posting key: SAP tự xác định theo loại dòng + Nợ/Có (G/L 40/50, KH 01/11, KH special G/L 09/19).
/// Tiền tệ VND; BAPI không tự tính thuế — dòng Tax phải có số thuế và TaxBaseAmount.
/// </summary>
public sealed class AccDocumentPostRequest : IPostingRequest, IValidatableObject
{
    public const int MaxLines = 999;

    /// <summary>Khóa idempotency do client sinh.</summary>
    /// <example>ZB-2026-0001</example>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>COMP_CODE — Company code.</summary>
    /// <example>G210</example>
    [Required, StringLength(4)]
    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>DOC_TYPE — Loại chứng từ (DRF-930: ZB Rebates, ZL Accruals, ZD DN to Customer, DG CN to Customer).</summary>
    /// <example>ZB</example>
    [Required, StringLength(2)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>DOC_DATE — Ngày chứng từ.</summary>
    [Required]
    public DateOnly? DocumentDate { get; set; }

    /// <summary>PSTNG_DATE — Ngày hạch toán.</summary>
    [Required]
    public DateOnly? PostingDate { get; set; }

    /// <summary>FIS_PERIOD — Kỳ kế toán (1-16). Bỏ trống = SAP tự xác định theo ngày hạch toán.</summary>
    [Range(1, 16)]
    public int? FiscalPeriod { get; set; }

    /// <summary>REF_DOC_NO (XBLNR) — mã tham chiếu RDM; API chặn trùng theo Company code + loại CT + Reference.</summary>
    /// <example>RDM-QT-000123</example>
    [Required, StringLength(16, MinimumLength = 1)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>HEADER_TXT (BKTXT) — Diễn giải header.</summary>
    [StringLength(25)]
    public string? HeaderText { get; set; }

    /// <summary>NEG_POSTNG — Ghi âm (không làm tăng phát sinh Nợ/Có). Loại chứng từ phải cho phép negative posting.</summary>
    public bool NegativePosting { get; set; }

    /// <summary>
    /// true = bỏ qua kiểm tra trùng Reference trên SAP (BKPF chưa đảo cùng Company code + loại CT + XBLNR).
    /// Chỉ dùng khi chắc chắn muốn tạo thêm chứng từ cùng mã tham chiếu.
    /// </summary>
    public bool AllowDuplicateReference { get; set; }

    /// <summary>Các dòng hạch toán (2..999). Tổng Nợ phải bằng tổng Có.</summary>
    [Required, MinLength(2), MaxLength(MaxLines)]
    public List<AccDocumentLine> Lines { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Lines is null || Lines.Count == 0) yield break;

        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (line is null)
            {
                yield return new ValidationResult($"Dòng {i + 1} rỗng.", new[] { nameof(Lines) });
                continue;
            }
            foreach (var error in line.Check())
                yield return new ValidationResult($"Dòng {i + 1}: {error}", new[] { $"{nameof(Lines)}[{i}]" });
        }

        var debit = Lines.Where(l => l is not null && l.IsDebit).Sum(l => l.Amount);
        var credit = Lines.Where(l => l is not null && !l.IsDebit).Sum(l => l.Amount);
        if (debit != credit)
            yield return new ValidationResult(
                $"Chứng từ không cân: tổng Nợ {debit:0.##} ≠ tổng Có {credit:0.##}.", new[] { nameof(Lines) });

        if (!Lines.Any(l => l is not null && l.LineType != AccDocumentLineType.Tax))
            yield return new ValidationResult("Phải có ít nhất 1 dòng G/L hoặc khách hàng.", new[] { nameof(Lines) });
    }
}

public enum AccDocumentLineType
{
    /// <summary>ACCOUNTGL — tài khoản tổng hợp (PK 40/50).</summary>
    GL,
    /// <summary>ACCOUNTRECEIVABLE — khách hàng (PK 01/11; có SpecialGlIndicator → 09/19).</summary>
    Customer,
    /// <summary>ACCOUNTTAX — dòng thuế GTGT (số thuế do client tính, kèm TaxBaseAmount).</summary>
    Tax
}

public sealed class AccDocumentLine
{
    /// <summary>Loại dòng: GL, Customer, Tax.</summary>
    [Required]
    public AccDocumentLineType? LineType { get; set; }

    /// <summary>Nợ/Có: "S" = Nợ (Soll), "H" = Có (Haben).</summary>
    /// <example>S</example>
    [Required, RegularExpression("^[SsHh]$", ErrorMessage = "DebitCredit phải là S (Nợ) hoặc H (Có).")]
    public string DebitCredit { get; set; } = string.Empty;

    /// <summary>Số tiền dương (VND). Dấu do DebitCredit quyết định.</summary>
    [Range(typeof(decimal), "0.01", "999999999999999")]
    public decimal Amount { get; set; }

    /// <summary>GL_ACCOUNT — bắt buộc với dòng GL; dòng Tax tùy chọn (bỏ trống = theo tax code).</summary>
    [StringLength(10)]
    public string? GlAccount { get; set; }

    /// <summary>CUSTOMER — bắt buộc với dòng Customer.</summary>
    [StringLength(10)]
    public string? Customer { get; set; }

    /// <summary>SP_GL_IND — Special G/L indicator của dòng khách hàng (phải có TK đối chiếu trong T074).</summary>
    [StringLength(1)]
    public string? SpecialGlIndicator { get; set; }

    /// <summary>C_CTR_AREA — Credit control area (dòng khách hàng).</summary>
    [StringLength(4)]
    public string? CreditControlArea { get; set; }

    /// <summary>PMNTTRMS — Điều khoản thanh toán (dòng khách hàng ghi Nợ, vd. phải thu lãi).</summary>
    [StringLength(4)]
    public string? PaymentTerms { get; set; }

    /// <summary>BLINE_DATE — Baseline date tính hạn thanh toán (dòng khách hàng).</summary>
    public DateOnly? BaselineDate { get; set; }

    /// <summary>COSTCENTER — dòng GL chi phí.</summary>
    [StringLength(10)]
    public string? CostCenter { get; set; }

    /// <summary>PROFIT_CTR — Profit center.</summary>
    [StringLength(10)]
    public string? ProfitCenter { get; set; }

    /// <summary>SEGMENT — Segment (dòng GL).</summary>
    [StringLength(10)]
    public string? Segment { get; set; }

    /// <summary>ALLOC_NMBR (ZUONR) — Assignment: mã chương trình RDM / số hóa đơn được hưởng.</summary>
    [StringLength(18)]
    public string? Assignment { get; set; }

    /// <summary>ITEM_TEXT (SGTXT) — Diễn giải dòng.</summary>
    [StringLength(50)]
    public string? ItemText { get; set; }

    /// <summary>TAX_CODE — bắt buộc với dòng Tax; dòng GL/Customer chịu thuế truyền cùng mã.</summary>
    [StringLength(2)]
    public string? TaxCode { get; set; }

    /// <summary>AMT_BASE — Cơ sở tính thuế (bắt buộc với dòng Tax, số dương).</summary>
    [Range(typeof(decimal), "0", "999999999999999")]
    public decimal? TaxBaseAmount { get; set; }

    /// <summary>ACCT_KEY — Account key thuế (vd. MWS). Bỏ trống = SAP xác định theo tax code.</summary>
    [StringLength(3)]
    public string? TaxAccountKey { get; set; }

    /// <summary>COND_KEY — Condition type thuế (vd. MWAS). Bỏ trống = SAP xác định theo tax code.</summary>
    [StringLength(4)]
    public string? TaxConditionKey { get; set; }

    /// <summary>
    /// CRITERIA — Đặc tính CO-PA của dòng (FIELDNAME → giá trị), vd. { "KNDNR": "100166", "ARTNR": "100016", "VKORG": "G210" }.
    /// </summary>
    public Dictionary<string, string>? ProfitabilitySegment { get; set; }

    public bool IsDebit => DebitCredit.Trim().Equals("S", StringComparison.OrdinalIgnoreCase);

    internal IEnumerable<string> Check()
    {
        switch (LineType)
        {
            case AccDocumentLineType.GL when string.IsNullOrWhiteSpace(GlAccount):
                yield return "dòng GL cần GlAccount.";
                break;
            case AccDocumentLineType.Customer when string.IsNullOrWhiteSpace(Customer):
                yield return "dòng Customer cần Customer.";
                break;
            case AccDocumentLineType.Tax:
                if (string.IsNullOrWhiteSpace(TaxCode)) yield return "dòng Tax cần TaxCode.";
                if (TaxBaseAmount is null) yield return "dòng Tax cần TaxBaseAmount.";
                break;
        }
        if (LineType != AccDocumentLineType.Customer &&
            (SpecialGlIndicator ?? CreditControlArea ?? PaymentTerms) is not null)
            yield return "SpecialGlIndicator / CreditControlArea / PaymentTerms chỉ dùng cho dòng Customer.";
        if (LineType == AccDocumentLineType.Tax && ProfitabilitySegment is { Count: > 0 })
            yield return "dòng Tax không nhận ProfitabilitySegment.";
        if (ProfitabilitySegment is not null)
            foreach (var (field, value) in ProfitabilitySegment)
                if (string.IsNullOrWhiteSpace(field) || field.Trim().Length > 30 || string.IsNullOrWhiteSpace(value))
                    yield return $"ProfitabilitySegment '{field}' không hợp lệ.";
    }
}
