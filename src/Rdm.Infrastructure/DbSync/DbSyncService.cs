using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Rdm.Infrastructure.Persistence;

namespace Rdm.Infrastructure.DbSync;

/// <summary>Ảnh dữ liệu (snapshot) của DB nguồn — file JSON (nén gzip nếu đuôi .gz), mang theo gói triển khai.</summary>
public sealed class DbSnapshot
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public DateTime CreatedAtUtc { get; set; }
    public string SourceServer { get; set; } = string.Empty;
    public string SourceDatabase { get; set; } = string.Empty;
    /// <summary>Migration EF mới nhất của DB nguồn — DB đích phải cùng mức schema mới nạp được.</summary>
    public string? LastMigration { get; set; }
    public string? ToolVersion { get; set; }
    public Dictionary<string, int> RowCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonElement> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DbCompareRow(string Table, int? SourceRows, int? TargetRows);

/// <summary>
/// Đồng bộ dữ liệu giữa 2 DB RDM (VD DB local → DB Dev của đối tác) để môi trường mới có master data + cấu hình API ngay,
/// không phải chạy lại đồng bộ từ SAP. Đọc bằng EF; ghi chỉ qua stored procedure (md.usp_*_Replace, rdm.usp_ApiEndpoint_Import,
/// rdm.usp_ApiUser_Upsert).
/// </summary>
public sealed class DbSyncService
{
    public const string Trigger = "DbImport";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly Action<string> _log;

    public DbSyncService(Action<string>? log = null) => _log = log ?? (_ => { });

    public static AppDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema);
                sql.CommandTimeout(600);
            })
            .Options);

    /// <summary>"server / database" — không lộ mật khẩu khi in log.</summary>
    public static string Describe(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        return $"{b.DataSource} / {b.InitialCatalog}";
    }

    public static bool SameDatabase(string a, string b)
    {
        var x = new SqlConnectionStringBuilder(a);
        var y = new SqlConnectionStringBuilder(b);
        static string Host(string ds) => ds.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Replace("127.0.0.1", "localhost")
            .Replace("(local)", "localhost", StringComparison.OrdinalIgnoreCase).Replace(" ", "").TrimEnd('.').ToLowerInvariant();
        return Host(x.DataSource) == Host(y.DataSource) && string.Equals(x.InitialCatalog, y.InitialCatalog, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DbSnapshot> ExportAsync(string sourceConnectionString, IReadOnlyList<DbSyncTable> tables, string? toolVersion, CancellationToken ct)
    {
        await using var db = CreateContext(sourceConnectionString);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"DB nguồn {Describe(sourceConnectionString)} chưa chạy {pending.Count} migration ({pending[^1]}) — khởi động Rdm.Api trên DB nguồn trước.");

        var b = new SqlConnectionStringBuilder(sourceConnectionString);
        var snapshot = new DbSnapshot
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceServer = b.DataSource,
            SourceDatabase = b.InitialCatalog,
            LastMigration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault(),
            ToolVersion = toolVersion
        };
        foreach (var t in tables)
        {
            var (rows, count) = await t.ExportAsync(db, Json, ct);
            snapshot.Tables[t.Name] = rows;
            snapshot.RowCounts[t.Name] = count;
            _log($"  đọc {t.Name,-28} {count,8:N0} dòng");
        }
        return snapshot;
    }

    /// <summary>
    /// Nạp snapshot vào DB đích: chạy EF migration (tạo schema + procedure nếu DB mới), kiểm tra cùng mức schema, rồi nạp từng bảng.
    /// Lỗi 1 bảng: dừng ngay (bảng đó rollback trong procedure; bảng đã nạp trước giữ nguyên).
    /// </summary>
    public async Task<IReadOnlyList<DbTableResult>> ImportAsync(string targetConnectionString, DbSnapshot snapshot, IReadOnlyList<DbSyncTable> tables,
        DbImportMode mode, bool dryRun, bool migrate, CancellationToken ct)
    {
        if (snapshot.Format != DbSnapshot.CurrentFormat)
            throw new InvalidOperationException($"File snapshot định dạng {snapshot.Format}, công cụ chỉ đọc định dạng {DbSnapshot.CurrentFormat}.");

        await using var db = CreateContext(targetConnectionString);
        var known = db.Database.GetMigrations().ToList();
        if (snapshot.LastMigration is { } last && !known.Contains(last))
            throw new InvalidOperationException($"Snapshot tạo từ schema {last} mới hơn công cụ này — dùng Rdm.DbSync cùng phiên bản gói.");

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            if (dryRun || !migrate)
                throw new InvalidOperationException(
                    $"DB đích {Describe(targetConnectionString)} chưa có / thiếu {pending.Count} migration ({pending[^1]}) — " +
                    (dryRun
                        ? "xem trước không tạo schema: chạy thật có cho phép migration (trang quản trị: tick \"Cho phép tạo / cập nhật schema\"; CLI: bỏ --dry-run), hoặc khởi động Rdm.Api trên DB đích trước."
                        : "cho phép migration (trang quản trị: tick \"Cho phép tạo / cập nhật schema\"; CLI: bỏ --no-migrate), hoặc khởi động Rdm.Api trên DB đích trước."));
            _log($"  chạy {pending.Count} migration trên DB đích ({pending[0]} … {pending[^1]})");
            await db.Database.MigrateAsync(ct);
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        if (snapshot.LastMigration is { } src && applied != src)
            _log($"  CẢNH BÁO: schema nguồn {src}, schema đích {applied} — cột mới không có trong snapshot sẽ nhận giá trị mặc định.");

        var results = new List<DbTableResult>();
        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshot.Tables.TryGetValue(t.Name, out var rows))
            {
                results.Add(new DbTableResult(t.Name, "skip-not-in-snapshot", 0, null, null, "snapshot không có bảng này"));
                continue;
            }
            var r = await t.ImportAsync(db, rows, mode, dryRun, Trigger, Json, ct);
            results.Add(r);
            _log($"  {(dryRun ? "[thử] " : "")}{r.Table,-28} {r.Action,-22} nguồn {r.SourceRows,8:N0}  đích {Fmt(r.TargetRowsBefore)} → {Fmt(r.TargetRowsAfter)}" +
                 (r.Detail is null ? "" : $"  ({r.Detail})"));
        }
        return results;
    }

    public async Task<IReadOnlyList<DbCompareRow>> CompareAsync(string? sourceConnectionString, string? targetConnectionString,
        IReadOnlyList<DbSyncTable> tables, CancellationToken ct)
    {
        var src = sourceConnectionString is null ? null : await CountAllAsync(sourceConnectionString, tables, ct);
        var dst = targetConnectionString is null ? null : await CountAllAsync(targetConnectionString, tables, ct);
        return tables.Select(t => new DbCompareRow(t.Name, src?.GetValueOrDefault(t.Name), dst?.GetValueOrDefault(t.Name))).ToList();
    }

    private static async Task<Dictionary<string, int?>> CountAllAsync(string cs, IReadOnlyList<DbSyncTable> tables, CancellationToken ct)
    {
        await using var db = CreateContext(cs);
        await db.Database.OpenConnectionAsync(ct);   // lỗi kết nối / đăng nhập → báo lỗi, không coi là "chưa có bảng"
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            try { result[t.Name] = await t.CountAsync(db, ct); }
            catch (SqlException ex) when (ex.Number == 208) { result[t.Name] = null; }   // Invalid object name: DB chưa chạy migration
        }
        return result;
    }

    private static string Fmt(int? n) => n is null ? "       ?" : n.Value.ToString("N0").PadLeft(8);

    public static async Task SaveAsync(DbSnapshot snapshot, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await using (var file = File.Create(tmp))
        await using (var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                         ? new GZipStream(file, CompressionLevel.Optimal)
                         : (Stream)file)
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, Json, ct);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<DbSnapshot> LoadAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        await using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        DbSnapshot snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<DbSnapshot>(stream, Json, ct) ?? throw new InvalidDataException($"File {path} trống.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"File snapshot {path} hỏng hoặc chưa chép đủ (đối chiếu lại file .sha256 của gói): {ex.Message}", ex);
        }
        foreach (var (table, rows) in snapshot.Tables)
        {
            var n = rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : -1;
            if (!snapshot.RowCounts.TryGetValue(table, out var expected) || expected != n)
                throw new InvalidDataException($"File {path} hỏng: bảng {table} có {n} dòng, ghi nhận {expected}.");
        }
        return snapshot;
    }
}
