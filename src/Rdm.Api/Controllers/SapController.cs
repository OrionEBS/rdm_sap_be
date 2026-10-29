using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;

namespace Rdm.Api.Controllers;

/// <summary>Kiểm tra kết nối SAP.</summary>
[ApiController]
[Route("api/sap")]
[Produces("application/json")]
public sealed class SapController : ControllerBase
{
    private readonly ISapClient _sap;

    public SapController(ISapClient sap)
    {
        _sap = sap;
    }

    /// <summary>Gọi RFC_SYSTEM_INFO — trả SID, client, host, release.</summary>
    [HttpGet("ping")]
    [ProducesResponseType<SapSystemInfo>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ping(CancellationToken ct)
    {
        try
        {
            return Ok(await _sap.PingAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Problem(title: "Không kết nối được SAP", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
