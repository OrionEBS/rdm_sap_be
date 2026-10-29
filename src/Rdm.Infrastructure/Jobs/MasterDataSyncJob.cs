using Hangfire;
using Rdm.Application.MasterData;

namespace Rdm.Infrastructure.Jobs;

/// <summary>Job Hangfire định kỳ đồng bộ toàn bộ master data SD từ SAP về DB local.</summary>
public sealed class MasterDataSyncJob
{
    private readonly IMasterDataSyncService _sync;

    public MasterDataSyncJob(IMasterDataSyncService sync)
    {
        _sync = sync;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunAsync(CancellationToken ct)
    {
        var results = await _sync.SyncAsync(null, "Schedule", ct);
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
            throw new InvalidOperationException("Đồng bộ lỗi: " + string.Join("; ", failed.Select(f => $"{f.Object}: {f.Error}")));
    }
}
