using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// BAPI_ACC_DOCUMENT_POST / BAPI_ACC_DOCUMENT_CHECK — chứng từ FI nhiều dòng.
///   - POST không tự commit → POST + BAPI_TRANSACTION_COMMIT trong cùng stateful session (như đảo chứng từ).
///   - Trước khi gửi lệnh: chặn trùng theo BKPF (Company code + loại CT + năm tài chính + XBLNR, chưa đảo) — vì requestId chỉ
///     chống trùng phía API, còn RDM có thể gửi cùng chứng từ bằng requestId khác. Năm tài chính lấy từ SAP
///     (BAPI_COMPANYCODE_GET_PERIOD): G210 không theo năm dương lịch (15/10/2026 thuộc FY 2027).
///   - USERNAME bắt buộc trong DOCUMENTHEADER; Tafico có validation ZFI 010 bắt buộc C_CTR_AREA trên dòng KH;
///     DRF-930 chưa cấu hình PA transfer structure FI (KF 018) → TK 311xxxxx gán cost center, chưa gán CO-PA được.
///   - Không có BAdI ACC_DOCUMENT / BTE RWBAPI01 của khách hàng trên DRF-930 → posting key do SAP tự xác định.
/// </summary>
public sealed partial class SapClient
{
    private const string AccDocumentPostFunction = "BAPI_ACC_DOCUMENT_POST";
    private const string AccDocumentCheckFunction = "BAPI_ACC_DOCUMENT_CHECK";

    public Task<SapCallResult> PostAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct) =>
        RunAsync(AccDocumentPostFunction, stateful: true, (dest, ctx) =>
        {
            var input = AccDocumentMapper.Map(request);

            if (!request.AllowDuplicateReference)
            {
                var duplicate = FindPostedDocument(dest, request);
                if (duplicate is not null)
                    return SapCallResult.Failed(SapErrorCodes.Business,
                        $"Đã có chứng từ {duplicate} (chưa đảo) cùng company code / loại CT / năm với Reference '{request.Reference.Trim()}'. " +
                        "Kiểm tra trên SAP; nếu thật sự cần tạo thêm, gửi AllowDuplicateReference = true.");
            }

            var fn = dest.Repository.CreateFunction(AccDocumentPostFunction);
            Fill(fn, input, _options.User);

            var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit: false);
            if (invokeError is not null)
            {
                Rollback(dest);
                return invokeError;
            }

            var messages = ReadReturn(fn.GetTable("RETURN"));
            var errors = messages.Where(m => m.IsError).ToList();
            if (errors.Count > 0)
            {
                Rollback(dest);
                return SapCallResult.Failed(SapErrorCodes.Business, string.Join("; ", errors.Select(e => e.Message)), messages);
            }

            return Commit(dest, messages, all =>
            {
                var objKey = fn.GetString("OBJ_KEY");
                var document = !string.IsNullOrWhiteSpace(objKey) && objKey.Trim() != "$"
                    ? SapResultInterpreter.FromObjectKey(fn.GetString("OBJ_TYPE"), objKey, fn.GetString("OBJ_SYS"))
                    : SapResultInterpreter.ExtractDocument(all);
                return document is null
                    ? SapCallResult.Unknown("Đã commit nhưng SAP không trả số chứng từ — kiểm tra trên SAP.", all)
                    : SapCallResult.Success(document, all);
            });
        }, ct);

    /// <summary>Chỉ kiểm tra (không ghi). Lỗi kết nối giữa chừng vẫn là Failed vì CHECK không tạo gì trên SAP.</summary>
    public async Task<SapCallResult> CheckAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct)
    {
        var result = await RunAsync(AccDocumentCheckFunction, stateful: false, (dest, ctx) =>
        {
            var fn = dest.Repository.CreateFunction(AccDocumentCheckFunction);
            Fill(fn, AccDocumentMapper.Map(request), _options.User);

            var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit: false);
            if (invokeError is not null) return invokeError;

            var messages = ReadReturn(fn.GetTable("RETURN"));
            var errors = messages.Where(m => m.IsError).ToList();
            return errors.Count > 0
                ? SapCallResult.Failed(SapErrorCodes.Business, string.Join("; ", errors.Select(e => e.Message)), messages)
                : SapCallResult.Success(new SapDocumentRef(null), messages);
        }, ct);

        return result.Status == PostingStatus.Unknown
            ? SapCallResult.Failed(SapErrorCodes.Unavailable, result.ErrorMessage ?? "Mất kết nối SAP khi kiểm tra.", result.Messages)
            : result;
    }

    /// <summary>USERNAME là field bắt buộc của DOCUMENTHEADER (RW 602) — dùng user RFC đang logon.</summary>
    private static void Fill(IRfcFunction fn, AccDocumentBapiInput input, string sapUser)
    {
        var header = fn.GetStructure("DOCUMENTHEADER");
        SetFields(header, input.Header);
        header.SetValue("USERNAME", sapUser.Trim().ToUpperInvariant());
        AppendRows(fn.GetTable("ACCOUNTGL"), input.AccountGl);
        AppendRows(fn.GetTable("ACCOUNTRECEIVABLE"), input.AccountReceivable);
        AppendRows(fn.GetTable("ACCOUNTTAX"), input.AccountTax);
        AppendRows(fn.GetTable("CURRENCYAMOUNT"), input.CurrencyAmount);
        AppendRows(fn.GetTable("CRITERIA"), input.Criteria);
    }

    /// <summary>Số chứng từ chưa đảo cùng BUKRS + BLART + GJAHR (năm tài chính của ngày hạch toán) + XBLNR; null nếu không có.</summary>
    private static string? FindPostedDocument(RfcDestination dest, AccDocumentPostRequest r)
    {
        var bukrs = SapRequestMapper.Code(r.CompanyCode)!;
        var period = dest.Repository.CreateFunction("BAPI_COMPANYCODE_GET_PERIOD");
        period.SetValue("COMPANYCODEID", bukrs);
        period.SetValue("POSTING_DATE", SapRequestMapper.Dats(r.PostingDate));
        period.InvokeAudited(dest);
        var periodError = period.GetStructure("RETURN");
        if (periodError.GetString("TYPE") is "E" or "A")
            throw new InvalidOperationException($"Không xác định được năm tài chính: {periodError.GetString("MESSAGE")}");
        var fiscalYear = period.GetString("FISCAL_YEAR");

        var rows = RfcTableReader.Read(dest, "BKPF", new[] { "BELNR", "GJAHR", "STBLG" },
            new[]
            {
                RfcTableReader.Condition("BUKRS", bukrs),
                RfcTableReader.Condition("GJAHR", fiscalYear, and: true),
                RfcTableReader.Condition("BLART", SapRequestMapper.Code(r.DocumentType)!, and: true),
                RfcTableReader.Condition("XBLNR", r.Reference.Trim(), and: true)
            }, 50);
        return rows.Where(row => row["STBLG"].Length == 0).Select(row => $"{row["BELNR"]}/{row["GJAHR"]}").FirstOrDefault();
    }

    // ------------------------------------------------------------------ helpers dùng chung cho BAPI không tự commit

    /// <summary>BAPI_TRANSACTION_COMMIT (WAIT) cùng session; commit lỗi → Failed (update task không chạy).</summary>
    private static SapCallResult Commit(
        RfcDestination dest, List<SapReturnMessage> messages, Func<IReadOnlyList<SapReturnMessage>, SapCallResult> onSuccess)
    {
        var commit = dest.Repository.CreateFunction("BAPI_TRANSACTION_COMMIT");
        commit.SetValue("WAIT", "X");
        commit.InvokeAudited(dest);
        var commitMessages = ReadReturn(commit.GetStructure("RETURN"));
        var all = messages.Concat(commitMessages).ToList();
        return commitMessages.Any(m => m.IsError)
            ? SapCallResult.Failed(SapErrorCodes.Business,
                "BAPI_TRANSACTION_COMMIT lỗi: " + string.Join("; ", commitMessages.Select(m => m.Message)), all)
            : onSuccess(all);
    }

    private static void SetFields(IRfcStructure target, IReadOnlyDictionary<string, object?> fields)
    {
        foreach (var (name, value) in fields)
            if (value is not null) target.SetValue(name, value);
    }

    private static void AppendRows(IRfcTable table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        foreach (var row in rows)
        {
            table.Append();
            foreach (var (name, value) in row)
                if (value is not null) table.SetValue(name, value);
        }
    }
}
