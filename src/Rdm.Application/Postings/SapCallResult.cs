namespace Rdm.Application.Postings;

/// <summary>1 dòng BAPIRET2 — lưu nguyên văn, không nuốt message SAP.</summary>
public sealed record SapReturnMessage(
    string Type,
    string Id,
    string Number,
    string Message,
    string? MessageV1 = null,
    string? MessageV2 = null,
    string? MessageV3 = null,
    string? MessageV4 = null,
    string? Parameter = null,
    string? Field = null,
    string? Item = null)
{
    // Item: đối tượng mà message thuộc về khi 1 request xử lý nhiều đối tượng (vd. mã vật tư).
    public bool IsError => Type is "E" or "A";
}

/// <summary>Định danh chứng từ SAP sinh ra.</summary>
public sealed record SapDocumentRef(
    string? DocumentNumber,
    string? CompanyCode = null,
    string? FiscalYear = null,
    string? ObjectType = null,
    string? ObjectKey = null,
    string? ObjectSystem = null);

/// <summary>
/// Kết quả 1 lời gọi SAP. Client SAP KHÔNG throw cho lỗi phía SAP mà phân loại vào đây,
/// vì việc phân biệt Failed (chắc chắn chưa ghi) và Unknown (có thể đã ghi) quyết định có được gửi lại hay không.
/// </summary>
public sealed record SapCallResult(
    PostingStatus Status,
    IReadOnlyList<SapReturnMessage> Messages,
    SapDocumentRef? Document = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static SapCallResult Success(SapDocumentRef document, IReadOnlyList<SapReturnMessage> messages) =>
        new(PostingStatus.Succeeded, messages, document);

    public static SapCallResult Failed(string errorCode, string message, IReadOnlyList<SapReturnMessage>? messages = null) =>
        new(PostingStatus.Failed, messages ?? Array.Empty<SapReturnMessage>(), null, errorCode, message);

    public static SapCallResult Unknown(string message, IReadOnlyList<SapReturnMessage>? messages = null) =>
        new(PostingStatus.Unknown, messages ?? Array.Empty<SapReturnMessage>(), null, SapErrorCodes.UnknownOutcome, message);
}
