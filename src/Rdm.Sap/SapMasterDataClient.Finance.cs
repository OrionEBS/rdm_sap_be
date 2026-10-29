using Rdm.Application.MasterData;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>Master data tài chính (mục 11): tài khoản GL và cost center qua BAPI chuẩn.</summary>
public sealed partial class SapMasterDataClient
{
    /// <summary>
    /// BAPI_GL_ACC_GETLIST nhận company code (không nhận COA) → gọi cho từng company code thuộc COA cấu hình
    /// (T001-KTOPL) VÀ gán vào CO area cấu hình (TKA02). COA 1000 dùng chung cho nhiều nước; giao với CO area G100
    /// chỉ còn các company code Việt Nam.
    /// </summary>
    public Task<IReadOnlyList<GlAccount>> ReadGlAccountsAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<GlAccount>>(dest =>
        {
            var coa = _options.ChartOfAccounts.Trim().ToUpperInvariant();
            var t001 = RfcTableReader.Read(dest, "T001", new[] { "BUKRS", "KTOPL" },
                new[] { RfcTableReader.Condition("KTOPL", coa) }, Batch);
            var tka02 = RfcTableReader.Read(dest, "TKA02", new[] { "KOKRS", "BUKRS" },
                new[] { RfcTableReader.Condition("KOKRS", _options.ControllingArea.Trim().ToUpperInvariant()) }, Batch);
            var companyCodes = ResolveCompanyCodes(t001, tka02);
            if (companyCodes.Count == 0)
                throw new InvalidOperationException(
                    $"Không có company code nào thuộc COA {coa} và CO area {_options.ControllingArea}.");

            var result = new List<GlAccount>();
            foreach (var bukrs in companyCodes)
            {
                var fn = dest.Repository.CreateFunction("BAPI_GL_ACC_GETLIST");
                fn.SetValue("COMPANYCODE", bukrs);
                fn.SetValue("LANGUAGE_ISO", "EN");
                fn.InvokeAudited(dest);

                var ret = fn.GetStructure("RETURN");
                if (ret.GetString("TYPE") is "E" or "A")
                    throw new InvalidOperationException($"BAPI_GL_ACC_GETLIST {bukrs}: {ret.GetString("MESSAGE")}");

                foreach (IRfcStructure row in fn.GetTable("ACCOUNT_LIST"))
                {
                    var account = row.GetString("GL_ACCOUNT");
                    result.Add(new GlAccount
                    {
                        CompanyCode = row.GetString("COMP_CODE") is { Length: > 0 } cc ? cc : bukrs,
                        AccountNumber = account,
                        AccountCode = ExternalCode(account),
                        ChartOfAccounts = coa,
                        ShortText = RfcTableReader.NullIfEmpty(row.GetString("SHORT_TEXT")),
                        LongText = RfcTableReader.NullIfEmpty(row.GetString("LONG_TEXT"))
                    });
                }
            }
            return result.GroupBy(a => (a.CompanyCode, a.AccountNumber)).Select(g => g.First()).ToList();
        }, ct);

    /// <summary>BAPI_COSTCENTER_GETLIST1 cho CO area cấu hình; text theo ngôn ngữ đăng nhập SAP (Sap:Language).</summary>
    public Task<IReadOnlyList<CostCenter>> ReadCostCentersAsync(CancellationToken ct) =>
        ReadAsync<IReadOnlyList<CostCenter>>(dest =>
        {
            var area = _options.ControllingArea.Trim().ToUpperInvariant();
            var fn = dest.Repository.CreateFunction("BAPI_COSTCENTER_GETLIST1");
            fn.SetValue("CONTROLLINGAREA", area);
            fn.InvokeAudited(dest);

            var errors = new List<string>();
            foreach (IRfcStructure r in fn.GetTable("RETURN"))
                if (r.GetString("TYPE") is "E" or "A") errors.Add(r.GetString("MESSAGE"));
            if (errors.Count > 0)
                throw new InvalidOperationException($"BAPI_COSTCENTER_GETLIST1 {area}: {string.Join("; ", errors)}");

            var result = new List<CostCenter>();
            foreach (IRfcStructure row in fn.GetTable("COSTCENTERLIST"))
            {
                result.Add(new CostCenter
                {
                    ControllingArea = row.GetString("CO_AREA") is { Length: > 0 } a ? a : area,
                    Code = row.GetString("COSTCENTER"),
                    Name = RfcTableReader.NullIfEmpty(row.GetString("NAME")),
                    Description = RfcTableReader.NullIfEmpty(row.GetString("DESCRIPT")),
                    ActivationStatus = RfcTableReader.NullIfEmpty(row.GetString("ACT_STATE"))
                });
            }
            return result.GroupBy(c => (c.ControllingArea, c.Code)).Select(g => g.First()).ToList();
        }, ct);

    /// <summary>Company code thuộc COA (T001 đã lọc theo KTOPL) giao với company code gán vào CO area (TKA02).</summary>
    internal static List<string> ResolveCompanyCodes(
        IReadOnlyList<Dictionary<string, string>> t001ForCoa, IReadOnlyList<Dictionary<string, string>> tka02ForArea)
    {
        var inArea = tka02ForArea.Select(r => r["BUKRS"]).ToHashSet(StringComparer.Ordinal);
        return t001ForCoa.Select(r => r["BUKRS"]).Where(inArea.Contains).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
    }
}
