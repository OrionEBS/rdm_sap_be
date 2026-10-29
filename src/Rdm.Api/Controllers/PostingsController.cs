using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;

namespace Rdm.Api.Controllers;

/// <summary>Tra cứu nhật ký các lần ghi SAP.</summary>
[ApiController]
[Route("api/postings")]
[Produces("application/json")]
public sealed class PostingsController : ControllerBase
{
    private readonly ISapPostingRepository _repository;

    public PostingsController(ISapPostingRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Tìm kiếm theo nghiệp vụ / trạng thái / số chứng từ / khoảng thời gian tạo (UTC).</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<PostingResponse>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<PostingResponse>> Search([FromQuery] PostingQuery query, CancellationToken ct)
    {
        var result = await _repository.SearchAsync(query, ct);
        return new PagedResult<PostingResponse>(
            result.Items.Select(p => PostingResponse.From(p)).ToList(), result.Page, result.PageSize, result.TotalCount);
    }

    /// <summary>Lấy theo Id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PostingResponse>> Get(Guid id, CancellationToken ct) =>
        await _repository.GetAsync(id, ct) is { } p ? PostingResponse.From(p) : NotFound();

    /// <summary>Lấy theo nghiệp vụ + requestId của client.</summary>
    [HttpGet("{operation}/{requestId}")]
    [ProducesResponseType<PostingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PostingResponse>> GetByRequestId(SapOperation operation, string requestId, CancellationToken ct) =>
        await _repository.FindAsync(operation, requestId.Trim(), ct) is { } p ? PostingResponse.From(p) : NotFound();
}
