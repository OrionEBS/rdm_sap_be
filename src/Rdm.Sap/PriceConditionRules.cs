using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Sap;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>1 trường khóa của bảng điều kiện (thứ tự = thứ tự trong VAKEY).</summary>
public sealed record PriceKeyField(string Name, int Length, int AlphaLength = 0);

/// <summary>Bảng điều kiện Axxx (KVEWE A, KAPPL V) — key layout đọc từ DD03L trên DRF-930.</summary>
public sealed record PriceTableDef(string Number, IReadOnlyList<PriceKeyField> Fields)
{
    public string TableName => "A" + Number;
}

/// <summary>Khóa 1 bảng giá: loại điều kiện + bảng + giá trị trường khóa (định dạng nội bộ SAP).</summary>
public sealed record PriceKey(string ConditionType, PriceTableDef Table, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>VAKEY = các trường khóa sau KSCHL nối liền theo độ dài cố định (không gồm KFRST / DATBI).</summary>
    public string VarKey => string.Concat(Table.Fields.Select(f => (Values.GetValueOrDefault(f.Name) ?? string.Empty).PadRight(f.Length)));

    public string Identity => $"{ConditionType}|{Table.Number}|{VarKey}";

    public override string ToString() =>
        $"{ConditionType}/A{Table.Number} " + string.Join(" ", Table.Fields.Select(f => $"{f.Name}={Values.GetValueOrDefault(f.Name)}"));
}

/// <summary>1 kỳ hiệu lực của condition record (1 dòng bảng Axxx; record bị SAP tách có nhiều dòng cùng KNUMH).</summary>
public sealed record PriceRecord(
    string ConditionRecordNumber,
    PriceKey Key,
    string ReleaseStatus,
    string? ProcessingStatus,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    bool Deleted,
    decimal? Amount,
    string? Currency,
    int? PricingUnit,
    string? Unit,
    string? CalculationType = null)
{
    public bool Pending => ReleaseStatus == "A";
}

/// <summary>Dòng đã kiểm tra hợp lệ, sẵn sàng map sang BAPI.</summary>
public sealed record PriceCreateLine(int Index, PriceKey Key, PriceCreateItem Item, DateOnly ValidFrom, DateOnly ValidTo);

/// <summary>
/// Logic thuần cho bảng giá (unit-test không cần SAP):
///   - Xác định bảng điều kiện theo loại + trường khóa; dựng VAKEY.
///   - Kiểm tra trùng / chồng hiệu lực với bảng giá có sẵn và giữa các dòng trong request.
///   - Tính giá theo VAT; map sang IT_PRICE (ZSSD_PRICE_UPLD) của Z_ESD015_PRICE_UPLOAD.
/// </summary>
public static class PriceConditionRules
{
    public static readonly DateOnly MaxDate = new(9999, 12, 31);
    public const string Application = "V";
    public const string Usage = "A";

    public static readonly PriceTableDef A990 = new("990", new PriceKeyField[]
    {
        new("VKORG", 4), new("VTWEG", 2), new("KUNAG", 10, 10), new("KUNWE", 10, 10), new("MATNR", 18, 18), new("VSBED", 2), new("WERKS", 4)
    });
    public static readonly PriceTableDef A991 = new("991", new PriceKeyField[] { new("VKORGAU", 4), new("WERKS", 4), new("MATNR", 18, 18) });
    public static readonly PriceTableDef A912 = new("912", new PriceKeyField[] { new("VKORG", 4), new("VTWEG", 2), new("MATNR", 18, 18), new("INCO1", 3) });
    public static readonly PriceTableDef A919 = new("919", new PriceKeyField[] { new("VKORG", 4), new("VTWEG", 2), new("REGIO", 3), new("MATNR", 18, 18) });

    public static readonly IReadOnlyList<string> ConditionTypes = new[] { "ZVLP", "ZVDI", "ZMIN", "ZVIV" };

    public static PriceTableDef? TableByNumber(string number) => number.Trim() switch
    {
        "990" => A990, "991" => A991, "912" => A912, "919" => A919, _ => null
    };

    /// <summary>Bảng mà loại điều kiện được phép dùng (để đọc bảng giá theo bộ lọc).</summary>
    public static IReadOnlyList<PriceTableDef> TablesFor(string conditionType) => conditionType switch
    {
        "ZVLP" or "ZVDI" => new[] { A990 },
        "ZVIV" => new[] { A991 },
        "ZMIN" => new[] { A912, A919 },
        _ => Array.Empty<PriceTableDef>()
    };

    // ------------------------------------------------------------------ cấu trúc cột (mỗi bảng khác nhau)

    private static readonly PriceColumn SalesOrgCol = new("salesOrganization", "VKORG", "Sales org");
    private static readonly PriceColumn DistChannelCol = new("distributionChannel", "VTWEG", "Distribution channel");
    private static readonly PriceColumn MaterialCol = new("materialCode", "MATNR", "Material");
    private static readonly PriceColumn PlantCol = new("plant", "WERKS", "Plant");

    /// <summary>Cột chi tiết chung: trạng thái + hiệu lực (bảng Axxx) và giá (KONP dòng 01).</summary>
    public static readonly IReadOnlyList<PriceColumn> DetailColumns = new PriceColumn[]
    {
        new("conditionRecordNumber", "KNUMH", "Condition record"),
        new("releaseStatus", "KFRST", "Release status (A = chờ duyệt)"),
        new("processingStatus", "KBSTAT", "Processing status"),
        new("validFrom", "DATAB", "Valid from"),
        new("validTo", "DATBI", "Valid to"),
        new("amount", "KONP-KBETR", "Rate"),
        new("currency", "KONP-KONWA", "Rate unit"),
        new("pricingUnit", "KONP-KPEIN", "Pricing unit"),
        new("unit", "KONP-KMEIN", "Condition UoM"),
        new("calculationType", "KONP-KRECH", "Calculation type"),
        new("deleted", "KONP-LOEVM_KO", "Deletion indicator")
    };

    /// <summary>Cấu trúc cột khóa theo bảng điều kiện (thứ tự = thứ tự khóa trong bảng).</summary>
    public static PriceTableLayout Layout(PriceTableDef table) => table.Number switch
    {
        "990" => new PriceTableLayout(table.TableName, "Sales org/Distr. chl/Sold-to/Ship-to/Material/Shipping cond./Plant (ZVLP, ZVDI)",
            new[]
            {
                SalesOrgCol, DistChannelCol, new("soldTo", "KUNAG", "Sold-to"), new("shipTo", "KUNWE", "Ship-to"),
                MaterialCol, new("shippingCondition", "VSBED", "Shipping condition"), PlantCol
            }, DetailColumns),
        "991" => new PriceTableLayout(table.TableName, "Sales org (order)/Plant/Material (ZVIV)",
            new[] { new PriceColumn("salesOrganization", "VKORGAU", "Sales org (order)"), PlantCol, MaterialCol }, DetailColumns),
        "912" => new PriceTableLayout(table.TableName, "Sales org/Distr. chl/Material/Incoterms (ZMIN)",
            new[] { SalesOrgCol, DistChannelCol, MaterialCol, new("incoterms", "INCO1", "Incoterms") }, DetailColumns),
        "919" => new PriceTableLayout(table.TableName, "Sales org/Distr. chl/Region/Material (ZMIN)",
            new[] { SalesOrgCol, DistChannelCol, new("region", "REGIO", "Region"), MaterialCol }, DetailColumns),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table.TableName, null)
    };

    /// <summary>Dựng khóa từ input; trả lỗi (mã + nội dung) nếu thiếu / thừa trường.</summary>
    public static PriceKey? BuildKey(PriceKeyInput input, bool allowPartial, out (string Code, string Message)? error)
    {
        error = null;
        var type = SapRequestMapper.Code(input.ConditionType) ?? string.Empty;
        if (!ConditionTypes.Contains(type))
        {
            error = (PriceErrorCodes.InvalidConditionType, $"Loại điều kiện '{input.ConditionType}' không hỗ trợ (chỉ ZVLP, ZVDI, ZMIN, ZVIV).");
            return null;
        }

        var supplied = new Dictionary<string, string?>
        {
            ["VKORG"] = SapRequestMapper.Code(input.SalesOrganization),
            ["VTWEG"] = SapRequestMapper.Code(input.DistributionChannel),
            ["KUNAG"] = SapRequestMapper.Code(input.SoldTo),
            ["KUNWE"] = SapRequestMapper.Code(input.ShipTo),
            ["MATNR"] = SapRequestMapper.Code(input.Material),
            ["VSBED"] = SapRequestMapper.Code(input.ShippingCondition),
            ["WERKS"] = SapRequestMapper.Code(input.Plant),
            ["INCO1"] = SapRequestMapper.Code(input.Incoterms),
            ["REGIO"] = SapRequestMapper.Code(input.Region)
        };

        PriceTableDef table;
        if (type == "ZMIN")
        {
            var hasInco = supplied["INCO1"] is not null;
            var hasRegion = supplied["REGIO"] is not null;
            if (hasInco && hasRegion)
            {
                error = (PriceErrorCodes.InvalidKey, "ZMIN: chỉ truyền Incoterms (A912) hoặc Region (A919), không truyền cả hai.");
                return null;
            }
            table = hasRegion ? A919 : A912;
            // Tạo / sửa: nghiệp vụ chỉ dùng A912 (VK11 ZMIN: Sales org / Distr. chl / Material / Incoterms). A919 có trong access
            // sequence ZMIN nhưng không dùng — DRF-930 không có region VN (T005S), chưa có record A919. Đọc (allowPartial) vẫn cho A919.
            if (hasRegion && !allowPartial)
            {
                error = (PriceErrorCodes.InvalidKey, "ZMIN chỉ tạo theo bảng A912 (Sales org / Distr. channel / Material / Incoterms) — không dùng Region.");
                return null;
            }
            if (!hasInco && !hasRegion && !allowPartial)
            {
                error = (PriceErrorCodes.InvalidKey, "ZMIN cần Incoterms (bảng A912: Sales org / Distr. channel / Material / Incoterms).");
                return null;
            }
        }
        else
        {
            table = TablesFor(type)[0];
        }

        // ZVIV dùng sales org của đơn hàng (VKORGAU).
        if (table == A991) supplied["VKORGAU"] = supplied["VKORG"];

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var f in table.Fields)
        {
            var v = supplied.GetValueOrDefault(f.Name);
            if (v is null)
            {
                missing.Add(f.Name);
                continue;
            }
            if (v.Length > f.Length && !(f.AlphaLength > 0 && v.TrimStart('0').Length <= f.Length))
            {
                error = (PriceErrorCodes.InvalidKey, $"{f.Name} '{v}' dài quá {f.Length} ký tự.");
                return null;
            }
            values[f.Name] = f.AlphaLength > 0 ? AccDocumentMapper.Alpha(v, f.AlphaLength)! : v;
        }

        var tableFieldNames = table.Fields.Select(f => f.Name).Append("VKORG").ToHashSet();
        var extra = supplied.Where(kv => kv.Value is not null && !tableFieldNames.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        if (extra.Count > 0)
        {
            error = (PriceErrorCodes.InvalidKey, $"{type}/A{table.Number} không có trường {string.Join(", ", extra)}.");
            return null;
        }
        if (missing.Count > 0 && !allowPartial)
        {
            error = (PriceErrorCodes.InvalidKey, $"{type}/A{table.Number} thiếu trường khóa {string.Join(", ", missing)}.");
            return null;
        }
        return new PriceKey(type, table, values);
    }

    /// <summary>Dựng khóa từ VAKEY của KONH (dùng cho sửa / duyệt).</summary>
    public static PriceKey KeyFromVarKey(string conditionType, PriceTableDef table, string varKey)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var pos = 0;
        foreach (var f in table.Fields)
        {
            values[f.Name] = pos >= varKey.Length ? string.Empty : varKey.Substring(pos, Math.Min(f.Length, varKey.Length - pos)).Trim();
            pos += f.Length;
        }
        return new PriceKey(conditionType, table, values);
    }

    public static bool AmountSignValid(string conditionType, decimal amount) =>
        conditionType == "ZVDI" ? amount < 0 : amount > 0;

    public static bool Overlaps(DateOnly aFrom, DateOnly aTo, DateOnly bFrom, DateOnly bTo) => aFrom <= bTo && bFrom <= aTo;

    /// <summary>
    /// Kiểm tra 1 kỳ hiệu lực mới (của 1 khóa) với các bản ghi có sẵn cùng khóa. Bỏ qua bản ghi đã xóa và bản ghi có KNUMH = ignoreRecord.
    /// Trả danh sách (loại message, mã, nội dung).
    /// </summary>
    public static List<(string Type, string Code, string Message)> CheckAgainstExisting(
        PriceKey key, DateOnly from, DateOnly to, IEnumerable<PriceRecord> existing, PriceOverlapMode mode, string? ignoreRecord = null)
    {
        var result = new List<(string, string, string)>();
        foreach (var r in existing)
        {
            if (r.Deleted || r.ConditionRecordNumber == ignoreRecord || r.Key.Identity != key.Identity) continue;
            if (!Overlaps(from, to, r.ValidFrom, r.ValidTo)) continue;

            var period = $"{r.ValidFrom:dd.MM.yyyy}–{r.ValidTo:dd.MM.yyyy}";
            if (r.Pending)
                result.Add(("E", PriceErrorCodes.OverlapPending,
                    $"Chồng hiệu lực với bảng giá {r.ConditionRecordNumber} ({period}) đang chờ duyệt — duyệt hoặc sửa bản đó trước."));
            else if (r.ValidFrom == from && r.ValidTo == to)
                result.Add(("E", PriceErrorCodes.SamePeriodExists,
                    $"Đã có bảng giá {r.ConditionRecordNumber} cùng khóa, cùng kỳ {period} — dùng API sửa."));
            else if (mode == PriceOverlapMode.Reject)
                result.Add(("E", PriceErrorCodes.Overlap,
                    $"Chồng hiệu lực với bảng giá đã duyệt {r.ConditionRecordNumber} ({period})."));
            else
                result.Add(("W", PriceErrorCodes.WillAdjustExisting, AdjustNote(r, from, to)));
        }
        return result;
    }

    private static string AdjustNote(PriceRecord r, DateOnly from, DateOnly to)
    {
        var period = $"{r.ValidFrom:dd.MM.yyyy}–{r.ValidTo:dd.MM.yyyy}";
        if (r.ValidFrom >= from && r.ValidTo <= to)
            return $"Bảng giá {r.ConditionRecordNumber} ({period}) nằm trọn trong kỳ mới — SAP sẽ thay thế khi tạo bảng giá mới.";
        if (r.ValidFrom < from && r.ValidTo > to)
            return $"Bảng giá {r.ConditionRecordNumber} ({period}) sẽ bị SAP tách thành 2 kỳ quanh kỳ mới.";
        return r.ValidFrom < from
            ? $"Bảng giá {r.ConditionRecordNumber} ({period}) sẽ được SAP cắt, kết thúc ngày {from.AddDays(-1):dd.MM.yyyy}."
            : $"Bảng giá {r.ConditionRecordNumber} ({period}) sẽ được SAP cắt, bắt đầu từ {to.AddDays(1):dd.MM.yyyy}.";
    }

    /// <summary>Kiểm tra các dòng trong cùng request: trùng khóa + chồng hiệu lực → lỗi ở dòng sau.</summary>
    /// <remarks>Nhóm theo khóa trước rồi mới so từng cặp trong nhóm — O(n) cho upload hàng loạt nhiều khóa khác nhau.</remarks>
    public static Dictionary<int, string> CheckWithinRequest(IReadOnlyList<(int Index, PriceKey Key, DateOnly From, DateOnly To)> lines)
    {
        var errors = new Dictionary<int, string>();
        foreach (var group in lines.GroupBy(l => l.Key.Identity))
        {
            var same = group.ToList();   // giữ thứ tự gốc trong request
            for (var i = 1; i < same.Count; i++)
                for (var j = 0; j < i; j++)
                    if (Overlaps(same[i].From, same[i].To, same[j].From, same[j].To))
                    {
                        errors[same[i].Index] = $"Trùng khóa và chồng hiệu lực với dòng {same[j].Index} trong cùng request.";
                        break;
                    }
        }
        return errors;
    }

    /// <summary>Giá mới theo VAT, làm tròn half-up tới bội số roundingUnit, giữ dấu.</summary>
    public static decimal ApplyVat(decimal oldAmount, decimal vatPercent, decimal? oldVatPercent, decimal roundingUnit)
    {
        var net = oldVatPercent is { } oldVat ? oldAmount / (1 + oldVat / 100m) : oldAmount;
        var gross = net * (1 + vatPercent / 100m);
        var unit = roundingUnit <= 0 ? 1m : roundingUnit;
        return Math.Round(gross / unit, 0, MidpointRounding.AwayFromZero) * unit;
    }

    // ------------------------------------------------------------------ Z_ESD015_PRICE_UPLOAD (IT_PRICE = ZSSD_PRICE_UPLD)

    /// <summary>1 dòng IT_PRICE cho tạo mới (IV_ACTION = C). ROW_NO = số dòng trong request.</summary>
    public static Row ToUploadRow(PriceCreateLine l) => new Dictionary<string, object?>
    {
        ["ROW_NO"] = l.Index,
        ["COND_TABLE"] = l.Key.Table.TableName,
        ["VKORG"] = l.Key.Values.GetValueOrDefault("VKORG"),
        ["VTWEG"] = l.Key.Values.GetValueOrDefault("VTWEG"),
        ["KUNAG"] = l.Key.Values.GetValueOrDefault("KUNAG"),
        ["KUNWE"] = l.Key.Values.GetValueOrDefault("KUNWE"),
        ["MATNR"] = l.Key.Values.GetValueOrDefault("MATNR"),
        ["INCO1"] = l.Key.Values.GetValueOrDefault("INCO1"),
        ["VSBED"] = l.Key.Values.GetValueOrDefault("VSBED"),
        ["WERKS"] = l.Key.Values.GetValueOrDefault("WERKS"),
        ["VKORGAU"] = l.Key.Values.GetValueOrDefault("VKORGAU"),
        ["REGIO"] = l.Key.Values.GetValueOrDefault("REGIO"),
        ["KBETR"] = l.Item.Amount,
        ["KONWA"] = SapRequestMapper.Code(l.Item.Currency) ?? "VND",
        ["KPEIN"] = l.Item.PricingUnit,
        ["KMEIN"] = SapRequestMapper.Code(l.Item.Unit),
        ["DATAB"] = SapRequestMapper.Dats(l.ValidFrom),
        ["DATBI"] = SapRequestMapper.Dats(l.ValidTo)
    };

    /// <summary>1 dòng IT_PRICE cho sửa (IV_ACTION = U): KNUMH + field đổi; null = giữ nguyên trên SAP.</summary>
    public static Row ToChangeRow(int index, string knumh, decimal? amount, int? pricingUnit, string? unit,
        DateOnly? validTo, DateOnly? periodStart) => new Dictionary<string, object?>
    {
        ["ROW_NO"] = index,
        ["KNUMH"] = knumh,
        ["KBETR"] = amount,
        ["KPEIN"] = pricingUnit,
        ["KMEIN"] = SapRequestMapper.Code(unit),
        ["DATBI"] = SapRequestMapper.Dats(validTo),
        ["DATAB"] = SapRequestMapper.Dats(periodStart)
    };

    /// <summary>1 dòng IT_PRICE cho duyệt (IV_ACTION = R).</summary>
    public static Row ToReleaseRow(int index, string knumh) => new Dictionary<string, object?>
    {
        ["ROW_NO"] = index,
        ["KNUMH"] = knumh
    };
}
