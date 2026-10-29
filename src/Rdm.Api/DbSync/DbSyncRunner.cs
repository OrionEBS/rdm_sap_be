using System.Reflection;
using Rdm.Infrastructure.DbSync;

namespace Rdm.Api.DbSync;

/// <summary>Kết quả 1 lần đồng bộ (hoặc xem trước) DB này → DB Dev.</summary>
public sealed record DbSyncRunResult(
    bool DryRun, string Mode, bool Migrate, string Source, string Target, string RequestedBy, DateTime StartedAtUtc, int DurationMs,
    bool Success, string? Error, IReadOnlyList<DbTableResult> Tables, IReadOnlyList<string> Log);

public sealed class DbSyncBusyException() : Exception("Đang có 1 lần đồng bộ khác chạy — chờ xong rồi thử lại.");

/// <summary>
/// Đồng bộ DB của API này (nguồn) sang DB Dev (đích, DbSync:TargetConnectionString) theo nút bấm trên trang quản trị.
/// Dùng chung engine với CLI Rdm.DbSync: đọc bằng EF, ghi DB đích chỉ qua stored procedure. Mỗi lúc chỉ 1 lần chạy.
/// Không cấu hình DbSync:TargetConnectionString → tính năng tắt.
/// </summary>
public sealed class DbSyncRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConfiguration _config;
    private readonly ILogger<DbSyncRunner> _logger;

    public DbSyncRunner(IConfiguration config, ILogger<DbSyncRunner> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string? SourceConnectionString =>
        First(_config["DbSync:SourceConnectionString"], _config.GetConnectionString("SqlServer"));

    public string? TargetConnectionString => First(_config["DbSync:TargetConnectionString"]);

    public bool Enabled => SourceConnectionString is not null && TargetConnectionString is not null;

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Lần đồng bộ thật gần nhất (không tính xem trước) kể từ khi API khởi động.</summary>
    public DbSyncRunResult? LastRun { get; private set; }

    public IReadOnlyList<DbSyncTable> Tables =>
        DbSyncTables.Select(_config.GetSection("DbSync:Tables").Get<string[]>());

    /// <summary>Tên database đích — người dùng phải gõ lại để xác nhận chế độ Replace.</summary>
    public string TargetDatabase =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(TargetConnectionString ?? string.Empty).InitialCatalog;

    public bool AllowMigrate => _config.GetValue("DbSync:AllowMigrate", true);

    public Task<IReadOnlyList<DbCompareRow>> CompareAsync(CancellationToken ct)
    {
        EnsureEnabled();
        return new DbSyncService().CompareAsync(SourceConnectionString, TargetConnectionString, Tables, ct);
    }

    public async Task<DbSyncRunResult> RunAsync(DbImportMode mode, bool dryRun, bool migrate, string requestedBy)
    {
        EnsureEnabled();
        if (!await _gate.WaitAsync(0)) throw new DbSyncBusyException();
        var started = DateTime.UtcNow;
        var log = new List<string>();
        var source = DbSyncService.Describe(SourceConnectionString!);
        var target = DbSyncService.Describe(TargetConnectionString!);
        try
        {
            if (DbSyncService.SameDatabase(SourceConnectionString!, TargetConnectionString!))
                throw new InvalidOperationException($"DB nguồn và DB đích trùng nhau ({target}) — kiểm tra DbSync:TargetConnectionString.");

            // Không gắn với request: người dùng đóng trình duyệt giữa chừng thì lần chạy vẫn chạy hết (mỗi bảng là 1 transaction).
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(_config.GetValue("DbSync:TimeoutMinutes", 15)));
            var engine = new DbSyncService(line => log.Add(line.Trim()));
            var version = typeof(DbSyncRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var snapshot = await engine.ExportAsync(SourceConnectionString!, Tables, version, timeout.Token);
            var tables = await engine.ImportAsync(TargetConnectionString!, snapshot, Tables, mode, dryRun, migrate && AllowMigrate, timeout.Token);

            var result = new DbSyncRunResult(dryRun, mode.ToString(), migrate, source, target, requestedBy, started,
                (int)(DateTime.UtcNow - started).TotalMilliseconds, true, null, tables, log);
            if (!dryRun)
            {
                LastRun = result;
                _logger.LogWarning("Đồng bộ DB {Source} → {Target} ({Mode}) bởi {User}: {Written} bảng nạp, {Skipped} bỏ qua, {DurationMs} ms",
                    source, target, mode, requestedBy, tables.Count(t => t.Action is "replace" or "merge"),
                    tables.Count(t => t.Action is not ("replace" or "merge")), result.DurationMs);
            }
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or Microsoft.Data.SqlClient.SqlException or OperationCanceledException)
        {
            var error = ex is OperationCanceledException ? "Quá thời gian DbSync:TimeoutMinutes." : ex.Message;
            var result = new DbSyncRunResult(dryRun, mode.ToString(), migrate, source, target, requestedBy, started,
                (int)(DateTime.UtcNow - started).TotalMilliseconds, false, error, Array.Empty<DbTableResult>(), log);
            if (!dryRun) LastRun = result;
            _logger.LogError(ex, "Đồng bộ DB {Source} → {Target} ({Mode}, thử = {DryRun}) bởi {User} lỗi", source, target, mode, dryRun, requestedBy);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureEnabled()
    {
        if (!Enabled) throw new InvalidOperationException("Chưa cấu hình DbSync:TargetConnectionString — tính năng đồng bộ DB Dev đang tắt.");
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
