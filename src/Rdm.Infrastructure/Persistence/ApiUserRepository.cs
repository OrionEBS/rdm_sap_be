using Microsoft.EntityFrameworkCore;
using Rdm.Application.Auth;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.Persistence;

/// <summary>Đọc bằng EF; ghi qua procedure (Persistence/Sql/Auth_Procedures.sql).</summary>
public sealed class ApiUserRepository : IApiUserRepository
{
    private readonly AppDbContext _db;

    public ApiUserRepository(AppDbContext db)
    {
        _db = db;
    }

    // Collation mặc định của DB không phân biệt hoa thường → username cũng vậy.
    public Task<ApiUser?> FindAsync(string username, CancellationToken ct) =>
        _db.ApiUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Username == username, ct);

    public Task<List<ApiUser>> ListAsync(CancellationToken ct) =>
        _db.ApiUsers.AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => _db.ApiUsers.CountAsync(ct);

    public Task UpsertAsync(string username, string? passwordHash, string role, bool isActive, DateTime nowUtc, CancellationToken ct) =>
        ExecuteScalarAsync(_db, "rdm.usp_ApiUser_Upsert", new[]
        {
            P("@Username", username), P("@PasswordHash", passwordHash), P("@Role", role), P("@IsActive", isActive), P("@NowUtc", nowUtc)
        }, ct);

    public Task SetLastLoginAsync(string username, DateTime nowUtc, CancellationToken ct) =>
        ExecuteScalarAsync(_db, "rdm.usp_ApiUser_SetLastLogin", new[] { P("@Username", username), P("@NowUtc", nowUtc) }, ct);
}
