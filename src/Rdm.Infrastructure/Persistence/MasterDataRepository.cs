using System.Data;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.Abstractions;
using Rdm.Application.MasterData;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.Persistence;

/// <summary>
/// Đọc bằng EF Core; GHI chỉ qua stored procedure: mỗi bảng 1 procedure md.usp_*_Replace nhận TVP,
/// DELETE + INSERT trong 1 transaction → lỗi giữa chừng thì bảng giữ nguyên dữ liệu cũ.
/// </summary>
public sealed class MasterDataRepository : IMasterDataRepository
{
    /// <summary>Thứ tự cột phải khớp table type mới nhất trong Persistence/Sql/MasterData_*.sql (có unit test kiểm tra).</summary>
    internal static readonly IReadOnlyDictionary<Type, (string Procedure, string TableType, string[] Columns)> Targets =
        new Dictionary<Type, (string, string, string[])>
        {
            [typeof(SoldToParty)] = ("md.usp_SoldTo_Replace", "md.SoldToTableType", new[]
            {
                "CustomerNumber", "CustomerName", "Name1", "Name2", "Name3", "Name4", "SearchTerm", "TaxNumber", "TaxNumber1",
                "VatRegistrationNumber", "CustomerType", "CustomerTypeName", "CreatedOn", "SyncedAtUtc"
            }),
            [typeof(ShipToParty)] = ("md.usp_ShipTo_Replace", "md.ShipToTableType", new[]
            {
                "CustomerNumber", "CustomerName", "Name1", "Name2", "Name3", "Name4", "Street", "City", "PostalCode", "Region",
                "Country", "Phone", "SearchTerm", "TaxNumber", "CustomerType", "CustomerTypeName", "SyncedAtUtc"
            }),
            [typeof(SoldToShipTo)] = ("md.usp_SoldToShipTo_Replace", "md.SoldToShipToTableType", new[]
            {
                "SoldTo", "SalesOrg", "DistributionChannel", "Division", "PartnerCounter", "ShipTo", "IsDefault", "SyncedAtUtc"
            }),
            [typeof(SalesDistrict)] = ("md.usp_SalesDistrict_Replace", "md.SalesDistrictTableType", new[] { "Code", "NameEn", "NameVi", "SyncedAtUtc" }),
            [typeof(ShippingCondition)] = ("md.usp_ShippingCondition_Replace", "md.ShippingConditionTableType", new[] { "Code", "NameEn", "NameVi", "SyncedAtUtc" }),
            [typeof(CustomerGroup3)] = ("md.usp_CustomerGroup3_Replace", "md.CustomerGroup3TableType", new[] { "Code", "NameEn", "NameVi", "SyncedAtUtc" }),
            [typeof(SalesOffice)] = ("md.usp_SalesOffice_Replace", "md.SalesOfficeTableType", new[] { "Code", "NameEn", "NameVi", "SapData", "SyncedAtUtc" }),
            [typeof(SalesOfficeAssignment)] = ("md.usp_SalesOfficeAssignment_Replace", "md.SalesOfficeAssignmentTableType",
                new[] { "SalesOrg", "DistributionChannel", "Division", "SalesOffice", "SyncedAtUtc" }),
            [typeof(SalesOrganization)] = ("md.usp_SalesOrg_Replace", "md.SalesOrgTableType",
                new[] { "Code", "NameEn", "NameVi", "Currency", "CompanyCode", "SapData", "SyncedAtUtc" }),
            [typeof(VehicleType)] = ("md.usp_VehicleType_Replace", "md.VehicleTypeTableType", new[] { "Code", "NameEn", "NameVi", "SapData", "SyncedAtUtc" }),
            [typeof(Plant)] = ("md.usp_Plant_Replace", "md.PlantTableType", new[]
            {
                "Code", "Name1", "Name2", "SalesOrg", "DistributionChannel", "Division", "Street", "City", "PostalCode", "Region",
                "Country", "SapData", "SyncedAtUtc"
            }),
            [typeof(PaymentMethod)] = ("md.usp_PaymentMethod_Replace", "md.PaymentMethodTableType",
                new[] { "Country", "Code", "Description", "SapData", "SyncedAtUtc" }),
            [typeof(Material)] = ("md.usp_Material_Replace", "md.MaterialTableType", new[]
            {
                "MaterialNumber", "MaterialCode", "NameEn", "NameVi", "BaseUnit", "BaseUnitExternal", "BaseUnitNameEn", "BaseUnitNameVi",
                "MaterialType", "MaterialGroup", "Division", "IsDeleted", "CreatedOn", "ChangedOn", "SyncedAtUtc"
            }),
            [typeof(MaterialGroup)] = ("md.usp_MaterialGroup_Replace", "md.MaterialGroupTableType",
                new[] { "Code", "NameEn", "NameVi", "LongNameEn", "LongNameVi", "SapData", "SyncedAtUtc" }),
            [typeof(MaterialType)] = ("md.usp_MaterialType_Replace", "md.MaterialTypeTableType", new[] { "Code", "NameEn", "NameVi", "SapData", "SyncedAtUtc" }),
            [typeof(Division)] = ("md.usp_Division_Replace", "md.DivisionTableType", new[] { "Code", "NameEn", "NameVi", "SyncedAtUtc" }),
            [typeof(GlAccount)] = ("md.usp_GlAccount_Replace", "md.GlAccountTableType",
                new[] { "CompanyCode", "AccountNumber", "AccountCode", "ChartOfAccounts", "ShortText", "LongText", "SyncedAtUtc" }),
            [typeof(CostCenter)] = ("md.usp_CostCenter_Replace", "md.CostCenterTableType",
                new[] { "ControllingArea", "Code", "Name", "Description", "ActivationStatus", "SyncedAtUtc" })
        };

    private readonly AppDbContext _db;

    public MasterDataRepository(AppDbContext db)
    {
        _db = db;
    }

    public IQueryable<T> Query<T>() where T : class => _db.Set<T>().AsNoTracking();

    public Task<int> CountAsync<T>(CancellationToken ct) where T : class => _db.Set<T>().CountAsync(ct);

    public async Task AddSyncRunAsync(MasterDataSyncRun run, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync(_db, "md.usp_SyncRun_Insert", new[]
        {
            P("@Object", run.Object.ToString()), P("@Trigger", run.Trigger), P("@StartedAtUtc", run.StartedAtUtc),
            P("@CompletedAtUtc", run.CompletedAtUtc), P("@DurationMs", run.DurationMs), P("@Success", run.Success),
            P("@RowCount", run.RowCount), P("@Error", run.Error)
        }, ct);
        run.Id = Convert.ToInt64(id);
    }

    public async Task<int> ReplaceAllAsync<T>(IReadOnlyCollection<T> rows, CancellationToken ct) where T : class
    {
        if (!Targets.TryGetValue(typeof(T), out var target))
            throw new InvalidOperationException($"Chưa khai báo procedure ghi cho {typeof(T).Name}.");

        using var table = ToDataTable(rows, target.Columns);
        var result = await ExecuteScalarAsync(_db, target.Procedure, new[] { Tvp("@Rows", target.TableType, table) }, ct);
        return Convert.ToInt32(result);
    }

    internal static DataTable ToDataTable<T>(IEnumerable<T> rows, IReadOnlyList<string> columns)
    {
        var props = columns.Select(c => typeof(T).GetProperty(c)
                                        ?? throw new InvalidOperationException($"{typeof(T).Name} không có property {c}.")).ToArray();
        var table = new DataTable();
        foreach (var p in props)
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            table.Columns.Add(p.Name, t == typeof(DateOnly) ? typeof(DateTime) : t);
        }
        foreach (var row in rows)
        {
            var values = new object[props.Length];
            for (var i = 0; i < props.Length; i++)
            {
                values[i] = props[i].GetValue(row) switch
                {
                    null => DBNull.Value,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    var v => v
                };
            }
            table.Rows.Add(values);
        }
        return table;
    }
}
