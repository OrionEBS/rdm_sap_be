namespace Rdm.Sap;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>Giá trị dòng điều kiện (KONP dòng 01) gửi cho BAPI. ReleaseStatus = KFRST (trống = đã duyệt, A = chờ duyệt).</summary>
public sealed record PriceBapiItemValues(
    decimal Amount, string Currency, int PricingUnit, string Unit, string CalculationType, string ReleaseStatus, bool Delete);

/// <summary>Các bảng của 1 lần gọi BAPI_PRICES_CONDITIONS cho 1 condition record.</summary>
public sealed record PriceBapiTables(
    IReadOnlyList<Row> ConditionTables, IReadOnlyList<Row> Headers, IReadOnlyList<Row> Items, bool PhysicalDeletion = false);

/// <summary>
/// Map sang TI_BAPICONDCT (dòng bảng Axxx) / TI_BAPICONDHD (KONH) / TI_BAPICONDIT (KONP) của BAPI_PRICES_CONDITIONS.
/// Hành vi BAPI kiểm chứng trên DRF-930 (đọc source LCND_PRICES_INBOUND* + chạy thử):
///   - KFRST (release status) nằm TRONG VARKEY: ký tự cuối, sau các trường khóa (T681E: A990 sau WERKS, A991 / A919 sau MATNR,
///     A912 sau INCO1). BAPI so VAKEY mới với VAKEY cũ (KONH) để chuyển dòng Axxx sang KFRST mới → duyệt / gỡ duyệt = 004 + VARKEY
///     có KFRST mới. Trường BAPICONDIT-STATUS chỉ ghi vào KONP, KHÔNG đổi KFRST. Mọi thao tác 004 phải giữ KFRST hiện tại trong VARKEY.
///   - Đổi hiệu lực đến: BAPI lấy DATBI cũ từ bộ đệm ánh xạ KNUMH (chỉ có khi trao đổi với CRM) → không tự xóa dòng cũ, sinh thêm
///     dòng chồng hiệu lực. Cách đúng: trong CÙNG 1 lần gọi gửi dòng 004 kỳ mới TRƯỚC, rồi dòng 003 kỳ cũ, PI_PHYSICAL_DELETION = X
///     (003 đứng trước sẽ để lại cờ xóa LOEVM_KO trên KONP).
///   - Hủy hiệu lực = 004 + INDIDELETE = X (cờ xóa LOEVM_KO như VK12).
///   - COND_VALUE là số thực (BAPI tự đổi sang dạng lưu nội bộ theo TCURX).
/// </summary>
public static class PriceBapiMapper
{
    public const string OperationCreate = "009";
    public const string OperationChange = "004";
    public const string OperationDelete = "003";
    public const string TemporaryNumber = "$000000001";

    /// <summary>VARKEY = trường khóa (độ dài cố định) + KFRST (1 ký tự).</summary>
    public static string VarKey(PriceKey key, string? releaseStatus) =>
        key.VarKey + (string.IsNullOrEmpty(releaseStatus) ? " " : releaseStatus[..1]);

    public static PriceBapiTables Create(PriceKey key, DateOnly from, DateOnly to, PriceBapiItemValues values, string createdBy, DateOnly today) =>
        new(
            new[] { ConditionTable(OperationCreate, TemporaryNumber, key, values.ReleaseStatus, from, to) },
            new[] { Header(OperationCreate, TemporaryNumber, key, values.ReleaseStatus, from, to, createdBy, today) },
            new[] { Item(OperationCreate, TemporaryNumber, key.ConditionType, values) });

    /// <summary>
    /// Sửa 1 record. periods = mọi kỳ hiệu lực hiện có (record bị SAP tách có nhiều dòng Axxx cùng KNUMH);
    /// kỳ có NewTo khác To → đổi hiệu lực đến (004 kỳ mới, sau đó 003 kỳ cũ, xóa vật lý).
    /// </summary>
    public static PriceBapiTables Change(string knumh, PriceKey key,
        IReadOnlyList<(DateOnly From, DateOnly To, DateOnly NewTo)> periods, PriceBapiItemValues values)
    {
        var status = values.ReleaseStatus;
        var rows = periods.Select(p => ConditionTable(OperationChange, knumh, key, status, p.From, p.NewTo)).ToList();
        var moved = periods.Where(p => p.NewTo != p.To).ToList();
        rows.AddRange(moved.Select(p => ConditionTable(OperationDelete, knumh, key, status, p.From, p.To)));
        return new(
            rows,
            new[] { Header(OperationChange, knumh, key, status, periods.Min(p => p.From), periods.Max(p => p.NewTo), null, null) },
            new[] { Item(OperationChange, knumh, key.ConditionType, values) },
            PhysicalDeletion: moved.Count > 0);
    }

    /// <summary>
    /// Đổi số tạm ($...) của 1 bảng giá tạo mới khi gom nhiều bảng giá vào 1 lần gọi BAPI: mỗi bảng giá 1 số tạm riêng
    /// ($000000001, $000000002…) — số thật lấy qua TO_BAPIKNUMHS (COND_NO_OLD → COND_NO_NEW).
    /// </summary>
    public static PriceBapiTables WithTemporaryNumber(PriceBapiTables tables, string temporaryNumber)
    {
        IReadOnlyList<Row> Renumber(IReadOnlyList<Row> rows) => rows
            .Select(r => (Row)r.ToDictionary(kv => kv.Key,
                kv => kv.Key == "COND_NO" && kv.Value is string s && s.StartsWith('$') ? temporaryNumber : kv.Value))
            .ToList();
        return tables with
        {
            ConditionTables = Renumber(tables.ConditionTables), Headers = Renumber(tables.Headers), Items = Renumber(tables.Items)
        };
    }

    private static Row ConditionTable(string operation, string knumh, PriceKey key, string release, DateOnly from, DateOnly to) =>
        new Dictionary<string, object?>
        {
            ["OPERATION"] = operation,
            ["COND_USAGE"] = PriceConditionRules.Usage,
            ["TABLE_NO"] = key.Table.Number,
            ["APPLICATIO"] = PriceConditionRules.Application,
            ["COND_TYPE"] = key.ConditionType,
            ["VARKEY"] = VarKey(key, release),
            ["VALID_FROM"] = SapRequestMapper.Dats(from),
            ["VALID_TO"] = SapRequestMapper.Dats(to),
            ["COND_NO"] = knumh
        };

    private static Row Header(string operation, string knumh, PriceKey key, string release, DateOnly from, DateOnly to,
        string? createdBy, DateOnly? created) =>
        new Dictionary<string, object?>
        {
            ["OPERATION"] = operation,
            ["COND_NO"] = knumh,
            ["COND_USAGE"] = PriceConditionRules.Usage,
            ["TABLE_NO"] = key.Table.Number,
            ["APPLICATIO"] = PriceConditionRules.Application,
            ["COND_TYPE"] = key.ConditionType,
            ["VARKEY"] = VarKey(key, release),
            ["VALID_FROM"] = SapRequestMapper.Dats(from),
            ["VALID_TO"] = SapRequestMapper.Dats(to),
            ["CREATED_BY"] = createdBy,
            ["CREAT_DATE"] = SapRequestMapper.Dats(created)
        };

    private static Row Item(string operation, string knumh, string conditionType, PriceBapiItemValues v) =>
        new Dictionary<string, object?>
        {
            ["OPERATION"] = operation,
            ["COND_NO"] = knumh,
            ["COND_COUNT"] = "01",
            ["APPLICATIO"] = PriceConditionRules.Application,
            ["COND_TYPE"] = conditionType,
            ["SCALETYPE"] = "A",
            ["CALCTYPCON"] = v.CalculationType,
            ["COND_VALUE"] = v.Amount,
            ["CONDCURR"] = v.Currency,
            ["COND_P_UNT"] = v.PricingUnit,
            ["COND_UNIT"] = v.Unit,
            ["STATUS"] = v.ReleaseStatus,
            ["INDIDELETE"] = v.Delete ? "X" : string.Empty
        };
}
