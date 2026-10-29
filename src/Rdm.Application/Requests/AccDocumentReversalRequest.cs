using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// BAPI_ACC_DOCUMENT_REV_POST — đảo chứng từ kế toán đã post qua BAPI (AWTYP = BKPFF, vd. chứng từ tạo từ FB01/FB75 API).
/// Nếu không truyền ObjectType/ObjectKey/ObjectSystem, API tự đọc BKPF (AWTYP/AWKEY/AWSYS) theo CompanyCode + DocumentNumber + FiscalYear.
/// </summary>
public sealed class AccDocumentReversalRequest : IPostingRequest, IValidatableObject
{
    /// <summary>Khóa idempotency do client sinh.</summary>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>COMP_CODE — Company code của chứng từ gốc.</summary>
    [Required, StringLength(4)]
    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>BELNR — Số chứng từ gốc.</summary>
    [Required, StringLength(10)]
    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>GJAHR — Năm tài chính của chứng từ gốc.</summary>
    [Required, RegularExpression(@"^\d{4}$")]
    public string FiscalYear { get; set; } = string.Empty;

    /// <summary>REASON_REV — Lý do đảo (vd. 01, 02).</summary>
    [Required, StringLength(2)]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>PSTNG_DATE — Ngày hạch toán chứng từ đảo. Bỏ trống = theo chứng từ gốc.</summary>
    public DateOnly? PostingDate { get; set; }

    /// <summary>FIS_PERIOD — Kỳ kế toán (1-16). Bỏ trống = SAP tự xác định.</summary>
    [Range(1, 16)]
    public int? FiscalPeriod { get; set; }

    /// <summary>BUS_ACT — Business transaction, mặc định RFBU.</summary>
    [StringLength(4)]
    public string BusinessTransaction { get; set; } = "RFBU";

    /// <summary>OBJ_TYPE của chứng từ gốc (AWTYP). Tùy chọn.</summary>
    [StringLength(5)]
    public string? ObjectType { get; set; }

    /// <summary>OBJ_KEY của chứng từ gốc (AWKEY). Tùy chọn.</summary>
    [StringLength(20)]
    public string? ObjectKey { get; set; }

    /// <summary>OBJ_SYS của chứng từ gốc (AWSYS). Tùy chọn.</summary>
    [StringLength(10)]
    public string? ObjectSystem { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ObjectType) != !string.IsNullOrWhiteSpace(ObjectKey))
            yield return new ValidationResult(
                "ObjectType và ObjectKey phải truyền cùng nhau (hoặc bỏ trống cả hai để API tự đọc BKPF).",
                new[] { nameof(ObjectType), nameof(ObjectKey) });
    }
}
