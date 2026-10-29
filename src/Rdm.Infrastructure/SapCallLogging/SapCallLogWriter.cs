using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rdm.Application.Abstractions;

namespace Rdm.Infrastructure.SapCallLogging;

/// <summary>Cấu hình bộ ghi nền (section SapCallLog).</summary>
public sealed class SapCallLogWriterOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Hàng đợi trong bộ nhớ; đầy → bỏ bản ghi mới (đếm Dropped, cảnh báo) thay vì chặn lời gọi SAP.</summary>
    public int QueueCapacity { get; set; } = 50_000;
    public int BatchSize { get; set; } = 200;
    /// <summary>Gom tối đa bao lâu trước khi ghi (ms) — độ trễ xuất hiện trong DB.</summary>
    public int FlushIntervalMs { get; set; } = 1000;
    /// <summary>DB lỗi → ghi lô ra file JSON lines ở đây, DB chạy lại thì tự nạp (tương đối: theo thư mục ứng dụng).</summary>
    public string FallbackDirectory { get; set; } = Path.Combine("logs", "sap-call-fallback");
    /// <summary>Giữ nhật ký bao nhiêu ngày (job dọn hằng ngày).</summary>
    public int RetentionDays { get; set; } = 180;
}

public sealed record SapCallLogWriterStats(
    bool Enabled, long Enqueued, long Written, long Dropped, long FailedBatches, long FallbackWritten, int QueueLength,
    int FallbackFilesPending, DateTime? LastFlushUtc, string? LastError, DateTime? LastErrorUtc);

/// <summary>
/// Bộ ghi nhật ký gọi SAP chạy nền: luồng gọi SAP chỉ <see cref="Write"/> vào hàng đợi (không I/O, không khóa);
/// vòng nền gom lô (BatchSize hoặc FlushIntervalMs) → 1 lần gọi rdm.usp_SapCallLog_InsertBatch (TVP).
/// DB lỗi → ghi lô ra file dự phòng (không mất bằng chứng), DB chạy lại → nạp lại file (procedure bỏ qua CallId đã có).
/// Dừng ứng dụng → ghi nốt hàng đợi.
/// </summary>
public sealed class SapCallLogWriter : BackgroundService, ISapCallLogSink
{
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly ISapCallLogStore _store;
    private readonly SapCallLogWriterOptions _options;
    private readonly ILogger<SapCallLogWriter> _logger;
    private readonly Channel<SapCallLogEntry> _queue;
    private readonly string _fallbackDir;

    private long _enqueued, _written, _dropped, _failedBatches, _fallbackWritten;
    private DateTime? _lastFlushUtc, _lastErrorUtc;
    private string? _lastError;
    private DateTime _lastDropWarningUtc;

    public SapCallLogWriter(ISapCallLogStore store, IConfiguration configuration, ILogger<SapCallLogWriter> logger)
    {
        _store = store;
        _logger = logger;
        _options = configuration.GetSection("SapCallLog").Get<SapCallLogWriterOptions>() ?? new SapCallLogWriterOptions();
        _queue = Channel.CreateBounded<SapCallLogEntry>(new BoundedChannelOptions(Math.Max(100, _options.QueueCapacity))
        {
            // Wait + chỉ dùng TryWrite: đầy thì TryWrite trả false ngay (không chặn) → đếm được bản ghi bị bỏ.
            // (DropWrite làm TryWrite vẫn trả true khi bỏ bản ghi → mất mà không biết.)
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _fallbackDir = Path.IsPathRooted(_options.FallbackDirectory)
            ? _options.FallbackDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.FallbackDirectory);
    }

    public bool Enabled => _options.Enabled;

    public void Write(SapCallLogEntry entry)
    {
        if (_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _enqueued);
            return;
        }
        Interlocked.Increment(ref _dropped);
        var now = DateTime.UtcNow;
        if (now - _lastDropWarningUtc > TimeSpan.FromMinutes(1))
        {
            _lastDropWarningUtc = now;
            _logger.LogWarning("Nhật ký gọi SAP: hàng đợi đầy ({Capacity}) — bỏ bản ghi (tổng đã bỏ {Dropped})",
                _options.QueueCapacity, Interlocked.Read(ref _dropped));
        }
    }

    public SapCallLogWriterStats Stats() => new(
        _options.Enabled, Interlocked.Read(ref _enqueued), Interlocked.Read(ref _written), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _failedBatches), Interlocked.Read(ref _fallbackWritten), _queue.Reader.Count, CountFallbackFiles(),
        _lastFlushUtc, _lastError, _lastErrorUtc);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled) SapCallLog.Attach(this);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        SapCallLog.Detach(this);
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        var batch = new List<SapCallLogEntry>(_options.BatchSize);
        var reader = _queue.Reader;
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, _options.FlushIntervalMs));
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                // Có bản ghi đầu tiên → gom thêm tới BatchSize hoặc hết thời gian gom.
                var deadline = DateTime.UtcNow + interval;
                while (batch.Count < _options.BatchSize)
                {
                    if (reader.TryRead(out var e)) { batch.Add(e); continue; }
                    var wait = deadline - DateTime.UtcNow;
                    if (wait <= TimeSpan.Zero) break;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(wait);
                    try { if (!await reader.WaitToReadAsync(cts.Token)) break; }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { break; }
                }
                await FlushAsync(batch);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Dừng ứng dụng: ghi nốt phần còn lại (lỗi → file dự phòng).
        while (reader.TryRead(out var e))
        {
            batch.Add(e);
            if (batch.Count >= _options.BatchSize) { await FlushAsync(batch); batch.Clear(); }
        }
        if (batch.Count > 0) await FlushAsync(batch);
    }

    private async Task FlushAsync(List<SapCallLogEntry> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _store.InsertBatchAsync(batch, timeout.Token);
            Interlocked.Add(ref _written, batch.Count);
            _lastFlushUtc = DateTime.UtcNow;
            await ReplayFallbackAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBatches);
            _lastError = ex.Message;
            _lastErrorUtc = DateTime.UtcNow;
            _logger.LogWarning(ex, "Nhật ký gọi SAP: ghi DB lỗi — lưu {Count} bản ghi ra file dự phòng", batch.Count);
            WriteFallback(batch);
        }
    }

    private void WriteFallback(IReadOnlyList<SapCallLogEntry> batch)
    {
        try
        {
            Directory.CreateDirectory(_fallbackDir);
            var file = Path.Combine(_fallbackDir, $"sapcall-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            var sb = new StringBuilder();
            foreach (var e in batch) sb.AppendLine(JsonSerializer.Serialize(e, FileJson));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            Interlocked.Add(ref _fallbackWritten, batch.Count);
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _dropped, batch.Count);
            _logger.LogError(ex, "Nhật ký gọi SAP: không ghi được file dự phòng — mất {Count} bản ghi", batch.Count);
        }
    }

    /// <summary>DB đã ghi được → nạp lại 1 file dự phòng mỗi lần (cũ nhất trước). Procedure bỏ qua CallId đã có.</summary>
    private async Task ReplayFallbackAsync()
    {
        if (!Directory.Exists(_fallbackDir)) return;
        var file = Directory.EnumerateFiles(_fallbackDir, "sapcall-*.jsonl").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        if (file is null) return;
        var entries = (await File.ReadAllLinesAsync(file))
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<SapCallLogEntry>(l, FileJson))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        foreach (var chunk in entries.Chunk(Math.Max(1, _options.BatchSize)))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _store.InsertBatchAsync(chunk, timeout.Token);
        }
        File.Delete(file);
        Interlocked.Add(ref _written, entries.Count);
        _logger.LogInformation("Nhật ký gọi SAP: đã nạp lại {Count} bản ghi từ file dự phòng {File}", entries.Count, Path.GetFileName(file));
    }

    private int CountFallbackFiles()
    {
        try { return Directory.Exists(_fallbackDir) ? Directory.EnumerateFiles(_fallbackDir, "sapcall-*.jsonl").Count() : 0; }
        catch { return -1; }
    }
}
