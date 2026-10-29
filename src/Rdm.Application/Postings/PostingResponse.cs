namespace Rdm.Application.Postings;

/// <summary>Kết quả trả về client cho mọi API ghi SAP.</summary>
public sealed class PostingResponse
{
    public Guid Id { get; init; }
    public SapOperation Operation { get; init; }
    public string FunctionName { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public PostingStatus Status { get; init; }
    /// <summary>true = requestId đã post thành công trước đó; trả lại kết quả cũ, KHÔNG gọi SAP lần nữa.</summary>
    public bool Replayed { get; init; }
    public int Attempts { get; init; }

    /// <summary>Số chứng từ SAP (BELNR với FI, VBELN với VA01/VA41). VBN1 tạo condition record nên không có số.</summary>
    public string? DocumentNumber { get; init; }
    public string? CompanyCode { get; init; }
    public string? FiscalYear { get; init; }
    /// <summary>OBJ_TYPE / OBJ_KEY / OBJ_SYS — dùng khi cần đảo chứng từ bằng BAPI_ACC_DOCUMENT_REV_POST.</summary>
    public string? ObjectType { get; init; }
    public string? ObjectKey { get; init; }
    public string? ObjectSystem { get; init; }

    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SapReturnMessage> Messages { get; init; } = Array.Empty<SapReturnMessage>();

    public DateTime CreatedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public int? DurationMs { get; init; }

    /// <summary>true = xử lý nền; Status = Pending nghĩa là đang chờ / đang chạy — xem <see cref="Progress"/>.</summary>
    public bool IsBackgroundJob { get; init; }
    public ProgressSnapshot? Progress { get; init; }

    public static PostingResponse From(SapPosting p, bool replayed = false) => new()
    {
        Id = p.Id,
        Operation = p.Operation,
        FunctionName = p.Operation.FunctionName(),
        RequestId = p.RequestId,
        Status = p.Status,
        Replayed = replayed,
        Attempts = p.Attempts,
        DocumentNumber = p.DocumentNumber,
        CompanyCode = p.CompanyCode,
        FiscalYear = p.FiscalYear,
        ObjectType = p.ObjectType,
        ObjectKey = p.ObjectKey,
        ObjectSystem = p.ObjectSystem,
        ErrorCode = p.ErrorCode,
        ErrorMessage = p.ErrorMessage,
        Messages = p.ReturnMessages(),
        CreatedAtUtc = p.CreatedAtUtc,
        CompletedAtUtc = p.CompletedAtUtc,
        DurationMs = p.DurationMs,
        IsBackgroundJob = p.IsBackgroundJob,
        Progress = p.ProgressTotal is { } total
            ? new ProgressSnapshot(total, p.ProgressDone ?? 0, p.ProgressSucceeded ?? 0, p.ProgressFailed ?? 0)
            : null
    };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class PostingQuery
{
    public SapOperation? Operation { get; set; }
    public PostingStatus? Status { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// requestId đang xử lý / ở trạng thái Unknown / bị dùng lại cho payload khác → HTTP 409.
/// </summary>
public sealed class PostingConflictException : Exception
{
    public SapPosting? Existing { get; }

    public PostingConflictException(string message, SapPosting? existing) : base(message)
    {
        Existing = existing;
    }
}
