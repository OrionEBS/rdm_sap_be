namespace Rdm.Application.Customers;

/// <summary>
/// 1 dòng KNKK — hạn mức tín dụng khách hàng theo credit control area.
/// Số tiền đã quy đổi về giá trị thực theo số lẻ của tiền tệ (TCURX), vd. VND: SAP lưu 112941.20 → 11294120.
/// </summary>
public sealed record CustomerCredit
{
    /// <summary>KUNNR — Customer number.</summary>
    public string CustomerNumber { get; init; } = string.Empty;
    /// <summary>KKBER — Credit control area.</summary>
    public string CreditControlArea { get; init; } = string.Empty;
    /// <summary>T014-WAERS — Tiền tệ của credit control area (tiền tệ của KLIMK/SAUFT/SKFOR/SSOBL).</summary>
    public string? Currency { get; init; }
    /// <summary>KLIMK — Credit limit.</summary>
    public decimal CreditLimit { get; init; }
    /// <summary>KNKLI — Customer's account number with credit limit reference.</summary>
    public string? CreditAccount { get; init; }
    /// <summary>SAUFT — Total of sales values for credit limit check.</summary>
    public decimal SalesValue { get; init; }
    /// <summary>SKFOR — Total receivables.</summary>
    public decimal Receivables { get; init; }
    /// <summary>SSOBL — Relevant special liabilities.</summary>
    public decimal SpecialLiabilities { get; init; }
    /// <summary>UEDAT — Date on which the credit limit was exceeded.</summary>
    public DateOnly? CreditLimitExceededOn { get; init; }
    /// <summary>ERNAM — Created by.</summary>
    public string? CreatedBy { get; init; }
    /// <summary>ERDAT — Created on.</summary>
    public DateOnly? CreatedOn { get; init; }
    /// <summary>CTLPC — Risk category.</summary>
    public string? RiskCategory { get; init; }
    /// <summary>KRAUS — Credit information number.</summary>
    public string? CreditInfoNumber { get; init; }
    /// <summary>AEDAT — Changed on.</summary>
    public DateOnly? ChangedOn { get; init; }
    /// <summary>AENAM — Changed by.</summary>
    public string? ChangedBy { get; init; }
    /// <summary>CASHD — Date of last payment.</summary>
    public DateOnly? LastPaymentDate { get; init; }
    /// <summary>CASHA — Amount of last payment (theo LastPaymentCurrency).</summary>
    public decimal LastPaymentAmount { get; init; }
    /// <summary>CASHC — Currency of last payment.</summary>
    public string? LastPaymentCurrency { get; init; }
}

public sealed record CustomerCreditList(int Count, DateTime ReadAtUtc, IReadOnlyList<CustomerCredit> Items);

/// <summary>Mã lỗi của Z_ESD017_READ_KNKK (ET_RETURN-PARAMETER) + lỗi phía API.</summary>
public static class CreditErrorCodes
{
    /// <summary>KUNNR không có trong KNA1 → 404.</summary>
    public const string CustomerNotFound = "CREDIT_CUSTOMER_NOT_FOUND";
    /// <summary>KKBER không có trong T014 → 404.</summary>
    public const string AreaNotFound = "CREDIT_AREA_NOT_FOUND";
    /// <summary>User RFC không có quyền F_KNKA_KKB (ACTVT 03) cho credit control area → 403.</summary>
    public const string NoAuthority = "CREDIT_NO_AUTHORITY";
    /// <summary>Lỗi không lường trước trong hàm ABAP → 502.</summary>
    public const string Unexpected = "CREDIT_UNEXPECTED_ERROR";
    /// <summary>Sap:CustomerCreditSource = Function nhưng hàm chưa có trên SAP → 502.</summary>
    public const string FunctionMissing = "CREDIT_RFC_MISSING";
}
