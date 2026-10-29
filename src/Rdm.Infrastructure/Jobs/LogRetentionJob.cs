using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rdm.Infrastructure.Persistence;

namespace Rdm.Infrastructure.Jobs;

/// <summary>
/// Job Hangfire định kỳ xóa log Serilog cũ trong SQL. KHÔNG xóa rdm.SapPostings (nhật ký chứng từ + khóa idempotency).
/// </summary>
public sealed class LogRetentionJob
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<LogRetentionJob> _logger;
    private readonly Rdm.Application.Abstractions.ISapCallLogStore _sapCallLog;

    public LogRetentionJob(AppDbContext db, IConfiguration config, ILogger<LogRetentionJob> logger,
        Rdm.Application.Abstractions.ISapCallLogStore sapCallLog)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _sapCallLog = sapCallLog;
    }

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task PurgeSerilogEventsAsync(CancellationToken ct)
    {
        var days = _config.GetValue("Jobs:LogRetentionDays", 30);
        var cutoff = DateTime.Now.AddDays(-days);
        var deleted = Convert.ToInt32(await StoredProcedure.ExecuteScalarAsync(_db, "dbo.usp_SerilogEvents_Purge",
            new[] { StoredProcedure.P("@Before", cutoff) }, ct));
        _logger.LogInformation("Đã xóa {Count} dòng log Serilog cũ hơn {Days} ngày", deleted, days);
    }

    /// <summary>Xóa nhật ký gọi SAP (rdm.SapCallLog) cũ hơn SapCallLog:RetentionDays (mặc định 180 ngày) — theo lô qua procedure.</summary>
    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task PurgeSapCallLogAsync(CancellationToken ct)
    {
        var days = Math.Max(1, _config.GetValue("SapCallLog:RetentionDays", 180));
        var deleted = await _sapCallLog.PurgeAsync(DateTime.UtcNow.AddDays(-days), ct);
        _logger.LogInformation("Đã xóa {Count} dòng nhật ký gọi SAP cũ hơn {Days} ngày", deleted, days);
    }
}
