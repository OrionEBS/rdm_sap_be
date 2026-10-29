using System.Text.Encodings.Web;
using System.Text.Json;
using Rdm.Application.MasterData;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Master data SD (mục 7) qua RFC_READ_TABLE. Text đọc cả tiếng Anh và tiếng Việt
/// (khóa ngôn ngữ nội bộ SAP lấy từ T002 theo LAISO EN / VI — tiếng Việt là ký tự '쁩').
/// </summary>
public sealed partial class SapMasterDataClient
{
    private static readonly JsonSerializerOptions SapDataJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Kna1Fields =
    {
        "KUNNR", "NAME1", "NAME2", "NAME3", "NAME4", "SORTL", "STCD1", "STCEG", "KTOKD", "LOEVM", "ERDAT",
        "STRAS", "ORT01", "PSTLZ", "REGIO", "LAND1", "TELF1"
    };

    private Languages? _languages;

    private int Batch => Math.Max(100, _options.ReadTableBatchSize);

    public Task<CustomerPartners> ReadCustomerPartnersAsync(CancellationToken ct) =>
        ReadAsync(dest =>
        {
            var kna1 = RfcTableReader.Read(dest, "KNA1", Kna1Fields, Array.Empty<string>(), Batch);
            var knvv = RfcTableReader.Read(dest, "KNVV", new[] { "KUNNR", "VKORG", "VTWEG", "SPART", "LOEVM" }, Array.Empty<string>(), Batch);
            var knvp = RfcTableReader.Read(dest, "KNVP",
                new[] { "KUNNR", "VKORG", "VTWEG", "SPART", "PARVW", "PARZA", "KUNN2", "DEFPA" },
                new[] { "PARVW IN ( 'AG' , 'WE' )" }, Batch);
            var types = Texts(dest, "T077X", "KTOKD", "TXT30");
            return BuildCustomerPartners(kna1, knvv, knvp, types.ToDictionary(t => t.Key, t => t.Value.En ?? t.Value.Vi),
                _options.SoldToAccountGroups);
        }, ct);

    public Task<IReadOnlyList<SalesDistrict>> ReadSalesDistrictsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<SalesDistrict>>(dest => CodeTexts<SalesDistrict>(dest, "T171", "BZIRK", "T171T", "BZTXT"), ct);

    public Task<IReadOnlyList<ShippingCondition>> ReadShippingConditionsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<ShippingCondition>>(dest => CodeTexts<ShippingCondition>(dest, "TVSB", "VSBED", "TVSBT", "VTEXT"), ct);

    public Task<IReadOnlyList<CustomerGroup3>> ReadCustomerGroups3Async(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<CustomerGroup3>>(dest => CodeTexts<CustomerGroup3>(dest, "TVV3", "KVGR3", "TVV3T", "BEZEI"), ct);

    public Task<IReadOnlyList<VehicleType>> ReadVehicleTypesAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<VehicleType>>(dest => CodeTexts<VehicleType>(dest, "T173", "VSART", "T173T", "BEZEI",
            (v, r) => v.SapData = ToSapData(r)), ct);

    public Task<IReadOnlyList<SalesOrganization>> ReadSalesOrganizationsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<SalesOrganization>>(dest => CodeTexts<SalesOrganization>(dest, "TVKO", "VKORG", "TVKOT", "VTEXT",
            (o, r) =>
            {
                o.Currency = RfcTableReader.NullIfEmpty(r["WAERS"]);
                o.CompanyCode = RfcTableReader.NullIfEmpty(r["BUKRS"]);
                o.SapData = ToSapData(r);
            }), ct);

    public Task<SalesOfficeData> ReadSalesOfficesAsync(CancellationToken ct) =>
        ReadAsync(dest =>
        {
            var offices = CodeTexts<SalesOffice>(dest, "TVBUR", "VKBUR", "TVKBT", "BEZEI", (o, r) => o.SapData = ToSapData(r));
            var assignments = RfcTableReader.Read(dest, "TVKBZ", new[] { "VKORG", "VTWEG", "SPART", "VKBUR" }, Array.Empty<string>(), Batch)
                .Select(r => new SalesOfficeAssignment
                {
                    SalesOrg = r["VKORG"], DistributionChannel = r["VTWEG"], Division = r["SPART"], SalesOffice = r["VKBUR"]
                })
                .ToList();
            return new SalesOfficeData(offices, assignments);
        }, ct);

    public Task<IReadOnlyList<Plant>> ReadPlantsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<Plant>>(dest => RfcTableReader.Read(dest, "T001W", Array.Empty<string>(),
                RfcTableReader.AnyOf("LAND1", _options.PlantCountries), Batch)
            .Select(r => new Plant
            {
                Code = r["WERKS"],
                Name1 = RfcTableReader.NullIfEmpty(r["NAME1"]),
                Name2 = RfcTableReader.NullIfEmpty(r["NAME2"]),
                SalesOrg = RfcTableReader.NullIfEmpty(r["VKORG"]),
                DistributionChannel = RfcTableReader.NullIfEmpty(r["VTWEG"]),
                Division = RfcTableReader.NullIfEmpty(r["SPART"]),
                Street = RfcTableReader.NullIfEmpty(r["STRAS"]),
                City = RfcTableReader.NullIfEmpty(r["ORT01"]),
                PostalCode = RfcTableReader.NullIfEmpty(r["PSTLZ"]),
                Region = RfcTableReader.NullIfEmpty(r["REGIO"]),
                Country = RfcTableReader.NullIfEmpty(r["LAND1"]),
                SapData = ToSapData(r)
            })
            .ToList(), ct);

    public Task<IReadOnlyList<PaymentMethod>> ReadPaymentMethodsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<PaymentMethod>>(dest => RfcTableReader.Read(dest, "T042Z", Array.Empty<string>(), Array.Empty<string>(), Batch)
            .Select(r => new PaymentMethod
            {
                Country = r["LAND1"],
                Code = r["ZLSCH"],
                Description = RfcTableReader.NullIfEmpty(r["TEXT1"]),
                SapData = ToSapData(r)
            })
            .ToList(), ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Sold-to = khách thuộc account group sold-to (cấu hình) có partner AG trỏ về chính nó ở 1 sales area còn hiệu lực;
    /// quan hệ = partner WE của các sold-to đó; ship-to = danh mục các khách xuất hiện trong quan hệ.
    /// Loại khách bị đánh dấu xóa (KNA1-LOEVM) và sales area bị xóa (KNVV-LOEVM).
    /// </summary>
    internal static CustomerPartners BuildCustomerPartners(
        IReadOnlyList<Dictionary<string, string>> kna1,
        IReadOnlyList<Dictionary<string, string>> knvv,
        IReadOnlyList<Dictionary<string, string>> knvp,
        IReadOnlyDictionary<string, string?> customerTypeNames,
        IReadOnlyCollection<string> soldToAccountGroups)
    {
        var soldToGroups = soldToAccountGroups.Select(g => g.Trim().ToUpperInvariant()).ToHashSet();
        var customers = kna1.Where(r => string.IsNullOrEmpty(r["LOEVM"]))
            .GroupBy(r => r["KUNNR"]).ToDictionary(g => g.Key, g => g.First());
        var activeAreas = knvv.Where(r => string.IsNullOrEmpty(r["LOEVM"]))
            .Select(AreaKey).ToHashSet();

        bool Valid(Dictionary<string, string> p) => activeAreas.Contains(AreaKey(p)) && customers.ContainsKey(p["KUNNR"]);

        var soldTo = knvp
            .Where(p => p["PARVW"] == "AG" && p["KUNN2"] == p["KUNNR"] && Valid(p) && soldToGroups.Contains(customers[p["KUNNR"]]["KTOKD"]))
            .Select(p => p["KUNNR"])
            .Distinct()
            .Select(k =>
            {
                var c = customers[k];
                var type = c["KTOKD"];
                return new SoldToParty
                {
                    CustomerNumber = k,
                    CustomerName = FullName(c),
                    Name1 = RfcTableReader.NullIfEmpty(c["NAME1"]),
                    Name2 = RfcTableReader.NullIfEmpty(c["NAME2"]),
                    Name3 = RfcTableReader.NullIfEmpty(c["NAME3"]),
                    Name4 = RfcTableReader.NullIfEmpty(c["NAME4"]),
                    SearchTerm = RfcTableReader.NullIfEmpty(c["SORTL"]),
                    TaxNumber = TaxNumber(c),
                    TaxNumber1 = RfcTableReader.NullIfEmpty(c["STCD1"]),
                    VatRegistrationNumber = RfcTableReader.NullIfEmpty(c["STCEG"]),
                    CustomerType = type,
                    CustomerTypeName = customerTypeNames.TryGetValue(type, out var name) ? name : null,
                    CreatedOn = RfcTableReader.ParseDate(c["ERDAT"])
                };
            })
            .ToList();
        var soldToSet = soldTo.Select(s => s.CustomerNumber).ToHashSet();

        var relations = knvp
            .Where(p => p["PARVW"] == "WE" && Valid(p) && soldToSet.Contains(p["KUNNR"]) && customers.ContainsKey(p["KUNN2"]))
            .Select(p => new SoldToShipTo
            {
                SoldTo = p["KUNNR"],
                SalesOrg = p["VKORG"],
                DistributionChannel = p["VTWEG"],
                Division = p["SPART"],
                PartnerCounter = int.TryParse(p["PARZA"], out var n) ? n : 0,
                ShipTo = p["KUNN2"],
                IsDefault = p["DEFPA"] == "X"
            })
            // KNVP có thể trùng khóa nếu dữ liệu SAP bất thường — giữ dòng đầu.
            .GroupBy(r => (r.SoldTo, r.SalesOrg, r.DistributionChannel, r.Division, r.PartnerCounter))
            .Select(g => g.First())
            .ToList();

        var shipTo = relations
            .Select(r => r.ShipTo)
            .Distinct()
            .Select(k =>
            {
                var c = customers[k];
                var type = c["KTOKD"];
                return new ShipToParty
                {
                    CustomerNumber = k,
                    CustomerName = FullName(c),
                    Name1 = RfcTableReader.NullIfEmpty(c["NAME1"]),
                    Name2 = RfcTableReader.NullIfEmpty(c["NAME2"]),
                    Name3 = RfcTableReader.NullIfEmpty(c["NAME3"]),
                    Name4 = RfcTableReader.NullIfEmpty(c["NAME4"]),
                    Street = RfcTableReader.NullIfEmpty(c["STRAS"]),
                    City = RfcTableReader.NullIfEmpty(c["ORT01"]),
                    PostalCode = RfcTableReader.NullIfEmpty(c["PSTLZ"]),
                    Region = RfcTableReader.NullIfEmpty(c["REGIO"]),
                    Country = RfcTableReader.NullIfEmpty(c["LAND1"]),
                    Phone = RfcTableReader.NullIfEmpty(c["TELF1"]),
                    SearchTerm = RfcTableReader.NullIfEmpty(c["SORTL"]),
                    TaxNumber = TaxNumber(c),
                    CustomerType = type,
                    CustomerTypeName = customerTypeNames.TryGetValue(type, out var name) ? name : null
                };
            })
            .ToList();

        return new CustomerPartners(soldTo, shipTo, relations);
    }

    /// <summary>Quy ước trên SAP: NAME1 là tên ngắn, tên pháp lý đầy đủ nằm ở NAME2..NAME4.</summary>
    internal static string FullName(IReadOnlyDictionary<string, string> c)
    {
        var full = string.Join(' ', new[] { c["NAME2"], c["NAME3"], c["NAME4"] }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(full) ? c["NAME1"] : full;
    }

    private static string? TaxNumber(IReadOnlyDictionary<string, string> c) =>
        RfcTableReader.NullIfEmpty(c["STCEG"]) ?? RfcTableReader.NullIfEmpty(c["STCD1"]);

    private static (string, string, string, string) AreaKey(Dictionary<string, string> r) =>
        (r["KUNNR"], r["VKORG"], r["VTWEG"], r["SPART"]);

    /// <summary>Bảng mã (đọc toàn bộ field) + bảng text (EN/VI).</summary>
    private List<T> CodeTexts<T>(RfcDestination dest, string baseTable, string keyField, string textTable, string textField,
        Action<T, Dictionary<string, string>>? extra = null) where T : CodeTextRow, new()
    {
        var baseRows = RfcTableReader.Read(dest, baseTable, Array.Empty<string>(), Array.Empty<string>(), Batch);
        var texts = Texts(dest, textTable, keyField, textField);
        return baseRows.Select(r =>
        {
            var row = new T { Code = r[keyField] };
            if (texts.TryGetValue(row.Code, out var t))
            {
                row.NameEn = t.En;
                row.NameVi = t.Vi;
            }
            extra?.Invoke(row, r);
            return row;
        }).ToList();
    }

    private Dictionary<string, (string? En, string? Vi)> Texts(RfcDestination dest, string table, string keyField, string textField)
    {
        var lang = GetLanguages(dest);
        var result = new Dictionary<string, (string? En, string? Vi)>(StringComparer.Ordinal);
        foreach (var r in RfcTableReader.Read(dest, table, new[] { "SPRAS", keyField, textField }, Array.Empty<string>(), Batch))
        {
            var key = r[keyField];
            var text = RfcTableReader.NullIfEmpty(r[textField]);
            result.TryGetValue(key, out var cur);
            if (r["SPRAS"] == lang.En) cur.En = text;
            else if (r["SPRAS"] == lang.Vi) cur.Vi = text;
            else continue;
            result[key] = cur;
        }
        return result;
    }

    private Languages GetLanguages(RfcDestination dest)
    {
        if (_languages is { } cached) return cached;
        var rows = RfcTableReader.Read(dest, "T002", new[] { "SPRAS", "LAISO" }, new[] { "LAISO IN ( 'EN' , 'VI' )" }, 100);
        string? Key(string iso) => rows.FirstOrDefault(r => r["LAISO"] == iso)?["SPRAS"];
        return _languages = new Languages(Key("EN") ?? "E", Key("VI") ?? "쁩");
    }

    /// <summary>Toàn bộ field của dòng (bỏ MANDT và field rỗng) dạng JSON.</summary>
    internal static string ToSapData(IReadOnlyDictionary<string, string> row) =>
        JsonSerializer.Serialize(
            row.Where(kv => kv.Key != "MANDT" && kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            SapDataJson);

    private sealed record Languages(string En, string Vi);
}
