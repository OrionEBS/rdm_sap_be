using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Api.Controllers;

/// <summary>Chứng từ kế toán (FI).</summary>
[ApiController]
[Route("api/fi")]
[Produces("application/json")]
public sealed class FiController : ControllerBase
{
    private readonly IPostingService _postings;
    private readonly ISapClient _sap;

    public FiController(IPostingService postings, ISapClient sap)
    {
        _postings = postings;
        _sap = sap;
    }

    /// <summary>Tạo chứng từ FB01 (GL + khách hàng) — Z_EFI011_CREATE_FB01.</summary>
    [HttpPost("fb01")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateFb01([FromBody] Fb01Request request, CancellationToken ct) =>
        (await _postings.CreateFb01Async(request, ct)).ToActionResult();

    /// <summary>Tạo Customer credit memo FB75 — Z_EFI009_CREATE_FB75.</summary>
    [HttpPost("fb75")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateFb75([FromBody] Fb75Request request, CancellationToken ct) =>
        (await _postings.CreateFb75Async(request, ct)).ToActionResult();

    /// <summary>Đảo chứng từ kế toán — BAPI_ACC_DOCUMENT_REV_POST + BAPI_TRANSACTION_COMMIT.</summary>
    [HttpPost("reversal")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Reverse([FromBody] AccDocumentReversalRequest request, CancellationToken ct) =>
        (await _postings.ReverseAccDocumentAsync(request, ct)).ToActionResult();

    /// <summary>
    /// Hạch toán chứng từ FI nhiều dòng (G/L, khách hàng, thuế) — BAPI_ACC_DOCUMENT_POST + BAPI_TRANSACTION_COMMIT.
    /// Dùng cho cấn trừ chiết khấu, chiết khấu thanh toán, lãi quá hạn, trích trước. Đảo bằng /api/fi/reversal.
    /// </summary>
    [HttpPost("documents")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostDocument([FromBody] AccDocumentPostRequest request, CancellationToken ct) =>
        (await _postings.PostAccDocumentAsync(request, ct)).ToActionResult();

    /// <summary>Kiểm tra chứng từ (BAPI_ACC_DOCUMENT_CHECK) — không ghi gì, không lưu nhật ký; requestId không bị giữ chỗ.</summary>
    [HttpPost("documents/check")]
    [ProducesResponseType<AccDocumentCheckResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AccDocumentCheckResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<AccDocumentCheckResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckDocument([FromBody] AccDocumentPostRequest request, CancellationToken ct)
    {
        var r = await _sap.CheckAccDocumentAsync(request, ct);
        var body = new AccDocumentCheckResponse(r.Status == PostingStatus.Succeeded, r.ErrorCode, r.ErrorMessage, r.Messages);
        var status = body.Ok ? StatusCodes.Status200OK
            : r.ErrorCode is SapErrorCodes.Unavailable or SapErrorCodes.Logon ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status422UnprocessableEntity;
        return new ObjectResult(body) { StatusCode = status };
    }
}

/// <summary>Kết quả kiểm tra chứng từ: Ok = SAP không báo lỗi E/A.</summary>
public sealed record AccDocumentCheckResponse(bool Ok, string? ErrorCode, string? ErrorMessage, IReadOnlyList<SapReturnMessage> Messages);
