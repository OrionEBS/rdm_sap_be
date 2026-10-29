using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Rdm.Application.Abstractions;
using Rdm.Application.Customers;
using Rdm.Application.Postings;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Đọc dữ liệu SAP qua RFC_READ_TABLE. Toàn bộ lời gọi đọc bọc trong Polly (retry + circuit breaker) vì không có tác dụng phụ.
/// </summary>
public sealed partial class SapMasterDataClient : ISapMasterDataClient
{
    public static readonly string[] KnkkFields =
    {
        "KUNNR", "KKBER", "KLIMK", "KNKLI", "SAUFT", "SKFOR", "SSOBL", "UEDAT", "ERNAM",
        "ERDAT", "CTLPC", "KRAUS", "AEDAT", "AENAM", "CASHD", "CASHA", "CASHC"
    };

    private static readonly TimeSpan CurrencyCacheTtl = TimeSpan.FromHours(1);

    private readonly SapDestinationProvider _destinations;
    private readonly SapResilience _resilience;
    private readonly SapOptions _options;
    private readonly ILogger<SapMasterDataClient> _logger;
    private readonly object _cacheLock = new();
    private CurrencySettings? _currencies;

    public SapMasterDataClient(SapDestinationProvider destinations, SapResilience resilience,
        IOptions<SapOptions> options, ILogger<SapMasterDataClient> logger)
    {
        _destinations = destinations;
        _resilience = resilience;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CustomerCredit>> GetCustomerCreditsAsync(string? customer, string? creditControlArea, CancellationToken ct)
    {
        if (!UseCreditFunction()) return await ReadCreditsByTableAsync(customer, creditControlArea, ct);
        try
        {
            return await ReadCreditsByFunctionAsync(customer, creditControlArea, ct);
        }
        catch (SapReadException ex) when (ex.ErrorCode == CreditErrorCodes.FunctionMissing && CreditSourceIsAuto)
        {
            // Auto: hàm bị gỡ / chưa Remote-Enabled giữa 2 lần kiểm tra → đọc RFC_READ_TABLE, không làm hỏng lời gọi.
            return await ReadCreditsByTableAsync(customer, creditControlArea, ct);
        }
    }

    /// <summary>Đọc KNKK qua RFC_READ_TABLE + T014 / TCURX (cách cũ, dùng khi chưa có Z_ESD017_READ_KNKK).</summary>
    private Task<IReadOnlyList<CustomerCredit>> ReadCreditsByTableAsync(string? customer, string? creditControlArea, CancellationToken ct) =>
        ReadAsync(dest =>
        {
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(customer))
                where.Add(RfcTableReader.Condition("KUNNR", NormalizeCustomer(customer)));
            if (!string.IsNullOrWhiteSpace(creditControlArea))
                where.Add(RfcTableReader.Condition("KKBER", creditControlArea.Trim().ToUpperInvariant(), and: where.Count > 0));

            var rows = RfcTableReader.Read(dest, "KNKK", KnkkFields, where, Math.Max(100, _options.ReadTableBatchSize));
            var currencies = GetCurrencies(dest);
            IReadOnlyList<CustomerCredit> result = rows.Select(r => MapKnkk(r, currencies)).ToList();
            return result;
        }, ct);

    internal static CustomerCredit MapKnkk(IReadOnlyDictionary<string, string> r, CurrencySettings currencies)
    {
        var area = r["KKBER"];
        var areaCurrency = currencies.AreaCurrency(area);
        var paymentCurrency = RfcTableReader.NullIfEmpty(r["CASHC"]);
        decimal Amount(string field, string? currency) =>
            Normalize(RfcTableReader.ToExternalAmount(RfcTableReader.ParseDecimal(r[field]), currencies.Decimals(currency)));

        return new CustomerCredit
        {
            CustomerNumber = r["KUNNR"],
            CreditControlArea = area,
            Currency = areaCurrency,
            CreditLimit = Amount("KLIMK", areaCurrency),
            CreditAccount = RfcTableReader.NullIfEmpty(r["KNKLI"]),
            SalesValue = Amount("SAUFT", areaCurrency),
            Receivables = Amount("SKFOR", areaCurrency),
            SpecialLiabilities = Amount("SSOBL", areaCurrency),
            CreditLimitExceededOn = RfcTableReader.ParseDate(r["UEDAT"]),
            CreatedBy = RfcTableReader.NullIfEmpty(r["ERNAM"]),
            CreatedOn = RfcTableReader.ParseDate(r["ERDAT"]),
            RiskCategory = RfcTableReader.NullIfEmpty(r["CTLPC"]),
            CreditInfoNumber = RfcTableReader.NullIfEmpty(r["KRAUS"]),
            ChangedOn = RfcTableReader.ParseDate(r["AEDAT"]),
            ChangedBy = RfcTableReader.NullIfEmpty(r["AENAM"]),
            LastPaymentDate = RfcTableReader.ParseDate(r["CASHD"]),
            // CASHC rỗng khi chưa có thanh toán → dùng tiền tệ của credit control area.
            LastPaymentAmount = Amount("CASHA", paymentCurrency ?? areaCurrency),
            LastPaymentCurrency = paymentCurrency
        };
    }

    /// <summary>KUNNR toàn số → thêm 0 đầu đủ 10 ký tự (dạng lưu trong DB).</summary>
    public static string NormalizeCustomer(string customer)
    {
        var c = customer.Trim().ToUpperInvariant();
        return c.Length < 10 && c.All(char.IsDigit) ? c.PadLeft(10, '0') : c;
    }

    /// <summary>Bỏ số 0 thừa sau dấu thập phân (11294120.00 → 11294120).</summary>
    private static decimal Normalize(decimal d) => d / 1.000000000000000000000000000000000m;

    private CurrencySettings GetCurrencies(RfcDestination dest)
    {
        lock (_cacheLock)
        {
            if (_currencies is { } cached && DateTime.UtcNow - cached.LoadedAtUtc < CurrencyCacheTtl) return cached;
        }

        var areas = RfcTableReader.Read(dest, "T014", new[] { "KKBER", "WAERS" }, Array.Empty<string>(), 10_000)
            .ToDictionary(r => r["KKBER"], r => r["WAERS"], StringComparer.Ordinal);
        var decimals = RfcTableReader.Read(dest, "TCURX", new[] { "CURRKEY", "CURRDEC" }, Array.Empty<string>(), 10_000)
            .ToDictionary(r => r["CURRKEY"], r => int.Parse(r["CURRDEC"]), StringComparer.Ordinal);
        var fresh = new CurrencySettings(areas, decimals, DateTime.UtcNow);

        lock (_cacheLock) _currencies = fresh;
        _logger.LogInformation("Đã nạp {Areas} credit control area (T014) và {Currencies} tiền tệ có số lẻ đặc biệt (TCURX)",
            areas.Count, decimals.Count);
        return fresh;
    }

    private Task<T> ReadAsync<T>(Func<RfcDestination, T> read, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                return _resilience.Connect.Execute(() => read(_destinations.Get()));
            }
            catch (Exception ex) when (ex is RfcBaseException or BrokenCircuitException)
            {
                var code = ex switch
                {
                    RfcLogonException => SapErrorCodes.Logon,
                    RfcCommunicationException or RfcResourceException or BrokenCircuitException => SapErrorCodes.Unavailable,
                    _ => SapErrorCodes.Business
                };
                _logger.LogWarning(ex, "{BapiName} lỗi ({ErrorCode})", RfcTableReader.FunctionName, code);
                throw new SapReadException(code, RfcTableReader.FunctionName, ex.Message, ex);
            }
        }, ct);
}

/// <summary>Tiền tệ theo credit control area (T014) và số lẻ tiền tệ (TCURX; không có trong TCURX = 2).</summary>
public sealed record CurrencySettings(
    IReadOnlyDictionary<string, string> AreaCurrencies,
    IReadOnlyDictionary<string, int> CurrencyDecimals,
    DateTime LoadedAtUtc)
{
    public string? AreaCurrency(string area) =>
        AreaCurrencies.TryGetValue(area, out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;

    public int Decimals(string? currency) =>
        currency is not null && CurrencyDecimals.TryGetValue(currency, out var d) ? d : 2;
}
