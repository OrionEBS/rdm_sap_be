using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Api.Controllers;

/// <summary>Bán hàng (SD).</summary>
[ApiController]
[Route("api/sd")]
[Produces("application/json")]
public sealed class SdController : ControllerBase
{
    private readonly IPostingService _postings;

    public SdController(IPostingService postings)
    {
        _postings = postings;
    }

    /// <summary>Tạo Credit memo request (VA01, loại ZVCR) — Z_ESD014_CREATE_VA01.</summary>
    [HttpPost("va01")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateVa01([FromBody] Va01Request request, CancellationToken ct) =>
        (await _postings.CreateVa01Async(request, ct)).ToActionResult();

    /// <summary>Tạo Free goods contract (VA41, loại ZVCF) — Z_ESD013_CREATE_VA41.</summary>
    [HttpPost("va41")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateVa41([FromBody] Va41Request request, CancellationToken ct) =>
        (await _postings.CreateVa41Async(request, ct)).ToActionResult();

    /// <summary>
    /// Tạo condition record free goods (VBN1, loại ZA00) — Z_ESD012_CREATE_VBN1 (batch input).
    /// Thành công không có số chứng từ; xem message "Condition records for Free goods saved".
    /// </summary>
    [HttpPost("vbn1")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateVbn1([FromBody] Vbn1Request request, CancellationToken ct) =>
        (await _postings.CreateVbn1Async(request, ct)).ToActionResult();

    /// <summary>
    /// Tạo sale contract — BAPI_CONTRACT_CREATEFROMDATA + COMMIT. Mặc định ở trạng thái chờ duyệt
    /// (delivery + billing block duyệt, mặc định 30); duyệt bằng /api/sd/contracts/approve. documentNumber = số hợp đồng.
    /// </summary>
    [HttpPost("contracts")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateContract([FromBody] ContractCreateRequest request, CancellationToken ct) =>
        (await _postings.CreateContractAsync(request, ct)).ToActionResult();

    /// <summary>
    /// Sửa sale contract — BAPI_CUSTOMERCONTRACT_CHANGE + COMMIT: hiệu lực, PO, điều kiện giao/thanh toán; thêm / sửa / từ chối dòng.
    /// Mặc định gắn lại block duyệt (requireReapproval).
    /// </summary>
    [HttpPost("contracts/change")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangeContract([FromBody] ContractChangeRequest request, CancellationToken ct) =>
        (await _postings.ChangeContractAsync(request, ct)).ToActionResult();

    /// <summary>Duyệt sale contract — gỡ delivery + billing block duyệt (BAPI_CUSTOMERCONTRACT_CHANGE + COMMIT).</summary>
    [HttpPost("contracts/approve")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ApproveContract([FromBody] ContractApproveRequest request, CancellationToken ct) =>
        (await _postings.ApproveContractAsync(request, ct)).ToActionResult();
}
