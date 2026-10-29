namespace Rdm.Application.Abstractions;

/// <summary>
/// Ngữ cảnh của lời gọi SAP — truyền ngầm (AsyncLocal) từ HTTP request / job xuống tầng NCo để mỗi dòng nhật ký gọi SAP
/// gắn được với request của hệ thống gọi (EU): X-Correlation-Id, requestId (khóa idempotency), user, IP, URL, job.
/// Bất biến: mỗi tầng tạo bản sao có thêm thông tin (<see cref="With"/>) rồi <see cref="Begin"/>.
/// </summary>
public sealed record SapCallContext
{
    private static readonly AsyncLocal<SapCallContext?> Slot = new();

    public static SapCallContext? Current => Slot.Value;

    /// <summary>X-Correlation-Id / X-Request-Id do EU gửi (không có → trace id của request) — trả lại ở header response.</summary>
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    /// <summary>http · job · schedule.</summary>
    public string? Source { get; init; }
    public string? UserName { get; init; }
    public string? ClientIp { get; init; }
    public string? HttpMethod { get; init; }
    public string? Path { get; init; }
    /// <summary>Thao tác nghiệp vụ (SapOperation / tên đồng bộ master data).</summary>
    public string? Operation { get; init; }
    /// <summary>requestId của EU (khóa idempotency rdm.SapPostings).</summary>
    public string? RequestId { get; init; }
    public Guid? PostingId { get; init; }
    public string? JobId { get; init; }

    public SapCallContext With(Func<SapCallContext, SapCallContext> change) => change(this);

    public static IDisposable Begin(SapCallContext context)
    {
        var previous = Slot.Value;
        Slot.Value = context;
        return new Scope(previous);
    }

    /// <summary>Ngữ cảnh hiện tại (hoặc rỗng) thêm thao tác nghiệp vụ.</summary>
    public static IDisposable BeginOperation(string operation, string? requestId = null, Guid? postingId = null, string? source = null) =>
        Begin((Current ?? new SapCallContext()) with
        {
            Operation = operation,
            RequestId = requestId ?? Current?.RequestId,
            PostingId = postingId ?? Current?.PostingId,
            Source = source ?? Current?.Source
        });

    private sealed class Scope(SapCallContext? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Slot.Value = previous;
        }
    }
}

/// <summary>Kết quả 1 lời gọi SAP trong nhật ký.</summary>
public static class SapCallStatus
{
    /// <summary>Gọi xong, không có message E / A.</summary>
    public const string Ok = "OK";
    /// <summary>Gọi xong, có message W (không có E / A).</summary>
    public const string Warning = "WARNING";
    /// <summary>Gọi xong, SAP trả message E / A (RETURN / BAPIRET2 / BDCMSGCOLL).</summary>
    public const string Error = "ERROR";
    /// <summary>Lỗi RFC: ABAP exception / runtime error, mất kết nối, logon — xem ResponseJson.exception.</summary>
    public const string Exception = "EXCEPTION";
}

/// <summary>1 lời gọi function module SAP — bằng chứng đối soát: nội dung gửi, kết quả nhận (JSON), thời gian, ngữ cảnh.</summary>
public sealed record SapCallLogEntry
{
    public Guid CallId { get; init; } = Guid.NewGuid();
    public DateTime StartedAtUtc { get; init; }
    public int DurationMs { get; init; }
    public string FunctionName { get; init; } = string.Empty;
    public string Status { get; init; } = SapCallStatus.Ok;
    /// <summary>EXCEPTION: loại exception NCo / key ABAP; ERROR: mã message SAP đầu tiên (ID/NUMBER).</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Message E / A (hoặc W) đầu tiên, hoặc text exception — để lọc nhanh không cần mở JSON.</summary>
    public string? Message { get; init; }
    public SapCallContext? Context { get; init; }
    public string? Machine { get; init; }
    public string? SapSystem { get; init; }
    /// <summary>Tham số gửi: IMPORT / CHANGING / TABLES (trước khi gọi).</summary>
    public string RequestJson { get; init; } = "{}";
    /// <summary>Kết quả: EXPORT / CHANGING / TABLES (sau khi gọi), hoặc {"exception": ...}.</summary>
    public string ResponseJson { get; init; } = "{}";
    /// <summary>Có bảng bị cắt bớt dòng (SapCallLog:MaxTableRows / TableRowLimits).</summary>
    public bool Truncated { get; init; }
}

/// <summary>Nơi nhận nhật ký gọi SAP. Write KHÔNG được chặn luồng gọi SAP (chỉ đưa vào hàng đợi).</summary>
public interface ISapCallLogSink
{
    bool Enabled { get; }
    void Write(SapCallLogEntry entry);
}

/// <summary>
/// Điểm nối tĩnh giữa tầng NCo (Rdm.Sap — có phần đọc bảng tĩnh) và bộ ghi nhật ký (Rdm.Infrastructure).
/// Chưa gắn bộ ghi (unit test, công cụ) → không ghi gì, không tốn chi phí tạo JSON.
/// </summary>
public static class SapCallLog
{
    private static ISapCallLogSink _sink = NullSink.Instance;

    public static ISapCallLogSink Sink => _sink;

    public static void Attach(ISapCallLogSink sink) => _sink = sink;

    public static void Detach(ISapCallLogSink sink)
    {
        if (ReferenceEquals(_sink, sink)) _sink = NullSink.Instance;
    }

    private sealed class NullSink : ISapCallLogSink
    {
        public static readonly NullSink Instance = new();
        public bool Enabled => false;
        public void Write(SapCallLogEntry entry) { }
    }
}

// ------------------------------------------------------------------ đọc nhật ký (đối soát)

public sealed record SapCallLogFilter
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? FunctionName { get; init; }
    public string? Status { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public string? Operation { get; init; }
    public string? UserName { get; init; }
    public Guid? PostingId { get; init; }
    /// <summary>Tìm trong message / path.</summary>
    public string? Text { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Dòng danh sách (không kèm JSON).</summary>
public sealed record SapCallLogRow(
    long Id, Guid CallId, DateTime StartedAtUtc, int DurationMs, string FunctionName, string Status, string? ErrorCode, string? Message,
    string? CorrelationId, string? RequestId, string? Operation, Guid? PostingId, string? Source, string? UserName, string? ClientIp,
    string? HttpMethod, string? Path, string? TraceId, string? JobId, string? Machine, string? SapSystem, int RequestBytes, int ResponseBytes,
    bool Truncated);

public sealed record SapCallLogPage(int Total, int Page, int PageSize, IReadOnlyList<SapCallLogRow> Items);

public sealed record SapCallLogDetail(SapCallLogRow Call, string RequestJson, string ResponseJson);

public sealed record SapCallLogSummaryRow(string FunctionName, string Status, int Calls, int AvgMs, int MaxMs, DateTime FirstUtc, DateTime LastUtc);

/// <summary>Dòng đối soát cho EU: không lộ tên hàm SAP / JSON kỹ thuật; kèm kết quả posting (số chứng từ) nếu có.</summary>
public sealed record SapCallReconcileRow(
    DateTime StartedAtUtc, string? CorrelationId, string? RequestId, string? Operation, string Status, string? Message, int DurationMs,
    string? PostingStatus, string? DocumentNumber, string? FiscalYear, string? CompanyCode);

public interface ISapCallLogStore
{
    Task InsertBatchAsync(IReadOnlyList<SapCallLogEntry> entries, CancellationToken ct);
    Task<SapCallLogPage> SearchAsync(SapCallLogFilter filter, CancellationToken ct);
    Task<SapCallLogDetail?> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<SapCallLogSummaryRow>> SummaryAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<IReadOnlyList<SapCallReconcileRow>> ReconcileAsync(DateTime fromUtc, DateTime toUtc, string? userName, string? requestId,
        string? correlationId, int max, CancellationToken ct);
    Task<int> PurgeAsync(DateTime beforeUtc, CancellationToken ct);
}
