using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Api.Controllers;

/// <summary>Vật tư (MM).</summary>
[ApiController]
[Route("api/mm")]
[Produces("application/json")]
public sealed class MmController : ControllerBase
{
    private readonly IPostingService _postings;

    public MmController(IPostingService postings)
    {
        _postings = postings;
    }

    /// <summary>
    /// Đổi tax classification 1 (MLAN-TAXM1) cho danh sách vật tư qua BAPI_MATERIAL_SAVEDATA (thay MM17).
    /// Kết quả từng vật tư trong <c>messages</c> (<c>item</c> = mã vật tư, <c>messageV1</c> = giá trị cũ, <c>messageV2</c> = giá trị mới,
    /// <c>number</c> = CHANGED / UNCHANGED / NOT_FOUND / FAILED / NOT_PROCESSED). Có vật tư lỗi → 422 (các vật tư khác vẫn đã cập nhật);
    /// gửi lại cùng requestId an toàn.
    /// </summary>
    [HttpPost("material-tax-classification")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateTaxClassification([FromBody] MaterialTaxClassRequest request, CancellationToken ct) =>
        (await _postings.UpdateMaterialTaxClassAsync(request, ct)).ToActionResult();
}
