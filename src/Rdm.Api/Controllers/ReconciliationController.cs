using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Rdm.Application.Abstractions;
using Rdm.Application.Auth;

namespace Rdm.Api.Controllers;

/// <summary>
/// Đối soát cho hệ thống gọi (EU): các lời gọi SAP phát sinh từ request của chính tài khoản đang đăng nhập — thời điểm,
/// X-Correlation-Id, requestId, thao tác, kết quả, số chứng từ. Không trả tên hàm / dữ liệu kỹ thuật SAP.
/// Admin xem được mọi tài khoản.
/// </summary>
[ApiController]
[Route("api/reconciliation")]
[Produces("application/json")]
public sealed class ReconciliationController : ControllerBase
{
    private readonly ISapCallLogStore _store;

    public ReconciliationController(ISapCallLogStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Danh sách lời gọi SAP của tài khoản hiện tại trong khoảng thời gian (UTC, tối đa 31 ngày; mặc định 24 giờ gần nhất),
    /// lọc thêm theo requestId hoặc X-Correlation-Id. Mỗi response API có header X-Correlation-Id để đối chiếu.
    /// </summary>
    /// <param name="from">Từ (UTC).</param>
    /// <param name="to">Đến (UTC, không gồm).</param>
    /// <param name="requestId">requestId đã gửi.</param>
    /// <param name="correlationId">X-Correlation-Id đã gửi / nhận.</param>
    /// <param name="max">Số dòng tối đa (mặc định 1000, tối đa 10000).</param>
    /// <param name="ct"></param>
    [HttpGet("sap-calls")]
    [ProducesResponseType<ReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReconciliationResponse>> SapCalls(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery, StringLength(64)] string? requestId,
        [FromQuery, StringLength(100)] string? correlationId, [FromQuery, Range(1, 10_000)] int max = 1000, CancellationToken ct = default)
    {
        var toUtc = SapCallLogController.Utc(to) ?? DateTime.UtcNow.AddMinutes(1);
        var fromUtc = SapCallLogController.Utc(from) ?? toUtc.AddDays(-1);
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(31))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["from"] = new[] { "Khoảng thời gian phải > 0 và tối đa 31 ngày." }
            }));

        var user = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.Identity?.Name;
        var scopeUser = User.IsInRole(ApiRoles.Admin) ? null : user ?? "?";
        var rows = await _store.ReconcileAsync(fromUtc, toUtc, scopeUser, requestId, correlationId, max, ct);
        return new ReconciliationResponse(fromUtc, toUtc, scopeUser, rows.Count, rows);
    }
}

public sealed record ReconciliationResponse(DateTime FromUtc, DateTime ToUtc, string? UserName, int Count, IReadOnlyList<SapCallReconcileRow> Items);
