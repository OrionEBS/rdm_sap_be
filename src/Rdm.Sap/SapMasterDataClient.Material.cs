using Rdm.Application.MasterData;

namespace Rdm.Sap;

/// <summary>Master data vật tư (mục 10) qua RFC_READ_TABLE.</summary>
public sealed partial class SapMasterDataClient
{
    /// <summary>
    /// MARA/MAKT đọc trong 1 lần gọi: RFC_READ_TABLE không ORDER BY nên chia lô bằng ROWSKIPS có thể trùng/sót dòng
    /// với bảng lớn. DEV có ~15k vật tư / ~30k dòng MAKT.
    /// </summary>
    private const int MaterialBatchSize = 200_000;

    public Task<IReadOnlyList<Material>> ReadMaterialsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<Material>>(dest =>
        {
            var lang = GetLanguages(dest);
            var mara = RfcTableReader.Read(dest, "MARA",
                new[] { "MATNR", "MTART", "MATKL", "SPART", "MEINS", "LVORM", "ERSDA", "LAEDA" },
                MaterialTypeFilter(_options.MaterialTypes), MaterialBatchSize);
            var makt = RfcTableReader.Read(dest, "MAKT", new[] { "MATNR", "SPRAS", "MAKTX" },
                new[] { $"SPRAS IN ( '{lang.En}' , '{lang.Vi}' )" }, MaterialBatchSize);
            var units = RfcTableReader.Read(dest, "T006A", new[] { "SPRAS", "MSEHI", "MSEH3", "MSEHL" },
                new[] { $"SPRAS IN ( '{lang.En}' , '{lang.Vi}' )" }, Batch);
            return BuildMaterials(mara, makt, units, lang.En, lang.Vi);
        }, ct);

    public Task<IReadOnlyList<MaterialGroup>> ReadMaterialGroupsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<MaterialGroup>>(dest =>
        {
            var longNames = Texts(dest, "T023T", "MATKL", "WGBEZ60");
            return CodeTexts<MaterialGroup>(dest, "T023", "MATKL", "T023T", "WGBEZ", (g, r) =>
            {
                if (longNames.TryGetValue(g.Code, out var l))
                {
                    g.LongNameEn = l.En;
                    g.LongNameVi = l.Vi;
                }
                g.SapData = ToSapData(r);
            });
        }, ct);

    public Task<IReadOnlyList<MaterialType>> ReadMaterialTypesAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<MaterialType>>(dest => CodeTexts<MaterialType>(dest, "T134", "MTART", "T134T", "MTBEZ",
            (t, r) => t.SapData = ToSapData(r)), ct);

    public Task<IReadOnlyList<Division>> ReadDivisionsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<Division>>(dest => CodeTexts<Division>(dest, "TSPA", "SPART", "TSPAT", "VTEXT"), ct);

    internal static List<Material> BuildMaterials(
        IReadOnlyList<Dictionary<string, string>> mara,
        IReadOnlyList<Dictionary<string, string>> makt,
        IReadOnlyList<Dictionary<string, string>> units,
        string enKey, string viKey)
    {
        var names = new Dictionary<string, (string? En, string? Vi)>(StringComparer.Ordinal);
        foreach (var r in makt)
        {
            names.TryGetValue(r["MATNR"], out var n);
            var text = RfcTableReader.NullIfEmpty(r["MAKTX"]);
            if (r["SPRAS"] == enKey) n.En = text;
            else if (r["SPRAS"] == viKey) n.Vi = text;
            else continue;
            names[r["MATNR"]] = n;
        }

        var unitTexts = new Dictionary<string, (string? External, string? En, string? Vi)>(StringComparer.Ordinal);
        foreach (var r in units)
        {
            unitTexts.TryGetValue(r["MSEHI"], out var u);
            if (r["SPRAS"] == enKey) u = (RfcTableReader.NullIfEmpty(r["MSEH3"]), RfcTableReader.NullIfEmpty(r["MSEHL"]), u.Vi);
            else if (r["SPRAS"] == viKey) u = (u.External ?? RfcTableReader.NullIfEmpty(r["MSEH3"]), u.En, RfcTableReader.NullIfEmpty(r["MSEHL"]));
            else continue;
            unitTexts[r["MSEHI"]] = u;
        }

        return mara
            .GroupBy(r => r["MATNR"], StringComparer.Ordinal)
            .Select(g =>
            {
                var r = g.First();
                var matnr = r["MATNR"];
                names.TryGetValue(matnr, out var n);
                unitTexts.TryGetValue(r["MEINS"], out var u);
                return new Material
                {
                    MaterialNumber = matnr,
                    MaterialCode = ExternalMaterialCode(matnr),
                    NameEn = n.En,
                    NameVi = n.Vi,
                    BaseUnit = r["MEINS"],
                    BaseUnitExternal = u.External,
                    BaseUnitNameEn = u.En,
                    BaseUnitNameVi = u.Vi,
                    MaterialType = RfcTableReader.NullIfEmpty(r["MTART"]),
                    MaterialGroup = RfcTableReader.NullIfEmpty(r["MATKL"]),
                    Division = RfcTableReader.NullIfEmpty(r["SPART"]),
                    IsDeleted = r["LVORM"] == "X",
                    CreatedOn = RfcTableReader.ParseDate(r["ERSDA"]),
                    ChangedOn = RfcTableReader.ParseDate(r["LAEDA"])
                };
            })
            .ToList();
    }

    /// <summary>WHERE cho MARA theo loại vật tư cấu hình (Sap:MaterialTypes). Danh sách rỗng = không lọc.</summary>
    internal static IReadOnlyList<string> MaterialTypeFilter(IReadOnlyCollection<string> materialTypes) =>
        RfcTableReader.AnyOf("MTART", materialTypes);

    /// <summary>MATNR toàn số → bỏ 0 đầu (giống CONVERSION_EXIT_MATN1_OUTPUT); mã chữ giữ nguyên.</summary>
    public static string ExternalMaterialCode(string matnr) => ExternalCode(matnr);

    /// <summary>Mã toàn số → bỏ 0 đầu (giống CONVERSION_EXIT_ALPHA_OUTPUT); mã có chữ giữ nguyên.</summary>
    public static string ExternalCode(string code)
    {
        if (code.Length == 0 || !code.All(char.IsDigit)) return code;
        var trimmed = code.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
