using System.ComponentModel.DataAnnotations;

namespace Rdm.Application.Requests;

/// <summary>
/// Đổi tax classification 1 của vật tư (MLAN-TAXM1) qua BAPI_MATERIAL_SAVEDATA — thay cho MM17.
/// Mỗi vật tư 1 lần gọi BAPI + commit. Thao tác "đặt TAXM1 = X" là idempotent nên gửi lại an toàn.
/// </summary>
public sealed class MaterialTaxClassRequest : IPostingRequest, IValidatableObject
{
    public const int MaxItems = 500;

    /// <summary>Khóa idempotency do client sinh.</summary>
    /// <example>TAX-20261014-001</example>
    [Required, StringLength(64, MinimumLength = 1)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>MLAN-ALAND — quốc gia, mặc định VN.</summary>
    /// <example>VN</example>
    [Required, StringLength(3)]
    public string Country { get; set; } = "VN";

    /// <summary>Tax category (TATYP). Bỏ trống = loại thuế thứ 1 của quốc gia trong TSTL (VN: MWS1).</summary>
    [StringLength(4)]
    public string? TaxCategory { get; set; }

    /// <summary>TAXM1 áp dụng cho mọi vật tư không có TaxClass riêng. VN/MWS1: 0 No tax, 1 Full tax, 2 Half tax, 3 Low tax rate, 4 Mixed, 5 Tax rate 8%.</summary>
    /// <example>5</example>
    [StringLength(1)]
    public string? TaxClass { get; set; }

    /// <summary>Danh sách vật tư (tối đa 500).</summary>
    [Required, MinLength(1), MaxLength(MaxItems)]
    public List<MaterialTaxClassItem> Items { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items.Any(i => string.IsNullOrWhiteSpace(i.TaxClass)) && string.IsNullOrWhiteSpace(TaxClass))
            yield return new ValidationResult("Mỗi vật tư phải có TaxClass (hoặc truyền TaxClass chung).", new[] { nameof(TaxClass) });

        var duplicates = Items.Where(i => !string.IsNullOrWhiteSpace(i.Material))
            .GroupBy(i => i.Material.Trim().TrimStart('0').ToUpperInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Material.Trim())
            .ToList();
        if (duplicates.Count > 0)
            yield return new ValidationResult($"Vật tư bị lặp: {string.Join(", ", duplicates)}.", new[] { nameof(Items) });
    }
}

public sealed class MaterialTaxClassItem
{
    /// <summary>MATNR — mã vật tư (100016 hoặc 000000000000100016).</summary>
    /// <example>100016</example>
    [Required, StringLength(18, MinimumLength = 1)]
    public string Material { get; set; } = string.Empty;

    /// <summary>TAXM1 riêng cho vật tư này (bỏ trống = dùng TaxClass chung).</summary>
    [StringLength(1)]
    public string? TaxClass { get; set; }
}
