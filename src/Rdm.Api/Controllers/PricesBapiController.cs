using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Api.Controllers;

/// <summary>
/// Bảng giá ZVLP / ZVDI / ZMIN / ZVIV qua <b>BAPI_PRICES_CONDITIONS</b> (vỏ RFC Z_ESD016_PRICES_CONDITIONS) — tách riêng với
/// phương án BDC ở /api/sd/prices để so sánh. Ghi theo lô (Sap:PriceBapiChunkSize bảng giá / 1 lần gọi + commit);
/// lô bị SAP từ chối → ghi lại từng bảng giá để báo đúng dòng lỗi.
/// Response: successCount / failedCount + từng record (mã lỗi RDM, mã lỗi SAP dạng ID/NUMBER, nội dung).
///
/// Đồng bộ (tối đa 500 dòng): HTTP 200 tất cả thành công · 400 dữ liệu sai / quá 500 dòng · 409 requestId đang xử lý / Unknown ·
/// 422 lỗi kiểm tra hoặc lỗi SAP (PRICE_VALIDATION = không ghi gì; PRICE_PARTIAL = ghi được một phần) · 502 Unknown · 503 mất kết nối SAP.
///
/// Hàng loạt — khai báo giá đầu năm (tối đa 20.000 dòng): POST /jobs/{operation} → 202 + Location; GET /jobs/{operation}/{requestId}
/// → 202 khi đang chạy (progress), xong thì trả kết quả với HTTP như đồng bộ. Gửi lại cùng requestId + payload khi đang chạy
/// → 202 trạng thái hiện tại; đã xong → kết quả cũ (replayed).
/// </summary>
[ApiController]
[Route("api/sd/prices-bapi")]
[Produces("application/json")]
public sealed class PricesBapiController : ControllerBase
{
    private readonly IPostingService _postings;

    public PricesBapiController(IPostingService postings)
    {
        _postings = postings;
    }

    // ------------------------------------------------------------------ đồng bộ

    /// <summary>Tạo 1 / nhiều bảng giá (OPERATION 009), tối đa 500 dòng. Chặn trùng / chồng hiệu lực với bảng giá cùng khóa và giữa các dòng.</summary>
    [HttpPost]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] PriceBapiCreateRequest request, CancellationToken ct) =>
        TooManyForSync(request.Items.Count, "create") ?? ToResult(await _postings.BapiCreatePricesAsync(request, ct));

    /// <summary>Sửa 1 / nhiều bảng giá theo KNUMH (OPERATION 004): giá, pricing unit, đơn vị, hiệu lực đến. Tối đa 500 dòng.</summary>
    [HttpPost("change")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Change([FromBody] PriceBapiChangeRequest request, CancellationToken ct) =>
        TooManyForSync(request.Items.Count, "change") ?? ToResult(await _postings.BapiChangePricesAsync(request, ct));

    /// <summary>Hủy hiệu lực 1 / nhiều bảng giá (đánh dấu xóa — INDIDELETE). Bản đã hủy được bỏ qua. Tối đa 500 dòng.</summary>
    [HttpPost("invalidate")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Invalidate([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        TooManyForSync(request.ConditionRecordNumbers.Count, "invalidate") ?? ToResult(await _postings.BapiInvalidatePricesAsync(request, ct));

    /// <summary>Duyệt 1 / nhiều bảng giá (release status A → trống). Bản đã duyệt được bỏ qua. Tối đa 500 dòng.</summary>
    [HttpPost("approve")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Approve([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        TooManyForSync(request.ConditionRecordNumbers.Count, "approve") ?? ToResult(await _postings.BapiApprovePricesAsync(request, ct));

    /// <summary>Gỡ duyệt 1 / nhiều bảng giá (release status trống → A). Bản đang chờ duyệt được bỏ qua. Tối đa 500 dòng.</summary>
    [HttpPost("unapprove")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Unapprove([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        TooManyForSync(request.ConditionRecordNumbers.Count, "unapprove") ?? ToResult(await _postings.BapiUnapprovePricesAsync(request, ct));

    // ------------------------------------------------------------------ hàng loạt (xử lý nền)

    /// <summary>Khai báo giá hàng loạt: tạo tối đa 20.000 bảng giá, xử lý nền. Trả 202 + Location để theo dõi.</summary>
    [HttpPost("jobs/create")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> CreateJob([FromBody] PriceBapiCreateRequest request, CancellationToken ct) =>
        Submit(SapOperation.PriceBapiCreate, "create", request, ct);

    /// <summary>Sửa hàng loạt (tối đa 20.000 dòng), xử lý nền.</summary>
    [HttpPost("jobs/change")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> ChangeJob([FromBody] PriceBapiChangeRequest request, CancellationToken ct) =>
        Submit(SapOperation.PriceBapiChange, "change", request, ct);

    /// <summary>Hủy hiệu lực hàng loạt (tối đa 20.000 KNUMH), xử lý nền.</summary>
    [HttpPost("jobs/invalidate")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> InvalidateJob([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        Submit(SapOperation.PriceBapiInvalidate, "invalidate", request, ct);

    /// <summary>Duyệt hàng loạt (tối đa 20.000 KNUMH), xử lý nền.</summary>
    [HttpPost("jobs/approve")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> ApproveJob([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        Submit(SapOperation.PriceBapiApprove, "approve", request, ct);

    /// <summary>Gỡ duyệt hàng loạt (tối đa 20.000 KNUMH), xử lý nền.</summary>
    [HttpPost("jobs/unapprove")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> UnapproveJob([FromBody] PriceBapiRecordsRequest request, CancellationToken ct) =>
        Submit(SapOperation.PriceBapiUnapprove, "unapprove", request, ct);

    /// <summary>
    /// Trạng thái / kết quả job hàng loạt. operation = create | change | invalidate | approve | unapprove.
    /// 202 = đang chờ / đang chạy (xem progress: total, done, succeeded, failed); xong → HTTP như API đồng bộ; 404 = không có requestId.
    /// </summary>
    [HttpGet("jobs/{operation}/{requestId}")]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<PriceBapiResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJob(string operation, string requestId, CancellationToken ct)
    {
        if (ToOperation(operation) is not { } op)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "operation không hợp lệ",
                detail: "operation = create | change | invalidate | approve | unapprove.");
        var posting = await _postings.FindAsync(op, requestId, ct);
        return posting is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Không tìm thấy requestId", detail: $"{operation}/{requestId}")
            : ToResult(posting);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<IActionResult> Submit(SapOperation op, string action, IPostingRequest request, CancellationToken ct)
    {
        var posting = await _postings.SubmitPriceBapiJobAsync(op, request, ct);
        if (posting.Status != PostingStatus.Pending) return ToResult(posting);   // đã xong trước đó (replayed) / không vào được hàng đợi
        var location = Url.Action(nameof(GetJob), new { operation = action, requestId = posting.RequestId });
        return new AcceptedResult(location, PriceBapiResult.From(posting));
    }

    private static SapOperation? ToOperation(string action) => action.ToLowerInvariant() switch
    {
        "create" => SapOperation.PriceBapiCreate,
        "change" => SapOperation.PriceBapiChange,
        "invalidate" => SapOperation.PriceBapiInvalidate,
        "approve" => SapOperation.PriceBapiApprove,
        "unapprove" => SapOperation.PriceBapiUnapprove,
        _ => null
    };

    private IActionResult? TooManyForSync(int count, string action) =>
        count <= PriceBapiLimits.SyncMaxItems
            ? null
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Quá nhiều dòng cho API đồng bộ",
                detail: $"{count} dòng > {PriceBapiLimits.SyncMaxItems}. Dùng POST /api/sd/prices-bapi/jobs/{action} " +
                        $"(xử lý nền, tối đa {PriceBapiLimits.BulkMaxItems} dòng) rồi theo dõi GET /api/sd/prices-bapi/jobs/{action}/{{requestId}}.");

    private static IActionResult ToResult(PostingResponse posting)
    {
        var status = posting is { Status: PostingStatus.Pending, IsBackgroundJob: true }
            ? StatusCodes.Status202Accepted
            : ((ObjectResult)posting.ToActionResult()).StatusCode ?? StatusCodes.Status500InternalServerError;
        return new ObjectResult(PriceBapiResult.From(posting)) { StatusCode = status };
    }
}

/// <summary>Kết quả 1 thao tác bảng giá qua BAPI.</summary>
public sealed record PriceBapiResult(
    string RequestId,
    PostingStatus Status,
    bool Replayed,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PriceBapiRecordResult> Records,
    Guid PostingId,
    // true = job hàng loạt (xử lý nền).
    bool IsBackgroundJob = false,
    // Tiến độ job hàng loạt: total / done / succeeded / failed (done gồm cả record bỏ qua).
    ProgressSnapshot? Progress = null)
{
    private static readonly HashSet<string> SkipCodes = new()
    {
        PriceErrorCodes.AlreadyApproved, PriceErrorCodes.AlreadyPending, PriceErrorCodes.AlreadyDeleted
    };

    public static PriceBapiResult From(PostingResponse p)
    {
        var records = p.Messages
            .Where(m => m.Item is not null)
            .GroupBy(m => m.Item!)
            .Select(g =>
            {
                var final = g.LastOrDefault(m => m.Id == "RDM" && !m.Number.StartsWith("PRICE_PLAN", StringComparison.Ordinal));
                var success = final?.Number == PriceErrorCodes.Ok;
                var skipped = final is not null && SkipCodes.Contains(final.Number);
                var sapError = g.FirstOrDefault(m => m.IsError && m.Id != "RDM");
                var error = success || skipped ? null : g.FirstOrDefault(m => m.IsError && m.Id == "RDM") ?? sapError;
                return new PriceBapiRecordResult(
                    g.Key,
                    final?.MessageV1 ?? g.Select(m => m.MessageV1).FirstOrDefault(v => v is not null),
                    success,
                    skipped,
                    success || skipped ? null : error?.Id == "RDM" ? error.Number : PriceErrorCodes.SapError,
                    sapError is null ? null : $"{sapError.Id}/{sapError.Number}",
                    success || skipped ? final!.Message : sapError?.Message ?? error?.Message,
                    g.Where(m => m.Id != "RDM").ToList(),
                    g.Where(m => m.Id == "RDM" && m.Type == "W").Select(m => m.Message).ToList());
            })
            .OrderBy(r => int.TryParse(r.Item, out var i) ? i : int.MaxValue).ThenBy(r => r.Item, StringComparer.Ordinal)
            .ToList();

        return new PriceBapiResult(p.RequestId, p.Status, p.Replayed,
            records.Count(r => r.Success), records.Count(r => !r.Success && !r.Skipped), records.Count(r => r.Skipped),
            p.ErrorCode, p.ErrorMessage, records, p.Id, p.IsBackgroundJob, p.Progress);
    }
}

/// <summary>Kết quả 1 bảng giá (Item = số dòng khi tạo / sửa, KNUMH khi hủy / duyệt / gỡ duyệt).</summary>
public sealed record PriceBapiRecordResult(
    string Item,
    string? ConditionRecordNumber,
    bool Success,
    bool Skipped,
    // Mã lỗi RDM (PRICE_OVERLAP, PRICE_SAP_ERROR...).
    string? ErrorCode,
    // Mã lỗi SAP dạng message ID/NUMBER (vd. VK/021) khi lỗi do SAP.
    string? SapErrorCode,
    string? Message,
    IReadOnlyList<SapReturnMessage> SapMessages,
    IReadOnlyList<string> Warnings);
