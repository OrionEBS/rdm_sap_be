using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.ApiCatalog;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.Persistence;

/// <summary>Đọc rdm.ApiOutputProfiles bằng EF; ghi chỉ qua stored procedure (Persistence/Sql/ApiOutputProfiles.sql).</summary>
public sealed class ApiOutputProfileRepository : IApiOutputProfileRepository
{
    private readonly AppDbContext _db;

    public ApiOutputProfileRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ApiOutputProfile>> GetAllAsync(CancellationToken ct) =>
        await _db.ApiOutputProfiles.AsNoTracking().OrderBy(x => x.EndpointKey).ThenBy(x => x.CopyName).ToListAsync(ct);

    public async Task<int> SaveAsync(int? profileId, string endpointKey, string? copyName, string? title, bool isActive,
        IReadOnlyList<ApiOutputField> fields, string updatedBy, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            var id = await ExecuteScalarAsync(_db, "rdm.usp_ApiOutputProfile_Save", new[]
            {
                P("@ProfileId", profileId), P("@EndpointKey", endpointKey), P("@CopyName", copyName), P("@Title", title), P("@IsActive", isActive),
                P("@FieldsJson", ApiOutputField.Serialize(fields)), P("@UpdatedBy", updatedBy), P("@NowUtc", nowUtc)
            }, ct);
            return id is null ? 0 : Convert.ToInt32(id);
        }
        catch (SqlException ex) when (ex.Number is 50409 or 2601 or 2627)
        {
            throw new ApiOutputConflictException($"Tên API sao chép '{copyName}' đã tồn tại.");
        }
    }

    public async Task<bool> DeleteAsync(int profileId, CancellationToken ct) =>
        Convert.ToBoolean(await ExecuteScalarAsync(_db, "rdm.usp_ApiOutputProfile_Delete", new[] { P("@ProfileId", profileId) }, ct));
}
