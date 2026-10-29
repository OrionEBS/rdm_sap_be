using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Gọi SAP qua NCo 3.1. NCo là API đồng bộ nên mỗi lời gọi chạy trọn trong 1 Task.Run
/// (BeginContext/EndContext gắn theo thread, phải cùng thread với các Invoke).
///
/// Phân loại lỗi — quyết định client có được gửi lại hay không:
///   - Lỗi trước khi gửi lệnh (ping, logon, metadata, map dữ liệu) → Failed: chắc chắn SAP chưa ghi gì.
///   - Mất kết nối SAU khi đã gửi lệnh → Unknown: SAP có thể đã tạo chứng từ.
/// </summary>
public sealed partial class SapClient : ISapClient
{
    private const string Fb01Function = "Z_EFI011_CREATE_FB01";
    private const string Fb75Function = "Z_EFI009_CREATE_FB75";
    private const string Va01Function = "Z_ESD014_CREATE_VA01";
    private const string Vbn1Function = "Z_ESD012_CREATE_VBN1";
    private const string Va41Function = "Z_ESD013_CREATE_VA41";
    private const string ReversalFunction = "BAPI_ACC_DOCUMENT_REV_POST";

    private readonly SapDestinationProvider _destinations;
    private readonly SapResilience _resilience;
    private readonly SapOptions _options;
    private readonly ILogger<SapClient> _logger;

    public SapClient(SapDestinationProvider destinations, SapResilience resilience, IOptions<SapOptions> options, ILogger<SapClient> logger)
    {
        _destinations = destinations;
        _resilience = resilience;
        _options = options.Value;
        _logger = logger;
    }

    public Task<SapCallResult> CreateFb01Async(Fb01Request request, CancellationToken ct) =>
        CallSelfCommittingAsync(Fb01Function, "IS_FB01", SapRequestMapper.ToFb01(request), abapErrorMayFollowCommit: false, ct);

    public Task<SapCallResult> CreateFb75Async(Fb75Request request, CancellationToken ct) =>
        CallSelfCommittingAsync(Fb75Function, "IS_FB75", SapRequestMapper.ToFb75(request), abapErrorMayFollowCommit: false, ct);

    // Z_ESD014 gọi SAVE_TEXT SAU khi đã COMMIT đơn hàng; SAVE_TEXT lỗi sẽ raise MESSAGE
    // → exception ABAP lúc này không có nghĩa là chưa tạo đơn.
    public Task<SapCallResult> CreateVa01Async(Va01Request request, CancellationToken ct) =>
        CallSelfCommittingAsync(Va01Function, "IS_VA01", SapRequestMapper.ToVa01(request), abapErrorMayFollowCommit: true, ct);

    public Task<SapCallResult> CreateVa41Async(Va41Request request, CancellationToken ct) =>
        CallSelfCommittingAsync(Va41Function, "IS_VA41", SapRequestMapper.ToVa41(request), abapErrorMayFollowCommit: false, ct);

    // ET_RETURN của VBN1 là BDCMSGCOLL (batch input), không phải BAPIRET2.
    public Task<SapCallResult> CreateVbn1Async(Vbn1Request request, CancellationToken ct) =>
        CallSelfCommittingAsync(Vbn1Function, "IS_VBN1", SapRequestMapper.ToVbn1(request), abapErrorMayFollowCommit: false, ct,
            fn => SapResultInterpreter.InterpretBatchInput(ReadBdcReturn(fn.GetTable("ET_RETURN"))));

    /// <summary>
    /// BAPI_ACC_DOCUMENT_REV_POST không tự commit → BAPI + BAPI_TRANSACTION_COMMIT phải chạy trong cùng
    /// stateful session (BeginContext), nếu không update task bị rollback ngầm.
    /// </summary>
    public Task<SapCallResult> ReverseAccDocumentAsync(AccDocumentReversalRequest request, CancellationToken ct) =>
        RunAsync(ReversalFunction, stateful: true, (dest, ctx) =>
        {
            var original = ResolveOriginalDocument(dest, request, out var lookupError);
            if (original is null) return lookupError!;

            var fn = dest.Repository.CreateFunction(ReversalFunction);
            fn.SetValue("BUS_ACT", SapRequestMapper.Code(request.BusinessTransaction) ?? "RFBU");
            var rev = fn.GetStructure("REVERSAL");
            rev.SetValue("OBJ_TYPE", original.ObjectType);
            rev.SetValue("OBJ_KEY", original.ObjectKey);
            rev.SetValue("OBJ_KEY_R", original.ObjectKey);
            if (original.ObjectSystem is not null) rev.SetValue("OBJ_SYS", original.ObjectSystem);
            rev.SetValue("COMP_CODE", SapRequestMapper.Code(request.CompanyCode));
            rev.SetValue("REASON_REV", SapRequestMapper.Code(request.ReasonCode));
            if (request.PostingDate is not null) rev.SetValue("PSTNG_DATE", SapRequestMapper.Dats(request.PostingDate));
            if (request.FiscalPeriod is { } period) rev.SetValue("FIS_PERIOD", period.ToString("00"));

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

            var commit = dest.Repository.CreateFunction("BAPI_TRANSACTION_COMMIT");
            commit.SetValue("WAIT", "X");
            commit.InvokeAudited(dest);
            var commitMessages = ReadReturn(commit.GetStructure("RETURN"));
            var all = messages.Concat(commitMessages).ToList();
            if (commitMessages.Any(m => m.IsError))
                return SapCallResult.Failed(SapErrorCodes.Business,
                    "BAPI_TRANSACTION_COMMIT lỗi: " + string.Join("; ", commitMessages.Select(m => m.Message)), all);

            var objKey = fn.GetString("OBJ_KEY");
            var document = !string.IsNullOrWhiteSpace(objKey)
                ? SapResultInterpreter.FromObjectKey(fn.GetString("OBJ_TYPE"), objKey, fn.GetString("OBJ_SYS"))
                : SapResultInterpreter.ExtractDocument(all);

            return document is null
                ? SapCallResult.Unknown("Đã commit nhưng SAP không trả số chứng từ đảo — kiểm tra trên SAP.", all)
                : SapCallResult.Success(document, all);
        }, ct);

    public Task<SapSystemInfo> PingAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var dest = _destinations.Get();
            var fn = dest.Repository.CreateFunction("RFC_SYSTEM_INFO");
            fn.InvokeAudited(dest);
            var si = fn.GetStructure("RFCSI_EXPORT");
            return new SapSystemInfo(
                si.GetString("RFCSYSID"), dest.Client, si.GetString("RFCHOST"), si.GetString("RFCSAPRL"),
                dest.User, (int)sw.ElapsedMilliseconds);
        }, ct);

    // ------------------------------------------------------------------ helpers

    private Task<SapCallResult> CallSelfCommittingAsync(
        string functionName, string importName, IReadOnlyDictionary<string, object?> fields,
        bool abapErrorMayFollowCommit, CancellationToken ct, Func<IRfcFunction, SapCallResult>? interpret = null) =>
        RunAsync(functionName, stateful: false, (dest, ctx) =>
        {
            var fn = dest.Repository.CreateFunction(functionName);
            var input = fn.GetStructure(importName);
            foreach (var (name, value) in fields)
                if (value is not null) input.SetValue(name, value);

            var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit);
            if (invokeError is not null) return invokeError;

            return interpret is not null
                ? interpret(fn)
                : SapResultInterpreter.InterpretSelfCommitting(ReadReturn(fn.GetTable("ET_RETURN")));
        }, ct);

    private Task<SapCallResult> RunAsync(
        string functionName, bool stateful, Func<RfcDestination, CallContext, SapCallResult> body, CancellationToken ct) =>
        Task.Run(() =>
        {
            var ctx = new CallContext();
            RfcDestination? dest = null;
            var contextStarted = false;
            try
            {
                dest = _resilience.Connect.Execute(() =>
                {
                    var d = _destinations.Get();
                    d.Ping();
                    return d;
                });
                if (stateful)
                {
                    RfcSessionManager.BeginContext(dest);
                    contextStarted = true;
                }
                return body(dest, ctx);
            }
            catch (Exception ex) when (!ctx.Sent)
            {
                return ClassifyBeforeSend(functionName, ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{BapiName}: lỗi sau khi đã gửi lệnh sang SAP", functionName);
                return SapCallResult.Unknown($"Lỗi sau khi đã gửi lệnh sang SAP ({ex.GetType().Name}): {ex.Message}");
            }
            finally
            {
                if (contextStarted)
                {
                    try { RfcSessionManager.EndContext(dest!); }
                    catch (Exception ex) { _logger.LogWarning(ex, "{BapiName}: EndContext lỗi", functionName); }
                }
            }
        }, ct);

    private SapCallResult ClassifyBeforeSend(string functionName, Exception ex)
    {
        var (code, message) = ex switch
        {
            RfcLogonException => (SapErrorCodes.Logon, $"Đăng nhập SAP thất bại: {ex.Message}"),
            BrokenCircuitException => (SapErrorCodes.Unavailable,
                "SAP tạm thời không khả dụng (circuit breaker đang mở sau chuỗi lỗi kết nối). Thử lại sau."),
            RfcCommunicationException or RfcResourceException => (SapErrorCodes.Unavailable, $"Không kết nối được SAP: {ex.Message}"),
            RfcTypeConversionException => (SapErrorCodes.InvalidData, $"Giá trị không hợp lệ với kiểu field SAP: {ex.Message}"),
            RfcAbapBaseException => (SapErrorCodes.Business, ex.Message),
            _ => (SapErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}")
        };
        _logger.LogWarning(ex, "{BapiName}: lỗi trước khi gửi lệnh ({ErrorCode}) — SAP chưa ghi gì", functionName, code);
        return SapCallResult.Failed(code, message);
    }

    /// <summary>Invoke 1 lần, không retry. Trả null nếu thành công.</summary>
    private static SapCallResult? Invoke(IRfcFunction fn, RfcDestination dest, CallContext ctx, bool abapErrorMayFollowCommit)
    {
        ctx.Sent = true;
        var name = fn.Metadata.Name;
        try
        {
            fn.InvokeAudited(dest);
            return null;
        }
        catch (RfcLogonException ex)
        {
            return SapCallResult.Failed(SapErrorCodes.Logon, $"Đăng nhập SAP thất bại: {ex.Message}");
        }
        catch (RfcAbapBaseException ex) when (!abapErrorMayFollowCommit)
        {
            return SapCallResult.Failed(SapErrorCodes.Business, ex.Message, new[] { AbapMessage(ex) });
        }
        catch (RfcAbapBaseException ex)
        {
            return SapCallResult.Unknown(
                $"{name} raise lỗi ABAP ở bước có thể đã sau COMMIT — kiểm tra trên SAP: {ex.Message}", new[] { AbapMessage(ex) });
        }
        catch (RfcCommunicationException ex)
        {
            return SapCallResult.Unknown($"Mất kết nối SAP trong khi thực thi {name}: {ex.Message}");
        }
    }

    private static SapReturnMessage AbapMessage(RfcAbapBaseException ex) =>
        new("E", "ABAP", string.Empty, ex.Message);

    private void Rollback(RfcDestination dest)
    {
        try { dest.Repository.CreateFunction("BAPI_TRANSACTION_ROLLBACK").InvokeAudited(dest); }
        catch (Exception ex) { _logger.LogWarning(ex, "BAPI_TRANSACTION_ROLLBACK lỗi"); }
    }

    private sealed record OriginalDocument(string ObjectType, string ObjectKey, string? ObjectSystem);

    /// <summary>
    /// Lấy AWTYP/AWKEY/AWSYS của chứng từ gốc từ BKPF (trừ khi client truyền sẵn) và chặn sớm chứng từ đã bị đảo (STBLG).
    /// Chạy trước khi gửi lệnh ghi → lỗi ở đây là Failed.
    /// </summary>
    private static OriginalDocument? ResolveOriginalDocument(RfcDestination dest, AccDocumentReversalRequest r, out SapCallResult? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(r.ObjectType) && !string.IsNullOrWhiteSpace(r.ObjectKey))
            return new OriginalDocument(r.ObjectType.Trim().ToUpperInvariant(), r.ObjectKey.Trim(),
                string.IsNullOrWhiteSpace(r.ObjectSystem) ? null : r.ObjectSystem.Trim().ToUpperInvariant());

        var bukrs = SapRequestMapper.Code(r.CompanyCode)!;
        var belnr = r.DocumentNumber.Trim();
        if (belnr.All(char.IsDigit)) belnr = belnr.PadLeft(10, '0');
        var gjahr = r.FiscalYear.Trim();

        var fn = dest.Repository.CreateFunction("RFC_READ_TABLE");
        fn.SetValue("QUERY_TABLE", "BKPF");
        fn.SetValue("DELIMITER", "|");
        fn.SetValue("ROWCOUNT", 1);
        var options = fn.GetTable("OPTIONS");
        foreach (var line in new[] { $"BUKRS = '{Escape(bukrs)}'", $"AND BELNR = '{Escape(belnr)}'", $"AND GJAHR = '{Escape(gjahr)}'" })
        {
            options.Append();
            options.SetValue("TEXT", line);
        }
        var fields = fn.GetTable("FIELDS");
        foreach (var f in new[] { "AWTYP", "AWKEY", "AWSYS", "STBLG", "STJAH" })
        {
            fields.Append();
            fields.SetValue("FIELDNAME", f);
        }
        fn.InvokeAudited(dest);

        var data = fn.GetTable("DATA");
        if (data.RowCount == 0)
        {
            error = SapCallResult.Failed(SapErrorCodes.Business, $"Không tìm thấy chứng từ {belnr} / {bukrs} / {gjahr} trên SAP.");
            return null;
        }
        data.CurrentIndex = 0;
        var cols = data.GetString("WA").Split('|').Select(c => c.Trim()).ToArray();
        string Col(int i) => i < cols.Length ? cols[i] : string.Empty;

        if (!string.IsNullOrEmpty(Col(3)))
        {
            error = SapCallResult.Failed(SapErrorCodes.Business,
                $"Chứng từ {belnr} / {bukrs} / {gjahr} đã được đảo bởi chứng từ {Col(3)} / {Col(4)}.");
            return null;
        }
        if (string.IsNullOrEmpty(Col(0)) || string.IsNullOrEmpty(Col(1)))
        {
            error = SapCallResult.Failed(SapErrorCodes.Business,
                $"Chứng từ {belnr} / {bukrs} / {gjahr} không có AWTYP/AWKEY — không đảo được bằng BAPI_ACC_DOCUMENT_REV_POST.");
            return null;
        }
        return new OriginalDocument(Col(0), Col(1), string.IsNullOrEmpty(Col(2)) ? null : Col(2));
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private static List<SapReturnMessage> ReadReturn(IRfcTable table)
    {
        var result = new List<SapReturnMessage>(table.RowCount);
        foreach (IRfcStructure row in table)
        {
            var m = ReadReturnRow(row);
            if (m is not null) result.Add(m);
        }
        return result;
    }

    private static List<SapReturnMessage> ReadReturn(IRfcStructure structure) =>
        ReadReturnRow(structure) is { } m ? new List<SapReturnMessage> { m } : new List<SapReturnMessage>();

    /// <summary>Đọc 1 dòng message kiểu BAPIRET2 / BAPI_MATRETURN2… (field không có trong structure → null).</summary>
    private static SapReturnMessage? ReadReturnRow(IRfcStructure row)
    {
        string? Get(string name) => row.Metadata.TryNameToIndex(name) >= 0 ? NullIfEmpty(row.GetString(name)) : null;

        var type = Get("TYPE");
        if (type is null) return null;
        return new SapReturnMessage(
            type,
            Get("ID") ?? string.Empty,
            Get("NUMBER") ?? string.Empty,
            Get("MESSAGE") ?? string.Empty,
            Get("MESSAGE_V1"),
            Get("MESSAGE_V2"),
            Get("MESSAGE_V3"),
            Get("MESSAGE_V4"),
            Get("PARAMETER"),
            Get("FIELD"));
    }

    /// <summary>BDCMSGCOLL: hàm Z đã format sẵn text message vào MSGV1.</summary>
    private static List<SapReturnMessage> ReadBdcReturn(IRfcTable table)
    {
        var result = new List<SapReturnMessage>(table.RowCount);
        foreach (IRfcStructure row in table)
        {
            var type = row.GetString("MSGTYP");
            if (string.IsNullOrWhiteSpace(type)) continue;
            var screen = NullIfEmpty($"{row.GetString("DYNAME")} {row.GetString("DYNUMB")}");
            result.Add(new SapReturnMessage(
                type,
                row.GetString("MSGID"),
                row.GetString("MSGNR"),
                row.GetString("MSGV1"),
                MessageV2: NullIfEmpty(row.GetString("MSGV2")),
                MessageV3: NullIfEmpty(row.GetString("MSGV3")),
                MessageV4: NullIfEmpty(row.GetString("MSGV4")),
                Parameter: screen,
                Field: NullIfEmpty(row.GetString("FLDNAME"))));
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class CallContext
    {
        /// <summary>true khi đã bắt đầu gửi lệnh ghi sang SAP.</summary>
        public bool Sent;
    }
}
