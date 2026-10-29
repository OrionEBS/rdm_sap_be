using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Rdm.Application.Abstractions;

namespace Rdm.Infrastructure.SapCallLogging;

/// <summary>
/// Đọc / ghi rdm.SapCallLog bằng ADO.NET (singleton — dùng cho bộ ghi nền, không qua DbContext).
/// GHI chỉ qua procedure: rdm.usp_SapCallLog_InsertBatch (TVP), rdm.usp_SapCallLog_Purge.
/// </summary>
public sealed class SapCallLogStore : ISapCallLogStore
{
    /// <summary>Thứ tự cột = rdm.SapCallLogTableType (Sql/SapCallLog.sql) — có unit test kiểm tra.</summary>
    internal static readonly string[] Columns =
    {
        "CallId", "StartedAtUtc", "DurationMs", "FunctionName", "Status", "ErrorCode", "Message", "CorrelationId", "RequestId",
        "Operation", "PostingId", "Source", "UserName", "ClientIp", "HttpMethod", "Path", "TraceId", "JobId", "Machine", "SapSystem",
        "Truncated", "RequestJson", "ResponseJson"
    };

    private const string RowColumns =
        "Id, CallId, StartedAtUtc, DurationMs, FunctionName, Status, ErrorCode, Message, CorrelationId, RequestId, Operation, PostingId, " +
        "Source, UserName, ClientIp, HttpMethod, Path, TraceId, JobId, Machine, SapSystem, RequestBytes, ResponseBytes, Truncated";

    private readonly string _connectionString;

    public SapCallLogStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
                            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer chưa cấu hình.");
    }

    public async Task InsertBatchAsync(IReadOnlyList<SapCallLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        using var table = ToDataTable(entries);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("rdm.usp_SapCallLog_InsertBatch", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 60 };
        cmd.Parameters.Add(new SqlParameter("@Rows", SqlDbType.Structured) { TypeName = "rdm.SapCallLogTableType", Value = table });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static DataTable ToDataTable(IEnumerable<SapCallLogEntry> entries)
    {
        var t = new DataTable();
        t.Columns.Add("CallId", typeof(Guid));
        t.Columns.Add("StartedAtUtc", typeof(DateTime));
        t.Columns.Add("DurationMs", typeof(int));
        foreach (var c in new[] { "FunctionName", "Status", "ErrorCode", "Message", "CorrelationId", "RequestId", "Operation" })
            t.Columns.Add(c, typeof(string));
        t.Columns.Add("PostingId", typeof(Guid));
        foreach (var c in new[] { "Source", "UserName", "ClientIp", "HttpMethod", "Path", "TraceId", "JobId", "Machine", "SapSystem" })
            t.Columns.Add(c, typeof(string));
        t.Columns.Add("Truncated", typeof(bool));
        t.Columns.Add("RequestJson", typeof(string));
        t.Columns.Add("ResponseJson", typeof(string));

        foreach (var e in entries)
        {
            var c = e.Context;
            t.Rows.Add(
                e.CallId, e.StartedAtUtc, e.DurationMs, Cut(e.FunctionName, 64), Cut(e.Status, 12), N(Cut(e.ErrorCode, 100)), N(Cut(e.Message, 1000)),
                N(Cut(c?.CorrelationId, 100)), N(Cut(c?.RequestId, 64)), N(Cut(c?.Operation, 64)), c?.PostingId is { } pid ? pid : DBNull.Value,
                N(Cut(c?.Source, 16)), N(Cut(c?.UserName, 100)), N(Cut(c?.ClientIp, 64)), N(Cut(c?.HttpMethod, 10)), N(Cut(c?.Path, 300)),
                N(Cut(c?.TraceId, 64)), N(Cut(c?.JobId, 64)), N(Cut(e.Machine, 64)), N(Cut(e.SapSystem, 32)),
                e.Truncated, e.RequestJson, e.ResponseJson);
        }
        return t;
    }

    private static object N(string? s) => s is null ? DBNull.Value : s;
    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    public async Task<SapCallLogPage> SearchAsync(SapCallLogFilter f, CancellationToken ct)
    {
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 500);
        var where = new StringBuilder(" WHERE 1 = 1");
        var ps = new List<SqlParameter>();
        void Add(string clause, string name, object? value)
        {
            if (value is null || value is string { Length: 0 }) return;
            where.Append(" AND ").Append(clause);
            ps.Add(new SqlParameter(name, value));
        }
        Add("StartedAtUtc >= @From", "@From", f.FromUtc);
        Add("StartedAtUtc < @To", "@To", f.ToUtc);
        Add("FunctionName = @Fn", "@Fn", f.FunctionName?.Trim().ToUpperInvariant());
        Add("Status = @Status", "@Status", f.Status?.Trim().ToUpperInvariant());
        Add("CorrelationId = @Corr", "@Corr", f.CorrelationId?.Trim());
        Add("RequestId = @Req", "@Req", f.RequestId?.Trim());
        Add("Operation = @Op", "@Op", f.Operation?.Trim());
        Add("UserName = @User", "@User", f.UserName?.Trim());
        Add("PostingId = @Posting", "@Posting", f.PostingId);
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            where.Append(" AND (Message LIKE @Text OR Path LIKE @Text OR ErrorCode LIKE @Text)");
            ps.Add(new SqlParameter("@Text", "%" + f.Text.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%"));
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        int total;
        await using (var count = new SqlCommand("SELECT COUNT_BIG(*) FROM rdm.SapCallLog" + where, conn))
        {
            foreach (var p in ps) count.Parameters.Add(Clone(p));
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        var items = new List<SapCallLogRow>();
        await using (var cmd = new SqlCommand(
                         $"SELECT {RowColumns} FROM rdm.SapCallLog{where} ORDER BY Id DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", conn))
        {
            foreach (var p in ps) cmd.Parameters.Add(Clone(p));
            cmd.Parameters.Add(new SqlParameter("@Skip", (page - 1) * size));
            cmd.Parameters.Add(new SqlParameter("@Take", size));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(ReadRow(r));
        }
        return new SapCallLogPage(total, page, size, items);
    }

    public async Task<SapCallLogDetail?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT {RowColumns}, CAST(DECOMPRESS(RequestJsonZ) AS nvarchar(max)), CAST(DECOMPRESS(ResponseJsonZ) AS nvarchar(max)) " +
            "FROM rdm.SapCallLog WHERE Id = @Id", conn);
        cmd.Parameters.Add(new SqlParameter("@Id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var row = ReadRow(r);
        var n = RowColumns.Split(',').Length;
        return new SapCallLogDetail(row, r.IsDBNull(n) ? "{}" : r.GetString(n), r.IsDBNull(n + 1) ? "{}" : r.GetString(n + 1));
    }

    public async Task<IReadOnlyList<SapCallLogSummaryRow>> SummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT FunctionName, Status, COUNT(*), AVG(CAST(DurationMs AS bigint)), MAX(DurationMs), MIN(StartedAtUtc), MAX(StartedAtUtc) " +
            "FROM rdm.SapCallLog WHERE StartedAtUtc >= @From AND StartedAtUtc < @To GROUP BY FunctionName, Status ORDER BY FunctionName, Status", conn);
        cmd.Parameters.Add(new SqlParameter("@From", fromUtc));
        cmd.Parameters.Add(new SqlParameter("@To", toUtc));
        var list = new List<SapCallLogSummaryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SapCallLogSummaryRow(r.GetString(0), r.GetString(1), r.GetInt32(2), (int)r.GetInt64(3), r.GetInt32(4),
                DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)));
        return list;
    }

    public async Task<IReadOnlyList<SapCallReconcileRow>> ReconcileAsync(DateTime fromUtc, DateTime toUtc, string? userName, string? requestId,
        string? correlationId, int max, CancellationToken ct)
    {
        var sql = new StringBuilder(
            "SELECT TOP (@Max) l.StartedAtUtc, l.CorrelationId, l.RequestId, l.Operation, l.Status, l.Message, l.DurationMs, " +
            "p.Status, p.DocumentNumber, p.FiscalYear, p.CompanyCode " +
            "FROM rdm.SapCallLog l LEFT JOIN rdm.SapPostings p ON p.Id = l.PostingId " +
            "WHERE l.StartedAtUtc >= @From AND l.StartedAtUtc < @To AND l.Source = 'http'");
        var ps = new List<SqlParameter>
        {
            new("@Max", Math.Clamp(max, 1, 10_000)), new("@From", fromUtc), new("@To", toUtc)
        };
        if (userName is not null) { sql.Append(" AND l.UserName = @User"); ps.Add(new SqlParameter("@User", userName)); }
        if (!string.IsNullOrWhiteSpace(requestId)) { sql.Append(" AND l.RequestId = @Req"); ps.Add(new SqlParameter("@Req", requestId.Trim())); }
        if (!string.IsNullOrWhiteSpace(correlationId)) { sql.Append(" AND l.CorrelationId = @Corr"); ps.Add(new SqlParameter("@Corr", correlationId.Trim())); }
        sql.Append(" ORDER BY l.Id DESC");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql.ToString(), conn);
        cmd.Parameters.AddRange(ps.ToArray());
        var list = new List<SapCallReconcileRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SapCallReconcileRow(
                DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), S(r, 1), S(r, 2), S(r, 3), r.GetString(4), S(r, 5), r.GetInt32(6),
                S(r, 7), S(r, 8), S(r, 9), S(r, 10)));
        return list;
    }

    public async Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("rdm.usp_SapCallLog_Purge", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 3600 };
        cmd.Parameters.Add(new SqlParameter("@BeforeUtc", beforeUtc));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static SapCallLogRow ReadRow(SqlDataReader r) => new(
        r.GetInt64(0), r.GetGuid(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetInt32(3), r.GetString(4), r.GetString(5),
        S(r, 6), S(r, 7), S(r, 8), S(r, 9), S(r, 10), r.IsDBNull(11) ? null : r.GetGuid(11), S(r, 12), S(r, 13), S(r, 14), S(r, 15), S(r, 16),
        S(r, 17), S(r, 18), S(r, 19), S(r, 20), r.GetInt32(21), r.GetInt32(22), r.GetBoolean(23));

    private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SqlParameter Clone(SqlParameter p) => new(p.ParameterName, p.Value);
}
