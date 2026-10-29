using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.ApiCatalog;
using Rdm.Application.Auth;
using Rdm.Application.MasterData;
using Rdm.Infrastructure.Persistence;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.DbSync;

/// <summary>
/// IfEmpty (mặc định, an toàn): chỉ nạp bảng đang trống ở DB đích; cấu hình API chỉ chép cho API chưa được chỉnh bên đích;
/// tài khoản chỉ thêm user chưa có. Replace: thay toàn bộ bảng master data, chép đè cấu hình API, cập nhật user trùng tên.
/// </summary>
public enum DbImportMode { IfEmpty, Replace }

/// <summary>Kết quả 1 bảng. Action: replace / merge / skip-target-has-data / skip-source-empty / skip-not-in-snapshot.</summary>
public sealed record DbTableResult(string Table, string Action, int SourceRows, int? TargetRowsBefore, int? TargetRowsAfter, string? Detail = null);

/// <summary>1 bảng được đồng bộ: đọc bằng EF (AsNoTracking), GHI chỉ qua stored procedure.</summary>
public abstract class DbSyncTable
{
    protected DbSyncTable(string name) => Name = name;

    /// <summary>Tên schema.bảng — dùng trong file snapshot và tham số --tables.</summary>
    public string Name { get; }

    public abstract Task<int> CountAsync(AppDbContext db, CancellationToken ct);
    public abstract Task<(JsonElement Rows, int Count)> ExportAsync(AppDbContext db, JsonSerializerOptions json, CancellationToken ct);

    /// <summary>dryRun = true: chỉ tính việc sẽ làm, không ghi.</summary>
    public abstract Task<DbTableResult> ImportAsync(AppDbContext db, JsonElement rows, DbImportMode mode, bool dryRun, string trigger,
        JsonSerializerOptions json, CancellationToken ct);

    protected static List<T> Read<T>(JsonElement rows, JsonSerializerOptions json) =>
        rows.Deserialize<List<T>>(json) ?? new List<T>();
}

/// <summary>Bảng master data md.*: thay toàn bộ qua md.usp_*_Replace (DELETE + INSERT trong 1 transaction) + ghi md.SyncRun.</summary>
internal sealed class MasterDataTable<T> : DbSyncTable where T : class
{
    private readonly MasterDataObject? _object;

    public MasterDataTable(string name, MasterDataObject? syncRunObject) : base(name) => _object = syncRunObject;

    public override Task<int> CountAsync(AppDbContext db, CancellationToken ct) => db.Set<T>().CountAsync(ct);

    public override async Task<(JsonElement, int)> ExportAsync(AppDbContext db, JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = await db.Set<T>().AsNoTracking().ToListAsync(ct);
        return (JsonSerializer.SerializeToElement(rows, json), rows.Count);
    }

    public override async Task<DbTableResult> ImportAsync(AppDbContext db, JsonElement rowsJson, DbImportMode mode, bool dryRun, string trigger,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = Read<T>(rowsJson, json);
        var before = await CountAsync(db, ct);
        // Nguồn trống thì không bao giờ xóa dữ liệu đích (cùng nguyên tắc với đồng bộ từ SAP).
        if (rows.Count == 0) return new DbTableResult(Name, "skip-source-empty", 0, before, before, "nguồn 0 dòng — giữ nguyên dữ liệu đích");
        if (mode == DbImportMode.IfEmpty && before > 0)
            return new DbTableResult(Name, "skip-target-has-data", rows.Count, before, before, "đích đã có dữ liệu (dùng --mode Replace để thay)");
        if (dryRun) return new DbTableResult(Name, "replace", rows.Count, before, null);

        var repo = new MasterDataRepository(db);
        var started = DateTime.UtcNow;
        var written = await repo.ReplaceAllAsync(rows, ct);
        if (_object is { } obj)
        {
            var done = DateTime.UtcNow;
            await repo.AddSyncRunAsync(new MasterDataSyncRun
            {
                Object = obj, Trigger = trigger, StartedAtUtc = started, CompletedAtUtc = done,
                DurationMs = (int)(done - started).TotalMilliseconds, Success = true, RowCount = written
            }, ct);
        }
        return new DbTableResult(Name, "replace", rows.Count, before, await CountAsync(db, ct));
    }
}

/// <summary>rdm.ApiEndpoints: bật / tắt, ghi chú, mô tả đối tác — qua rdm.usp_ApiEndpoint_Import (không xóa API thừa ở đích).</summary>
internal sealed class ApiEndpointsTable : DbSyncTable
{
    /// <summary>Thứ tự cột = rdm.ApiEndpointImportTableType (Sql/DbSync_Procedures.sql).</summary>
    internal static readonly string[] Columns =
    {
        "EndpointKey", "HttpMethod", "RouteTemplate", "GroupName", "ActionName", "Description", "PublicSummary", "PublicDescription",
        "PublicHidden", "DocUpdatedBy", "DocUpdatedAtUtc", "IsActive", "IsLocked", "Note", "UpdatedBy", "UpdatedAtUtc", "CreatedAtUtc",
        "LastSeenAtUtc"
    };

    public ApiEndpointsTable() : base("rdm.ApiEndpoints") { }

    public override Task<int> CountAsync(AppDbContext db, CancellationToken ct) => db.ApiEndpoints.CountAsync(ct);

    public override async Task<(JsonElement, int)> ExportAsync(AppDbContext db, JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = await db.ApiEndpoints.AsNoTracking().OrderBy(x => x.EndpointKey).ToListAsync(ct);
        return (JsonSerializer.SerializeToElement(rows, json), rows.Count);
    }

    public override async Task<DbTableResult> ImportAsync(AppDbContext db, JsonElement rowsJson, DbImportMode mode, bool dryRun, string trigger,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = Read<ApiEndpointSetting>(rowsJson, json);
        var before = await CountAsync(db, ct);
        if (rows.Count == 0) return new DbTableResult(Name, "skip-source-empty", 0, before, before);
        if (dryRun)
        {
            var existing = (await db.ApiEndpoints.AsNoTracking()
                    .Select(x => new { x.EndpointKey, Touched = x.UpdatedAtUtc != null || x.DocUpdatedAtUtc != null }).ToListAsync(ct))
                .ToDictionary(x => x.EndpointKey, x => x.Touched, StringComparer.OrdinalIgnoreCase);
            var add = rows.Count(r => !existing.ContainsKey(r.EndpointKey));
            var upd = rows.Count(r => existing.TryGetValue(r.EndpointKey, out var touched) && (mode == DbImportMode.Replace || !touched));
            return new DbTableResult(Name, "merge", rows.Count, before, null, $"thêm {add}, cập nhật {upd}, bỏ qua {rows.Count - add - upd}");
        }

        using var table = MasterDataRepository.ToDataTable(rows, Columns);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("rdm.usp_ApiEndpoint_Import", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        cmd.Parameters.Add(Tvp("@Rows", "rdm.ApiEndpointImportTableType", table));
        cmd.Parameters.Add(P("@OnlyUntouched", mode == DbImportMode.IfEmpty));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var detail = $"thêm {reader.GetInt32(0)}, cập nhật {reader.GetInt32(1)}, bỏ qua {reader.GetInt32(2)}";
        await reader.CloseAsync();
        return new DbTableResult(Name, "merge", rows.Count, before, await CountAsync(db, ct),
            mode == DbImportMode.IfEmpty ? detail + " (API đã chỉnh bên đích giữ nguyên)" : detail);
    }
}

/// <summary>rdm.ApiOutputProfiles: cấu hình cột output + API sao chép — qua rdm.usp_ApiOutputProfile_Import (không xóa dòng thừa ở đích).</summary>
internal sealed class ApiOutputProfilesTable : DbSyncTable
{
    /// <summary>Thứ tự cột = rdm.ApiOutputProfileImportTableType (Sql/ApiOutputProfiles.sql).</summary>
    internal static readonly string[] Columns =
        { "EndpointKey", "CopyName", "Title", "IsActive", "FieldsJson", "UpdatedBy", "UpdatedAtUtc", "CreatedAtUtc" };

    public ApiOutputProfilesTable() : base("rdm.ApiOutputProfiles") { }

    public override Task<int> CountAsync(AppDbContext db, CancellationToken ct) => db.ApiOutputProfiles.CountAsync(ct);

    public override async Task<(JsonElement, int)> ExportAsync(AppDbContext db, JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = await db.ApiOutputProfiles.AsNoTracking().OrderBy(x => x.EndpointKey).ThenBy(x => x.CopyName).ToListAsync(ct);
        return (JsonSerializer.SerializeToElement(rows, json), rows.Count);
    }

    public override async Task<DbTableResult> ImportAsync(AppDbContext db, JsonElement rowsJson, DbImportMode mode, bool dryRun, string trigger,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = Read<ApiOutputProfile>(rowsJson, json);
        var before = await CountAsync(db, ct);
        if (rows.Count == 0) return new DbTableResult(Name, "skip-source-empty", 0, before, before);
        if (dryRun)
        {
            var existing = (await db.ApiOutputProfiles.AsNoTracking().Select(x => new { x.EndpointKey, x.CopyName }).ToListAsync(ct))
                .Select(x => x.CopyName ?? "#" + x.EndpointKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var add = rows.Count(r => !existing.Contains(r.CopyName ?? "#" + r.EndpointKey));
            var upd = mode == DbImportMode.Replace ? rows.Count - add : 0;
            return new DbTableResult(Name, "merge", rows.Count, before, null, $"thêm {add}, cập nhật {upd}, bỏ qua {rows.Count - add - upd}");
        }

        using var table = MasterDataRepository.ToDataTable(rows, Columns);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("rdm.usp_ApiOutputProfile_Import", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        cmd.Parameters.Add(Tvp("@Rows", "rdm.ApiOutputProfileImportTableType", table));
        cmd.Parameters.Add(P("@OnlyUntouched", mode == DbImportMode.IfEmpty));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var detail = $"thêm {reader.GetInt32(0)}, cập nhật {reader.GetInt32(1)}, bỏ qua {reader.GetInt32(2)}";
        await reader.CloseAsync();
        return new DbTableResult(Name, "merge", rows.Count, before, await CountAsync(db, ct),
            mode == DbImportMode.IfEmpty ? detail + " (cấu hình đã có bên đích giữ nguyên)" : detail);
    }
}

/// <summary>rdm.ApiUsers (chỉ khi chọn rõ trong --tables): qua rdm.usp_ApiUser_Upsert, giữ nguyên mật khẩu đã băm.</summary>
internal sealed class ApiUsersTable : DbSyncTable
{
    public ApiUsersTable() : base("rdm.ApiUsers") { }

    public override Task<int> CountAsync(AppDbContext db, CancellationToken ct) => db.ApiUsers.CountAsync(ct);

    public override async Task<(JsonElement, int)> ExportAsync(AppDbContext db, JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = await db.ApiUsers.AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct);
        return (JsonSerializer.SerializeToElement(rows, json), rows.Count);
    }

    public override async Task<DbTableResult> ImportAsync(AppDbContext db, JsonElement rowsJson, DbImportMode mode, bool dryRun, string trigger,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var rows = Read<ApiUser>(rowsJson, json);
        var existing = (await db.ApiUsers.AsNoTracking().Select(x => x.Username).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = existing.Count;
        var todo = rows.Where(r => mode == DbImportMode.Replace || !existing.Contains(r.Username)).ToList();
        var detail = $"thêm {todo.Count(r => !existing.Contains(r.Username))}, cập nhật {todo.Count(r => existing.Contains(r.Username))}, " +
                     $"bỏ qua {rows.Count - todo.Count}";
        if (dryRun) return new DbTableResult(Name, "merge", rows.Count, before, null, detail);

        foreach (var u in todo)
        {
            await ExecuteScalarAsync(db, "rdm.usp_ApiUser_Upsert", new[]
            {
                P("@Username", u.Username), P("@PasswordHash", u.PasswordHash), P("@Role", u.Role), P("@IsActive", u.IsActive),
                P("@NowUtc", DateTime.UtcNow)
            }, ct);
        }
        return new DbTableResult(Name, "merge", rows.Count, before, await CountAsync(db, ct), detail);
    }
}

public static class DbSyncTables
{
    /// <summary>Mọi bảng hỗ trợ, theo thứ tự nạp. Không gồm dữ liệu giao dịch / môi trường: rdm.SapPostings, md.SyncRun, hangfire.*, log.</summary>
    public static readonly IReadOnlyList<DbSyncTable> All = new DbSyncTable[]
    {
        new MasterDataTable<SalesOrganization>("md.SalesOrg", MasterDataObject.SalesOrg),
        new MasterDataTable<Division>("md.Division", MasterDataObject.Division),
        new MasterDataTable<Plant>("md.Plant", MasterDataObject.Plant),
        new MasterDataTable<SalesOffice>("md.SalesOffice", MasterDataObject.SalesOffice),
        new MasterDataTable<SalesOfficeAssignment>("md.SalesOfficeAssignment", null),
        new MasterDataTable<SalesDistrict>("md.SalesDistrict", MasterDataObject.SalesDistrict),
        new MasterDataTable<ShippingCondition>("md.ShippingCondition", MasterDataObject.ShippingCondition),
        new MasterDataTable<PaymentMethod>("md.PaymentMethod", MasterDataObject.PaymentMethod),
        new MasterDataTable<VehicleType>("md.VehicleType", MasterDataObject.VehicleType),
        new MasterDataTable<CustomerGroup3>("md.CustomerGroup3", MasterDataObject.CustomerGroup3),
        new MasterDataTable<SoldToParty>("md.SoldTo", MasterDataObject.SoldTo),
        new MasterDataTable<ShipToParty>("md.ShipTo", MasterDataObject.ShipTo),
        new MasterDataTable<SoldToShipTo>("md.SoldToShipTo", MasterDataObject.SoldToShipTo),
        new MasterDataTable<MaterialType>("md.MaterialType", MasterDataObject.MaterialType),
        new MasterDataTable<MaterialGroup>("md.MaterialGroup", MasterDataObject.MaterialGroup),
        new MasterDataTable<Material>("md.Material", MasterDataObject.Material),
        new MasterDataTable<GlAccount>("md.GlAccount", MasterDataObject.GlAccount),
        new MasterDataTable<CostCenter>("md.CostCenter", MasterDataObject.CostCenter),
        new ApiEndpointsTable(),
        new ApiOutputProfilesTable(),
        new ApiUsersTable()
    };

    /// <summary>Mặc định: master data + cấu hình API (bật / tắt, mô tả, cột output). Tài khoản API (rdm.ApiUsers) phải ghi rõ trong --tables.</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = new[] { "md.*", "rdm.ApiEndpoints", "rdm.ApiOutputProfiles" };

    /// <summary>Chọn bảng theo mẫu: "md.*", "md.Material", "rdm.ApiEndpoints"... (không phân biệt hoa thường). Mẫu không khớp bảng nào → lỗi.</summary>
    public static IReadOnlyList<DbSyncTable> Select(IReadOnlyCollection<string>? patterns)
    {
        var list = patterns is { Count: > 0 } ? patterns : DefaultPatterns;
        var unknown = list.Where(p => !All.Any(t => Matches(t.Name, p))).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Không có bảng khớp: {string.Join(", ", unknown)}. Bảng hỗ trợ: {string.Join(", ", All.Select(t => t.Name))}.");
        return All.Where(t => list.Any(p => Matches(t.Name, p))).ToList();
    }

    internal static bool Matches(string table, string pattern)
    {
        pattern = pattern.Trim();
        return pattern.EndsWith(".*", StringComparison.Ordinal)
            ? table.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(table, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
