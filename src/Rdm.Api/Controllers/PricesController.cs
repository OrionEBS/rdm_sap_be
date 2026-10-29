using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Api.Controllers;

/// <summary>
/// Bảng giá ZVLP / ZVDI / ZMIN / ZVIV — Z_ESD015_PRICE_UPLOAD (VK11 / VK12 bằng BDC, như report ZSD_VN_PRICE_UPLD).
/// Bảng giá tạo / sửa qua API có hiệu lực ngay (duyệt đã làm trên RDM).
/// Kiểm tra theo lô: 1 dòng lỗi → không ghi dòng nào (422, ErrorCode PRICE_VALIDATION, mã lỗi từng dòng ở messages[].number,
/// số dòng ở messages[].item). SAP ghi từng dòng: lỗi SAP ở vài dòng → 422 PRICE_PARTIAL. Mã lỗi: xem PriceErrorCodes.
/// </summary>
[ApiController]
[Route("api/sd/prices")]
[Produces("application/json")]
public sealed class PricesController : ControllerBase
{
    private readonly IPostingService _postings;
    private readonly ISapClient _sap;

    public PricesController(IPostingService postings, ISapClient sap)
    {
        _postings = postings;
        _sap = sap;
    }

    /// <summary>
    /// Đọc bảng giá trên SAP (mỗi dòng = 1 kỳ hiệu lực, kèm giá KONP).
    /// Bắt buộc: conditionType, salesOrganization, validFrom, validTo (lấy các kỳ giao với khoảng này); các bộ lọc khác tùy chọn.
    /// ZVLP / ZVDI → A990 (sold-to, ship-to, material, shipping cond., plant); ZVIV → A991 (sales org đơn hàng, plant, material);
    /// ZMIN → A912 (incoterms) + A919 (region).
    /// Mỗi bảng có cấu trúc cột khóa khác nhau: <c>tables</c> mô tả cột của từng bảng trong kết quả; mỗi dòng <c>items</c>
    /// chỉ có cột khóa của bảng mình (<c>conditionTable</c>) + cột chi tiết chung (trạng thái, hiệu lực, giá KONP).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PriceListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get([FromQuery] PriceQuery query, CancellationToken ct)
    {
        try
        {
            return Ok(await _sap.GetPricesAsync(query, ct));
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Kiểm tra lô bảng giá (khóa, hiệu lực, dấu giá, trùng / chồng hiệu lực) — không ghi SAP, không lưu nhật ký.</summary>
    [HttpPost("check")]
    [ProducesResponseType<PriceCheckResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<PriceCheckResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PriceCheckResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Check([FromBody] PriceCreateRequest request, CancellationToken ct)
    {
        var r = await _sap.CheckPricesAsync(request, ct);
        var body = new PriceCheckResponse(r.Status == PostingStatus.Succeeded, r.ErrorCode, r.ErrorMessage, r.Messages);
        var status = body.Ok ? StatusCodes.Status200OK
            : r.ErrorCode is SapErrorCodes.Unavailable or SapErrorCodes.Logon ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status422UnprocessableEntity;
        return new ObjectResult(body) { StatusCode = status };
    }

    /// <summary>Tạo 1 hoặc nhiều bảng giá (VK11, có hiệu lực ngay). messages[].messageV1 = KNUMH mới của từng dòng.</summary>
    [HttpPost]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] PriceCreateRequest request, CancellationToken ct) =>
        (await _postings.CreatePricesAsync(request, ct)).ToActionResult();

    /// <summary>Sửa 1 hoặc nhiều bảng giá theo KNUMH (VK12): giá, pricing unit, đơn vị, hiệu lực đến.</summary>
    [HttpPost("change")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Change([FromBody] PriceChangeRequest request, CancellationToken ct) =>
        (await _postings.ChangePricesAsync(request, ct)).ToActionResult();

    /// <summary>
    /// Duyệt 1 hoặc nhiều bảng giá chờ duyệt KFRST = A (nhập tay trên SAP) — VK12 processing status 02, như ZSD_VN_PRICE_APR.
    /// Bản đã duyệt được bỏ qua.
    /// </summary>
    [HttpPost("approve")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Approve([FromBody] PriceApproveRequest request, CancellationToken ct) =>
        (await _postings.ApprovePricesAsync(request, ct)).ToActionResult();

    /// <summary>
    /// Tạo bảng giá mới từ bảng giá cũ theo VAT% (vd. đổi 8% → 10%: oldVatPercent = 8, vatPercent = 10).
    /// messages[].messageV1 = KNUMH mới, messageV2 = KNUMH nguồn, messageV3 / V4 = giá cũ / mới.
    /// </summary>
    [HttpPost("copy-with-vat")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CopyWithVat([FromBody] PriceCopyWithVatRequest request, CancellationToken ct) =>
        (await _postings.CopyPricesWithVatAsync(request, ct)).ToActionResult();
}

/// <summary>Kết quả kiểm tra lô bảng giá: Ok = không có dòng lỗi (có thể có cảnh báo PRICE_WILL_ADJUST_EXISTING).</summary>
public sealed record PriceCheckResponse(bool Ok, string? ErrorCode, string? ErrorMessage, IReadOnlyList<SapReturnMessage> Messages);
