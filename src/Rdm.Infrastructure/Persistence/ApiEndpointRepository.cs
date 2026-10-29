using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.ApiCatalog;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.Persistence;

/// <summary>Đọc rdm.ApiEndpoints bằng EF; ghi chỉ qua stored procedure (Persistence/Sql/ApiEndpoints.sql).</summary>
public sealed class ApiEndpointRepository : IApiEndpointRepository
{
    private readonly AppDbContext _db;

    public ApiEndpointRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ApiEndpointSetting>> GetAllAsync(CancellationToken ct) =>
        await _db.ApiEndpoints.AsNoTracking().OrderBy(x => x.GroupName).ThenBy(x => x.RouteTemplate).ThenBy(x => x.HttpMethod).ToListAsync(ct);

    public Task SyncAsync(IReadOnlyCollection<ApiEndpointInfo> endpoints, bool defaultActive, DateTime nowUtc, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("EndpointKey", typeof(string));
        table.Columns.Add("HttpMethod", typeof(string));
        table.Columns.Add("RouteTemplate", typeof(string));
        table.Columns.Add("GroupName", typeof(string));
        table.Columns.Add("ActionName", typeof(string));
        table.Columns.Add("IsLocked", typeof(bool));
        table.Columns.Add("Description", typeof(string));
        foreach (var e in endpoints)
            table.Rows.Add(e.EndpointKey, e.HttpMethod, e.RouteTemplate, e.GroupName, e.ActionName, e.IsLocked,
                e.Description is null ? DBNull.Value : (object)(e.Description.Length > 500 ? e.Description[..500] : e.Description));

        return ExecuteScalarAsync(_db, "rdm.usp_ApiEndpoint_Sync", new[]
        {
            Tvp("@Endpoints", "rdm.ApiEndpointTableType", table), P("@DefaultActive", defaultActive), P("@NowUtc", nowUtc)
        }, ct);
    }

    public async Task<bool> SetPublicDocAsync(string key, string? publicSummary, string? publicDescription, bool publicHidden, string updatedBy,
        DateTime nowUtc,
        CancellationToken ct) =>
        Convert.ToBoolean(await ExecuteScalarAsync(_db, "rdm.usp_ApiEndpoint_SetDoc", new[]
        {
            P("@EndpointKey", key), P("@PublicSummary", publicSummary), P("@PublicDescription", publicDescription), P("@PublicHidden", publicHidden),
            P("@UpdatedBy", updatedBy), P("@NowUtc", nowUtc)
        }, ct));

    public async Task<IReadOnlyList<string>> SetActiveAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string> groups, bool isActive,
        string updatedBy, string? note, DateTime nowUtc, CancellationToken ct)
    {
        var connection = (SqlConnection)_db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("rdm.usp_ApiEndpoint_SetActive", connection) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add(Tvp("@Keys", "rdm.StringListTableType", StringList(keys)));
            cmd.Parameters.Add(Tvp("@Groups", "rdm.StringListTableType", StringList(groups)));
            cmd.Parameters.Add(P("@IsActive", isActive));
            cmd.Parameters.Add(P("@UpdatedBy", updatedBy));
            cmd.Parameters.Add(P("@Note", note));
            cmd.Parameters.Add(P("@NowUtc", nowUtc));
            var changed = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) changed.Add(reader.GetString(0));
            return changed;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static DataTable StringList(IEnumerable<string> values)
    {
        var table = new DataTable();
        table.Columns.Add("Value", typeof(string));
        foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            table.Rows.Add(v);
        return table;
    }
}
