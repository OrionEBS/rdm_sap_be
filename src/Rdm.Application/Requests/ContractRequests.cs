using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Tạo sale contract — BAPI_CONTRACT_CREATEFROMDATA + BAPI_TRANSACTION_COMMIT (ZRDM-006, 010).
/// Loại hợp đồng được phép cấu hình ở Sap:ContractTypes (mặc định ZVCQ VN Sales Contract, ZVCF VN Free Remain).
/// RequireApproval = true: header gắn delivery block + billing block chờ duyệt (Sap:ContractApprovalDeliveryBlock /
/// ContractApprovalBillingBlock, mặc định 30 "Price Chg. Approval") → đơn call-off chưa giao/billing được cho tới khi gọi API duyệt.
/// </summary>
public sealed class ContractCreateRequest : IPostingRequest, IValidatableObject
{
    public const int MaxItems = 500;

    /// <summary>Khóa idempotency do client sinh.</summary>
    /// <example>HD-2026-0001</example>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>DOC_TYPE (AUART) — Loại hợp đồng.</summary>
    /// <example>ZVCQ</example>
    [Required, StringLength(4)]
    public string ContractType { get; set; } = string.Empty;

    /// <summary>SALES_ORG.</summary>
    /// <example>G210</example>
    [Required, StringLength(4)]
    public string SalesOrganization { get; set; } = string.Empty;

    /// <summary>DISTR_CHAN.</summary>
    /// <example>10</example>
    [Required, StringLength(2)]
    public string DistributionChannel { get; set; } = string.Empty;

    /// <summary>DIVISION.</summary>
    /// <example>10</example>
    [Required, StringLength(2)]
    public string Division { get; set; } = string.Empty;

    /// <summary>Partner AG — Sold-to.</summary>
    /// <example>100166</example>
    [Required, StringLength(10)]
    public string SoldTo { get; set; } = string.Empty;

    /// <summary>Partner WE — Ship-to (ZVCF: bắt buộc theo khóa Sold-to + Ship-to + Plant + Material; ZVCQ trên DRF không dùng WE ở header).</summary>
    /// <example>100166</example>
    [StringLength(10)]
    public string? ShipTo { get; set; }

    /// <summary>Partner RE — Bill-to (bỏ trống = SAP xác định theo partner của sold-to).</summary>
    [StringLength(10)]
    public string? BillTo { get; set; }

    /// <summary>Partner RG — Payer (bỏ trống = SAP xác định theo partner của sold-to).</summary>
    [StringLength(10)]
    public string? Payer { get; set; }

    /// <summary>PURCH_NO_C — Số hợp đồng / PO của khách hàng.</summary>
    [StringLength(35)]
    public string? CustomerPoNumber { get; set; }

    /// <summary>PURCH_DATE — Ngày PO / ngày ký.</summary>
    public DateOnly? CustomerPoDate { get; set; }

    /// <summary>CT_VALID_F + CON_ST_DAT — Hiệu lực từ.</summary>
    [Required]
    public DateOnly? ValidFrom { get; set; }

    /// <summary>CT_VALID_T + CON_EN_DAT — Hiệu lực đến.</summary>
    [Required]
    public DateOnly? ValidTo { get; set; }

    /// <summary>SALES_OFF.</summary>
    [StringLength(4)]
    public string? SalesOffice { get; set; }

    /// <summary>SALES_GRP.</summary>
    [StringLength(3)]
    public string? SalesGroup { get; set; }

    /// <summary>SHIP_COND — Shipping condition.</summary>
    /// <example>PK</example>
    [StringLength(2)]
    public string? ShippingCondition { get; set; }

    /// <summary>PMNTTRMS — Điều khoản thanh toán (bỏ trống = theo khách hàng).</summary>
    [StringLength(4)]
    public string? PaymentTerms { get; set; }

    /// <summary>true (mặc định) = tạo ở trạng thái chờ duyệt (gắn delivery + billing block duyệt).</summary>
    public bool RequireApproval { get; set; } = true;

    [Required, MinLength(1), MaxLength(MaxItems)]
    public List<ContractItemInput> Items { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValidFrom is { } from && ValidTo is { } to && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });

        if (Items is null) yield break;
        for (var i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it is null)
                yield return new ValidationResult($"Dòng {i + 1} rỗng.", new[] { nameof(Items) });
            else if (string.IsNullOrWhiteSpace(it.Material))
                yield return new ValidationResult($"Dòng {i + 1}: cần Material.", new[] { $"{nameof(Items)}[{i}]" });
            else if (it.TargetQuantity > 0 && string.IsNullOrWhiteSpace(it.Unit))
                yield return new ValidationResult($"Dòng {i + 1}: có TargetQuantity thì cần Unit.", new[] { $"{nameof(Items)}[{i}]" });
        }

        var numbers = Items.Where(i => i?.ItemNumber is not null).Select(i => i!.ItemNumber!.Value).ToList();
        if (numbers.Count != numbers.Distinct().Count())
            yield return new ValidationResult("ItemNumber bị trùng.", new[] { nameof(Items) });
    }
}

/// <summary>Dòng hợp đồng.</summary>
public sealed class ContractItemInput
{
    /// <summary>ITM_NUMBER — bỏ trống khi tạo = tự đánh 10, 20, 30…</summary>
    [Range(1, 999999)]
    public int? ItemNumber { get; set; }

    /// <summary>MATERIAL.</summary>
    /// <example>100016</example>
    [StringLength(18)]
    public string? Material { get; set; }

    /// <summary>TARGET_QTY — Định mức sản lượng (ZVCQ trên DRF thường để 0).</summary>
    [Range(typeof(decimal), "0", "9999999999.999")]
    public decimal? TargetQuantity { get; set; }

    /// <summary>TARGET_QU — Đơn vị (mã nội bộ SAP, vd. TO, BAG).</summary>
    [StringLength(3)]
    public string? Unit { get; set; }

    /// <summary>PLANT (ZVCF: bắt buộc theo nghiệp vụ; ZVCQ có thể bỏ trống).</summary>
    [StringLength(4)]
    public string? Plant { get; set; }

    /// <summary>SHIP_POINT.</summary>
    [StringLength(4)]
    public string? ShippingPoint { get; set; }

    /// <summary>SHORT_TEXT — Mô tả dòng (bỏ trống = tên vật tư).</summary>
    [StringLength(40)]
    public string? ShortText { get; set; }
}

public enum ContractItemAction
{
    /// <summary>Thêm dòng mới.</summary>
    Add,
    /// <summary>Sửa số lượng / plant / shipping point của dòng có sẵn.</summary>
    Update,
    /// <summary>Từ chối (hủy) dòng bằng reason for rejection — dòng không bị xóa khỏi SAP.</summary>
    Reject
}

/// <summary>
/// Sửa sale contract — BAPI_CUSTOMERCONTRACT_CHANGE + BAPI_TRANSACTION_COMMIT.
/// Chỉ field được truyền mới bị đổi. RequireReapproval = true (mặc định): gắn lại block duyệt sau khi sửa.
/// </summary>
public sealed class ContractChangeRequest : IPostingRequest, IValidatableObject
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>VBELN — Số hợp đồng SAP.</summary>
    /// <example>1100000000</example>
    [Required, StringLength(10)]
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>Hiệu lực từ mới (header + contract data của mọi dòng).</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>Hiệu lực đến mới.</summary>
    public DateOnly? ValidTo { get; set; }

    [StringLength(35)]
    public string? CustomerPoNumber { get; set; }

    public DateOnly? CustomerPoDate { get; set; }

    [StringLength(2)]
    public string? ShippingCondition { get; set; }

    [StringLength(4)]
    public string? PaymentTerms { get; set; }

    /// <summary>true (mặc định) = sửa xong hợp đồng trở về trạng thái chờ duyệt.</summary>
    public bool RequireReapproval { get; set; } = true;

    [MaxLength(ContractCreateRequest.MaxItems)]
    public List<ContractItemChange> Items { get; set; } = new();

    public bool HasHeaderChange =>
        ValidFrom is not null || ValidTo is not null || CustomerPoNumber is not null || CustomerPoDate is not null ||
        ShippingCondition is not null || PaymentTerms is not null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ValidFrom is { } from && ValidTo is { } to && to < from)
            yield return new ValidationResult("ValidTo phải ≥ ValidFrom.", new[] { nameof(ValidTo) });

        if (!HasHeaderChange && (Items is null || Items.Count == 0))
            yield return new ValidationResult("Không có thay đổi nào.", new[] { nameof(Items) });

        if (Items is null) yield break;
        for (var i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            var at = new[] { $"{nameof(Items)}[{i}]" };
            if (it is null)
            {
                yield return new ValidationResult($"Dòng {i + 1} rỗng.", at);
                continue;
            }
            switch (it.Action)
            {
                case ContractItemAction.Add when string.IsNullOrWhiteSpace(it.Material):
                    yield return new ValidationResult($"Dòng {i + 1} (Add): cần Material.", at);
                    break;
                case ContractItemAction.Add when it.TargetQuantity > 0 && string.IsNullOrWhiteSpace(it.Unit):
                    yield return new ValidationResult($"Dòng {i + 1} (Add): có TargetQuantity thì cần Unit.", at);
                    break;
                case ContractItemAction.Update when it.ItemNumber is null:
                    yield return new ValidationResult($"Dòng {i + 1} (Update): cần ItemNumber.", at);
                    break;
                case ContractItemAction.Update when it.Material is not null:
                    yield return new ValidationResult($"Dòng {i + 1} (Update): không đổi được Material — từ chối dòng cũ và thêm dòng mới.", at);
                    break;
                case ContractItemAction.Update when it.TargetQuantity is null && it.Unit is null && it.Plant is null &&
                                                    it.ShippingPoint is null && it.ShortText is null:
                    yield return new ValidationResult($"Dòng {i + 1} (Update): không có field nào để sửa.", at);
                    break;
                case ContractItemAction.Reject when it.ItemNumber is null || string.IsNullOrWhiteSpace(it.RejectionReason):
                    yield return new ValidationResult($"Dòng {i + 1} (Reject): cần ItemNumber và RejectionReason.", at);
                    break;
                case null:
                    yield return new ValidationResult($"Dòng {i + 1}: cần Action (Add / Update / Reject).", at);
                    break;
            }
        }

        var numbers = Items.Where(i => i?.ItemNumber is not null).Select(i => i!.ItemNumber!.Value).ToList();
        if (numbers.Count != numbers.Distinct().Count())
            yield return new ValidationResult("ItemNumber bị lặp trong cùng request.", new[] { nameof(Items) });
    }
}

public sealed class ContractItemChange
{
    [Required]
    public ContractItemAction? Action { get; set; }

    /// <summary>ITM_NUMBER — bắt buộc với Update/Reject; Add bỏ trống = số lớn nhất hiện có + 10.</summary>
    [Range(1, 999999)]
    public int? ItemNumber { get; set; }

    [StringLength(18)]
    public string? Material { get; set; }

    [Range(typeof(decimal), "0", "9999999999.999")]
    public decimal? TargetQuantity { get; set; }

    [StringLength(3)]
    public string? Unit { get; set; }

    [StringLength(4)]
    public string? Plant { get; set; }

    [StringLength(4)]
    public string? ShippingPoint { get; set; }

    [StringLength(40)]
    public string? ShortText { get; set; }

    /// <summary>REASON_REJ (ABGRU) — Lý do từ chối (Reject).</summary>
    [StringLength(2)]
    public string? RejectionReason { get; set; }
}

/// <summary>
/// Duyệt sale contract — gỡ delivery block + billing block duyệt ở header (BAPI_CUSTOMERCONTRACT_CHANGE + COMMIT).
/// Chỉ gỡ block đúng mã duyệt cấu hình; block khác (vd. Z1 credit limit) được giữ nguyên.
/// Hợp đồng không còn block duyệt → trả thành công, không gọi BAPI.
/// </summary>
public sealed class ContractApproveRequest : IPostingRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>VBELN — Số hợp đồng SAP.</summary>
    /// <example>1100000000</example>
    [Required, StringLength(10)]
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>Người duyệt trên RDM (lưu ở nhật ký API, không ghi vào SAP).</summary>
    [StringLength(100)]
    public string? ApprovedBy { get; set; }
}
