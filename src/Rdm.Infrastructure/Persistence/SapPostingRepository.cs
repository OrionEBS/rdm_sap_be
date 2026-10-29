using Microsoft.EntityFrameworkCore;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using static Rdm.Infrastructure.Persistence.StoredProcedure;

namespace Rdm.Infrastructure.Persistence;

public sealed class SapPostingRepository : ISapPostingRepository
{
    private readonly AppDbContext _db;

    public SapPostingRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<SapPosting?> FindAsync(SapOperation op, string requestId, CancellationToken ct) =>
        _db.SapPostings.AsNoTracking().FirstOrDefaultAsync(x => x.Operation == op && x.RequestId == requestId, ct);

    public Task<SapPosting?> GetAsync(Guid id, CancellationToken ct) =>
        _db.SapPostings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    // Ghi chỉ qua stored procedure (Persistence/Sql/Posting_Procedures.sql).

    public async Task<bool> TryAddAsync(SapPosting posting, CancellationToken ct) =>
        Convert.ToBoolean(await ExecuteScalarAsync(_db, "rdm.usp_SapPosting_TryInsert", new[]
        {
            P("@Id", posting.Id), P("@Operation", posting.Operation.ToString()), P("@RequestId", posting.RequestId),
            P("@RequestHash", posting.RequestHash), P("@RequestJson", posting.RequestJson), P("@Status", posting.Status.ToString()),
            P("@Attempts", posting.Attempts), P("@CreatedAtUtc", posting.CreatedAtUtc), P("@StartedAtUtc", posting.StartedAtUtc),
            P("@IsBackgroundJob", posting.IsBackgroundJob)
        }, ct));

    public async Task<bool> TryRestartFailedAsync(Guid id, string requestJson, string requestHash, DateTime nowUtc, CancellationToken ct,
        bool backgroundJob = false) =>
        Convert.ToBoolean(await ExecuteScalarAsync(_db, "rdm.usp_SapPosting_RestartFailed", new[]
        {
            P("@Id", id), P("@RequestJson", requestJson), P("@RequestHash", requestHash), P("@NowUtc", nowUtc),
            P("@IsBackgroundJob", backgroundJob)
        }, ct));

    public async Task<bool> TryClaimJobAsync(Guid id, DateTime nowUtc, CancellationToken ct) =>
        Convert.ToBoolean(await ExecuteScalarAsync(_db, "rdm.usp_SapPosting_ClaimJob", new[]
        {
            P("@Id", id), P("@NowUtc", nowUtc)
        }, ct));

    public Task UpdateProgressAsync(Guid id, ProgressSnapshot progress, CancellationToken ct) =>
        ExecuteScalarAsync(_db, "rdm.usp_SapPosting_Progress", new[]
        {
            P("@Id", id), P("@Total", progress.Total), P("@Done", progress.Done),
            P("@Succeeded", progress.Succeeded), P("@Failed", progress.Failed)
        }, ct);

    public Task SaveResultAsync(SapPosting posting, CancellationToken ct) =>
        ExecuteScalarAsync(_db, "rdm.usp_SapPosting_Complete", new[]
        {
            P("@Id", posting.Id), P("@Status", posting.Status.ToString()),
            P("@DocumentNumber", posting.DocumentNumber), P("@CompanyCode", posting.CompanyCode), P("@FiscalYear", posting.FiscalYear),
            P("@ObjectType", posting.ObjectType), P("@ObjectKey", posting.ObjectKey), P("@ObjectSystem", posting.ObjectSystem),
            P("@ErrorCode", posting.ErrorCode), P("@ErrorMessage", posting.ErrorMessage), P("@ReturnMessagesJson", posting.ReturnMessagesJson),
            P("@CompletedAtUtc", posting.CompletedAtUtc), P("@DurationMs", posting.DurationMs)
        }, ct);

    public async Task<PagedResult<SapPosting>> SearchAsync(PostingQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var q = _db.SapPostings.AsNoTracking();
        if (query.Operation is { } op) q = q.Where(x => x.Operation == op);
        if (query.Status is { } status) q = q.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(query.DocumentNumber)) q = q.Where(x => x.DocumentNumber == query.DocumentNumber.Trim());
        if (query.FromUtc is { } from) q = q.Where(x => x.CreatedAtUtc >= from);
        if (query.ToUtc is { } to) q = q.Where(x => x.CreatedAtUtc < to);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<SapPosting>(items, page, pageSize, total);
    }
}
