using System.Text.Json;

namespace Rdm.Application.Postings;

/// <summary>
/// Nhật ký + khóa idempotency cho mỗi lần ghi sang SAP. Unique theo (Operation, RequestId).
/// </summary>
public class SapPosting
{
    public Guid Id { get; set; }
    public SapOperation Operation { get; set; }
    public string RequestId { get; set; } = string.Empty;
    /// <summary>SHA-256 của payload — phát hiện requestId bị dùng lại cho payload khác.</summary>
    public string RequestHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public PostingStatus Status { get; set; }
    public int Attempts { get; set; }

    public string? DocumentNumber { get; set; }
    public string? CompanyCode { get; set; }
    public string? FiscalYear { get; set; }
    public string? ObjectType { get; set; }
    public string? ObjectKey { get; set; }
    public string? ObjectSystem { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReturnMessagesJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>true = xử lý nền (Hangfire) — client theo dõi bằng GET .../jobs/{operation}/{requestId}.</summary>
    public bool IsBackgroundJob { get; set; }
    /// <summary>Lúc job bắt đầu gọi SAP. Đã có giá trị mà job chạy lại = lần trước bị gián đoạn (server khởi động lại).</summary>
    public DateTime? JobClaimedAtUtc { get; set; }
    public int? ProgressTotal { get; set; }
    public int? ProgressDone { get; set; }
    public int? ProgressSucceeded { get; set; }
    public int? ProgressFailed { get; set; }

    public static SapPosting CreatePending(SapOperation op, string requestId, string requestJson, string requestHash, DateTime nowUtc,
        bool backgroundJob = false) => new()
    {
        Id = Guid.NewGuid(),
        Operation = op,
        RequestId = requestId,
        RequestJson = requestJson,
        RequestHash = requestHash,
        Status = PostingStatus.Pending,
        Attempts = 1,
        CreatedAtUtc = nowUtc,
        StartedAtUtc = nowUtc,
        IsBackgroundJob = backgroundJob
    };

    public void SetProgress(ProgressSnapshot p)
    {
        ProgressTotal = p.Total;
        ProgressDone = p.Done;
        ProgressSucceeded = p.Succeeded;
        ProgressFailed = p.Failed;
    }

    public void Complete(SapCallResult result, DateTime nowUtc, int durationMs)
    {
        Status = result.Status;
        ErrorCode = result.ErrorCode;
        ErrorMessage = Truncate(result.ErrorMessage, 2000);
        ReturnMessagesJson = result.Messages.Count == 0 ? null : JsonSerializer.Serialize(result.Messages);
        if (result.Document is { } d)
        {
            DocumentNumber = d.DocumentNumber;
            CompanyCode = d.CompanyCode;
            FiscalYear = d.FiscalYear;
            ObjectType = d.ObjectType;
            ObjectKey = d.ObjectKey;
            ObjectSystem = d.ObjectSystem;
        }
        CompletedAtUtc = nowUtc;
        DurationMs = durationMs;
    }

    public IReadOnlyList<SapReturnMessage> ReturnMessages() =>
        string.IsNullOrEmpty(ReturnMessagesJson)
            ? Array.Empty<SapReturnMessage>()
            : JsonSerializer.Deserialize<List<SapReturnMessage>>(ReturnMessagesJson) ?? new List<SapReturnMessage>();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
