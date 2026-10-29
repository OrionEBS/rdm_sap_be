using Rdm.Application.Requests;

namespace Rdm.Sap;

/// <summary>Giá trị các tham số BAPI_ACC_DOCUMENT_POST/CHECK — dictionary thuần để unit-test không cần SAP.</summary>
public sealed record AccDocumentBapiInput(
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> AccountGl,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> AccountReceivable,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> AccountTax,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> CurrencyAmount,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Criteria);

/// <summary>
/// Map <see cref="AccDocumentPostRequest"/> → BAPI_ACC_DOCUMENT_POST.
///   - ITEMNO_ACC = thứ tự dòng (1..n); CURRENCYAMOUNT: Nợ dương, Có âm (SAP tự chọn posting key theo dấu).
///   - BAPI không chạy conversion exit: TK, KH, cost/profit center toàn số được thêm 0 đầu.
///   - Ngày kiểu DATS (yyyyMMdd).
/// </summary>
public static class AccDocumentMapper
{
    public const string BusinessTransaction = "RFBU";

    public static AccDocumentBapiInput Map(AccDocumentPostRequest r, string currency = "VND")
    {
        var header = new Dictionary<string, object?>
        {
            ["BUS_ACT"] = BusinessTransaction,
            ["COMP_CODE"] = SapRequestMapper.Code(r.CompanyCode),
            ["DOC_TYPE"] = SapRequestMapper.Code(r.DocumentType),
            ["DOC_DATE"] = SapRequestMapper.Dats(r.DocumentDate),
            ["PSTNG_DATE"] = SapRequestMapper.Dats(r.PostingDate),
            ["FIS_PERIOD"] = r.FiscalPeriod?.ToString("00"),
            ["REF_DOC_NO"] = SapRequestMapper.Text(r.Reference),
            ["HEADER_TXT"] = SapRequestMapper.Text(r.HeaderText),
            ["NEG_POSTNG"] = r.NegativePosting ? "X" : null
        };

        var gl = new List<IReadOnlyDictionary<string, object?>>();
        var ar = new List<IReadOnlyDictionary<string, object?>>();
        var tax = new List<IReadOnlyDictionary<string, object?>>();
        var amounts = new List<IReadOnlyDictionary<string, object?>>();
        var criteria = new List<IReadOnlyDictionary<string, object?>>();

        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            var itemNo = ItemNo(i + 1);
            var sign = l.IsDebit ? 1m : -1m;

            switch (l.LineType)
            {
                case AccDocumentLineType.GL:
                    gl.Add(new Dictionary<string, object?>
                    {
                        ["ITEMNO_ACC"] = itemNo,
                        ["GL_ACCOUNT"] = Alpha(l.GlAccount, 10),
                        ["ITEM_TEXT"] = SapRequestMapper.Text(l.ItemText),
                        ["ALLOC_NMBR"] = SapRequestMapper.Text(l.Assignment),
                        ["COSTCENTER"] = Alpha(l.CostCenter, 10),
                        ["PROFIT_CTR"] = Alpha(l.ProfitCenter, 10),
                        ["SEGMENT"] = SapRequestMapper.Code(l.Segment),
                        ["TAX_CODE"] = SapRequestMapper.Code(l.TaxCode)
                    });
                    break;

                case AccDocumentLineType.Customer:
                    ar.Add(new Dictionary<string, object?>
                    {
                        ["ITEMNO_ACC"] = itemNo,
                        ["CUSTOMER"] = Alpha(l.Customer, 10),
                        ["SP_GL_IND"] = SapRequestMapper.Code(l.SpecialGlIndicator),
                        ["C_CTR_AREA"] = SapRequestMapper.Code(l.CreditControlArea),
                        ["PMNTTRMS"] = SapRequestMapper.Code(l.PaymentTerms),
                        ["BLINE_DATE"] = SapRequestMapper.Dats(l.BaselineDate),
                        ["ALLOC_NMBR"] = SapRequestMapper.Text(l.Assignment),
                        ["ITEM_TEXT"] = SapRequestMapper.Text(l.ItemText),
                        ["TAX_CODE"] = SapRequestMapper.Code(l.TaxCode),
                        ["PROFIT_CTR"] = Alpha(l.ProfitCenter, 10)
                    });
                    break;

                case AccDocumentLineType.Tax:
                    tax.Add(new Dictionary<string, object?>
                    {
                        ["ITEMNO_ACC"] = itemNo,
                        ["TAX_CODE"] = SapRequestMapper.Code(l.TaxCode),
                        ["ACCT_KEY"] = SapRequestMapper.Code(l.TaxAccountKey),
                        ["COND_KEY"] = SapRequestMapper.Code(l.TaxConditionKey),
                        ["GL_ACCOUNT"] = Alpha(l.GlAccount, 10)
                    });
                    break;

                default:
                    throw new ArgumentException($"Dòng {i + 1}: LineType không hợp lệ.");
            }

            amounts.Add(new Dictionary<string, object?>
            {
                ["ITEMNO_ACC"] = itemNo,
                ["CURR_TYPE"] = "00",
                ["CURRENCY"] = currency,
                ["AMT_DOCCUR"] = sign * l.Amount,
                ["AMT_BASE"] = l.LineType == AccDocumentLineType.Tax ? sign * (l.TaxBaseAmount ?? 0m) : null
            });

            if (l.ProfitabilitySegment is not null)
                foreach (var (field, value) in l.ProfitabilitySegment)
                {
                    var name = field.Trim().ToUpperInvariant();
                    criteria.Add(new Dictionary<string, object?>
                    {
                        ["ITEMNO_ACC"] = itemNo,
                        ["FIELDNAME"] = name,
                        ["CHARACTER"] = name switch
                        {
                            "KNDNR" => Alpha(value, 10),
                            "ARTNR" => Alpha(value, 18),
                            _ => value.Trim().ToUpperInvariant()
                        }
                    });
                }
        }

        return new AccDocumentBapiInput(header, gl, ar, tax, amounts, criteria);
    }

    public static string ItemNo(int n) => n.ToString("0000000000");

    /// <summary>Mã toàn số → thêm 0 đầu đủ độ dài (thay CONVERSION_EXIT_ALPHA_INPUT); mã chữ → viết hoa.</summary>
    public static string? Alpha(string? value, int length)
    {
        var v = SapRequestMapper.Code(value);
        if (v is null) return null;
        return v.Length < length && v.All(char.IsDigit) ? v.PadLeft(length, '0') : v;
    }
}
