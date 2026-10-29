using System.Globalization;
using Rdm.Application.Requests;

namespace Rdm.Sap;

/// <summary>
/// Map request → giá trị field của structure import (IS_FB01 / IS_FB75 / IS_VA01).
/// Trả dictionary thuần để unit-test được mà không cần kết nối SAP.
///   - Ngày: các hàm Z cắt chuỗi theo DD.MM.YYYY (BLDAT+6(4) = năm...) → bắt buộc định dạng dd.MM.yyyy.
///   - Mã khách hàng / GL / cost center / material: hàm Z tự CONVERSION_EXIT_ALPHA_INPUT, gửi nguyên giá trị.
///   - Field null không set (để initial).
/// </summary>
public static class SapRequestMapper
{
    public const string SapDateFormat = "dd.MM.yyyy";

    public static IReadOnlyDictionary<string, object?> ToFb01(Fb01Request r) => new Dictionary<string, object?>
    {
        ["BUKRS"] = Code(r.CompanyCode),
        ["BLART"] = Code(r.DocumentType),
        ["BLDAT"] = Date(r.DocumentDate),
        ["BUDAT"] = Date(r.PostingDate),
        ["XBLNR"] = Text(r.Reference),
        ["BKTXT"] = Text(r.HeaderText),
        ["ZZBSCHL_1"] = PostingKey(r.GlLine!.PostingKey),
        ["ZZHKONT_1"] = Code(r.GlLine.GlAccount),
        ["ZZWRBTR_1"] = r.GlLine.Amount,
        ["ZZKOSTL_1"] = Code(r.GlLine.CostCenter),
        ["ZZSGTXT_1"] = Text(r.GlLine.ItemText),
        ["ZZBSCHL_2"] = PostingKey(r.CustomerLine!.PostingKey),
        ["KUNNR"] = Code(r.CustomerLine.Customer),
        ["ZZUMSKZ_2"] = Code(r.CustomerLine.SpecialGlIndicator),
        ["ZZWRBTR_2"] = r.CustomerLine.Amount,
        ["ZZSGTXT_2"] = Text(r.CustomerLine.ItemText),
        ["KKBER"] = Code(r.CreditControlArea)
    };

    public static IReadOnlyDictionary<string, object?> ToFb75(Fb75Request r) => new Dictionary<string, object?>
    {
        ["BUKRS"] = Code(r.CompanyCode),
        ["BUSCS"] = Code(r.BusinessTransaction),
        ["ACCNT"] = Code(r.Customer),
        ["BLART"] = Code(r.DocumentType),
        ["BLDAT"] = Date(r.DocumentDate),
        ["BUDAT"] = Date(r.PostingDate),
        ["XBLNR"] = Text(r.Reference),
        ["WRBTR"] = r.Amount,
        ["FWBAS"] = r.TaxBaseAmount,
        ["XMWST"] = r.CalculateTax ? "X" : null,
        ["MWSKZ"] = Code(r.TaxCode),
        ["WMWST"] = r.TaxAmount,
        ["SGTXT"] = Text(r.ItemText),
        ["HKONT"] = Code(r.GlAccount),
        ["SHKZG"] = Code(r.DebitCreditIndicator),
        ["KOSTL"] = Code(r.CostCenter),
        ["PRCTR"] = Code(r.ProfitCenter),
        ["KKBER"] = Code(r.CreditControlArea)
    };

    public static IReadOnlyDictionary<string, object?> ToVa01(Va01Request r) => new Dictionary<string, object?>
    {
        ["AUART"] = Code(r.OrderType),
        ["VKORG"] = Code(r.SalesOrganization),
        ["VTWEG"] = Code(r.DistributionChannel),
        ["SPART"] = Code(r.Division),
        ["KUNAG"] = Code(r.SoldTo),
        ["KUNWE"] = Code(r.ShipTo),
        ["BSTKD"] = Text(r.CustomerPoNumber),
        ["FKDAT"] = Date(r.BillingDate),
        ["AUGRU"] = Code(r.OrderReason),
        ["TEXT_LINE"] = Text(r.HeaderText),
        ["MATNR"] = Code(r.Material),
        ["ZMENG"] = r.Quantity,
        ["ZIEME"] = Code(r.Unit),
        ["WERKS"] = Code(r.Plant),
        ["VSTEL"] = Code(r.ShippingPoint),
        ["KBETR"] = r.Rate
    };

    /// <summary>
    /// IS_VBN1 — giá trị đi thẳng vào màn hình VBN1 qua batch input (màn hình tự áp conversion exit cho KUNNR).
    /// Ngày: hàm Z ghép DD+MM+YYYY từ chuỗi DD.MM.YYYY.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ToVbn1(Vbn1Request r) => new Dictionary<string, object?>
    {
        ["KSCHL"] = Code(r.ConditionType),
        ["SELKZ"] = r.KeyCombination.Trim(),
        ["VKORG"] = Code(r.SalesOrganization),
        ["VTWEG"] = Code(r.DistributionChannel),
        ["KUNNR"] = Code(r.Customer),
        ["VKBUR"] = Code(r.SalesOffice),
        ["ZZKVGR3"] = Code(r.CustomerGroup3),
        ["DATAB"] = Date(r.ValidFrom),
        ["DATBI"] = Date(r.ValidTo),
        ["ZZEXCLUSIVE"] = r.Exclusive ? "X" : null,
        ["MATNR"] = Code(r.Material),
        ["VSBED"] = Code(r.ShippingCondition),
        ["WERKS"] = Code(r.Plant),
        ["DISPKNRMM"] = r.MinimumQuantity,
        ["DISPKNRNM"] = r.FreeGoodsQuantity,
        ["KNRME"] = Code(r.QuantityUnit),
        ["DISPKNRZM"] = r.AdditionalQuantity,
        ["KNREZ"] = Code(r.AdditionalQuantityUnit),
        ["KNRRR"] = r.CalculationRule.ToString("000", CultureInfo.InvariantCulture),
        ["KNRDD"] = Code(r.FreeGoodsCategory),
        ["KNRMAT"] = Code(r.FreeGoodsMaterial)
    };

    public static IReadOnlyDictionary<string, object?> ToVa41(Va41Request r) => new Dictionary<string, object?>
    {
        ["AUART"] = Code(r.ContractType),
        ["VKORG"] = Code(r.SalesOrganization),
        ["VTWEG"] = Code(r.DistributionChannel),
        ["SPART"] = Code(r.Division),
        ["KUNAG"] = Code(r.SoldTo),
        ["KUNWE"] = Code(r.ShipTo),
        ["BSTKD"] = Text(r.CustomerPoNumber),
        ["GUEBG"] = Date(r.ValidFrom),
        ["GUEEN"] = Date(r.ValidTo),
        ["MATNR"] = Code(r.Material),
        ["ZMENG"] = r.Quantity,
        ["ZIEME"] = Code(r.Unit),
        ["WERKS"] = Code(r.Plant),
        ["VSTEL"] = Code(r.ShippingPoint),
        ["VSBED"] = Code(r.ShippingCondition)
    };

    public static string? Date(DateOnly? d) => d?.ToString(SapDateFormat, CultureInfo.InvariantCulture);

    /// <summary>Định dạng DATS (YYYYMMDD) cho field kiểu DATE của BAPI chuẩn.</summary>
    public static string? Dats(DateOnly? d) => d?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Mã SAP: trim + viết hoa.</summary>
    public static string? Code(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    public static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>BSCHL CHAR(2) tra TBSL bằng khóa 2 chữ số — "9" phải thành "09".</summary>
    public static string? PostingKey(string? s) => Code(s)?.PadLeft(2, '0');
}
