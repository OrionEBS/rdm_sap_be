using System.Globalization;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Rdm.Application.Abstractions;
using Rdm.Application.Customers;
using Rdm.Application.Postings;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Đọc KNKK qua hàm RFC Z_ESD017_READ_KNKK (Sap:CustomerCreditFunction, source abap/Z_ESD017_READ_KNKK.abap):
/// SAP trả sẵn số tiền giá trị thực + tiền tệ area (không phải đọc T014 / TCURX), kiểm tra quyền F_KNKA_KKB,
/// tự chuyển ALPHA mã khách hàng. Sap:CustomerCreditSource = Auto: chưa có hàm trên SAP thì dùng RFC_READ_TABLE như cũ.
/// </summary>
public sealed partial class SapMasterDataClient
{
    private static readonly TimeSpan CreditFunctionRecheck = TimeSpan.FromMinutes(10);
    private bool? _creditFunctionExists;
    private DateTime _creditFunctionCheckedAtUtc;

    private bool CreditSourceIsAuto =>
        !(_options.CustomerCreditSource?.Trim() is { } s && (s.Equals("Function", StringComparison.OrdinalIgnoreCase)
                                                             || s.Equals("ReadTable", StringComparison.OrdinalIgnoreCase)));

    private bool UseCreditFunction()
    {
        var source = _options.CustomerCreditSource?.Trim() ?? "Auto";
        if (source.Equals("ReadTable", StringComparison.OrdinalIgnoreCase)) return false;
        if (source.Equals("Function", StringComparison.OrdinalIgnoreCase)) return true;
        return CreditFunctionExists();
    }

    /// <summary>
    /// Hàm đã có trên SAP VÀ là Remote-Enabled (TFDIR-FMODE = R)? Hàm thường (chưa tick Remote-Enabled) vẫn đọc được metadata nhưng
    /// gọi RFC bị từ chối → coi như chưa có. Kết quả nhớ 10 phút (hàm mới kích hoạt được dùng sau tối đa 10 phút, không cần khởi động lại).
    /// </summary>
    private bool CreditFunctionExists()
    {
        lock (_cacheLock)
        {
            if (_creditFunctionExists is { } known && DateTime.UtcNow - _creditFunctionCheckedAtUtc < CreditFunctionRecheck) return known;
        }
        bool exists;
        try
        {
            exists = _resilience.Connect.Execute(() =>
            {
                var dest = _destinations.Get();
                var tfdir = RfcTableReader.Read(dest, "TFDIR", new[] { "FUNCNAME", "FMODE" },
                    new[] { RfcTableReader.Condition("FUNCNAME", _options.CustomerCreditFunction) }, 10);
                return tfdir.Count == 1 && tfdir[0]["FMODE"] == "R";
            });
        }
        catch (RfcBaseException ex) when (ex.Message.Contains("FU_NOT_FOUND", StringComparison.Ordinal))
        {
            exists = false;
        }
        catch (Exception ex) when (ex is RfcBaseException or BrokenCircuitException)
        {
            // SAP không kết nối được: không kết luận — để lời gọi đọc thật báo lỗi kết nối.
            return false;
        }
        lock (_cacheLock)
        {
            if (_creditFunctionExists != exists)
                _logger.LogInformation("KNKK: {Function} {State} trên SAP → đọc bằng {Source}", _options.CustomerCreditFunction,
                    exists ? "đã có" : "chưa có", exists ? _options.CustomerCreditFunction : RfcTableReader.FunctionName);
            _creditFunctionExists = exists;
            _creditFunctionCheckedAtUtc = DateTime.UtcNow;
        }
        return exists;
    }

    private Task<IReadOnlyList<CustomerCredit>> ReadCreditsByFunctionAsync(string? customer, string? creditControlArea, CancellationToken ct) =>
        Task.Run(() =>
        {
            var name = _options.CustomerCreditFunction;
            IRfcFunction fn;
            try
            {
                fn = _resilience.Connect.Execute(() =>
                {
                    var dest = _destinations.Get();
                    var f = dest.Repository.CreateFunction(name);
                    if (!string.IsNullOrWhiteSpace(customer)) f.SetValue("IV_KUNNR", customer.Trim().ToUpperInvariant());
                    if (!string.IsNullOrWhiteSpace(creditControlArea)) f.SetValue("IV_KKBER", creditControlArea.Trim().ToUpperInvariant());
                    f.InvokeAudited(dest);
                    return f;
                });
            }
            // NCo 3.1: hàm chưa có → RfcInvalidStateException "metadata for function ... not available: FU_NOT_FOUND ...";
            // hàm có nhưng chưa Remote-Enabled → RfcAbapRuntimeException "... cannot be used for 'remote' calls".
            catch (RfcBaseException ex) when (ex.Message.Contains("FU_NOT_FOUND", StringComparison.Ordinal)
                                               || ex.Message.Contains("cannot be used for 'remote' calls", StringComparison.OrdinalIgnoreCase))
            {
                throw CreditFunctionMissing(ex);
            }
            catch (Exception ex) when (ex is RfcBaseException or BrokenCircuitException)
            {
                var code = ex switch
                {
                    RfcLogonException => SapErrorCodes.Logon,
                    RfcCommunicationException or RfcResourceException or BrokenCircuitException => SapErrorCodes.Unavailable,
                    _ => SapErrorCodes.Business
                };
                _logger.LogWarning(ex, "{BapiName} lỗi ({ErrorCode})", name, code);
                throw new SapReadException(code, name, ex.Message, ex);
            }

            var messages = fn.GetTable("ET_RETURN").Select(r => new CreditMessage(
                r.GetString("TYPE"), r.GetString("PARAMETER"), r.GetString("FIELD"), r.GetString("MESSAGE"))).ToList();
            foreach (var w in messages.Where(m => m.Type == "W"))
                _logger.LogWarning("{BapiName}: {Code} {Message}", name, w.Code, w.Message);
            if (InterpretCreditMessages(messages, name) is { } error) throw error;

            IReadOnlyList<CustomerCredit> items = fn.GetTable("ET_KNKK").Select(r => MapCreditRow(ToStrings(r))).ToList();
            return items;
        }, ct);

    private SapReadException CreditFunctionMissing(Exception ex)
    {
        lock (_cacheLock)
        {
            _creditFunctionExists = false;
            _creditFunctionCheckedAtUtc = DateTime.UtcNow;
        }
        _logger.LogWarning(ex, "{BapiName}: chưa có trên SAP", _options.CustomerCreditFunction);
        return new SapReadException(CreditErrorCodes.FunctionMissing, _options.CustomerCreditFunction,
            $"Hàm RFC {_options.CustomerCreditFunction} chưa có trên SAP hoặc chưa đặt Processing Type = Remote-Enabled Module " +
            "(SE37 → Attributes) — xem abap/Z_ESD017_READ_KNKK.md, " +
            "hoặc đặt Sap:CustomerCreditSource = Auto / ReadTable.", ex);
    }

    internal sealed record CreditMessage(string Type, string Code, string Field, string Message);

    /// <summary>ET_RETURN → lỗi: E/A có mã RDM (CREDIT_*) giữ nguyên mã; E/A khác → SAP_BUSINESS. I / W / S không phải lỗi.</summary>
    internal static SapReadException? InterpretCreditMessages(IReadOnlyList<CreditMessage> messages, string functionName)
    {
        var error = messages.FirstOrDefault(m => m.Type is "E" or "A");
        if (error is null) return null;
        var code = error.Code.StartsWith("CREDIT_", StringComparison.Ordinal) ? error.Code : SapErrorCodes.Business;
        return new SapReadException(code, functionName, string.IsNullOrWhiteSpace(error.Message) ? code : error.Message);
    }

    /// <summary>Dòng ZSRDM_S_KNKK → chuỗi theo tên field (NCo GetString: số thập phân dấu chấm, ngày yyyy-MM-dd hoặc yyyyMMdd).</summary>
    private static Dictionary<string, string> ToStrings(IRfcStructure row)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < row.Metadata.FieldCount; i++)
            d[row.Metadata[i].Name] = row.GetString(i);
        return d;
    }

    /// <summary>Số tiền trong ZSRDM_S_KNKK đã là giá trị thực (Z_ESD017 quy đổi theo TCURX) — không dịch số lẻ lần nữa.</summary>
    internal static CustomerCredit MapCreditRow(IReadOnlyDictionary<string, string> r)
    {
        string? S(string f) => r.TryGetValue(f, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        decimal D(string f) => S(f) is { } v ? Normalize(decimal.Parse(v.EndsWith('-') ? "-" + v[..^1] : v, NumberStyles.Number, CultureInfo.InvariantCulture)) : 0m;
        DateOnly? Date(string f)
        {
            var v = S(f)?.Replace("-", "", StringComparison.Ordinal);
            return v is { Length: 8 } && v != "00000000" && DateOnly.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
        }

        return new CustomerCredit
        {
            CustomerNumber = S("KUNNR") ?? string.Empty,
            CreditControlArea = S("KKBER") ?? string.Empty,
            Currency = S("WAERS"),
            CreditLimit = D("KLIMK"),
            CreditAccount = S("KNKLI"),
            SalesValue = D("SAUFT"),
            Receivables = D("SKFOR"),
            SpecialLiabilities = D("SSOBL"),
            CreditLimitExceededOn = Date("UEDAT"),
            CreatedBy = S("ERNAM"),
            CreatedOn = Date("ERDAT"),
            RiskCategory = S("CTLPC"),
            CreditInfoNumber = S("KRAUS"),
            ChangedOn = Date("AEDAT"),
            ChangedBy = S("AENAM"),
            LastPaymentDate = Date("CASHD"),
            LastPaymentAmount = D("CASHA"),
            LastPaymentCurrency = S("CASHC")
        };
    }
}
