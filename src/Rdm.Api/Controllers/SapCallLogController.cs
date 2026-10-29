using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rdm.Api.ApiCatalog;
using Rdm.Application.Abstractions;
using Rdm.Application.Auth;
using Rdm.Infrastructure.SapCallLogging;

namespace Rdm.Api.Controllers;

/// <summary>
/// Nhật ký gọi SAP (rdm.SapCallLog) — bằng chứng từng lời gọi function module: tham số gửi, kết quả nhận (JSON), thời gian,
/// trạng thái, X-Correlation-Id / requestId của EU, user, IP. Chỉ role Admin, chỉ tài liệu nội bộ.
/// </summary>
[ApiController]
[Route("api/admin/sap-calls")]
[Authorize(Roles = ApiRoles.Admin)]
[AlwaysActive]
[InternalDoc]
[Produces("application/json")]
public sealed class SapCallLogController : ControllerBase
{
    private readonly ISapCallLogStore _store;
    private readonly SapCallLogWriter _writer;

    public SapCallLogController(ISapCallLogStore store, SapCallLogWriter writer)
    {
        _store = store;
        _writer = writer;
    }

    /// <summary>Tìm lời gọi SAP (mới nhất trước, không kèm JSON). Thời gian theo UTC; mặc định 24 giờ gần nhất.</summary>
    /// <param name="from">Từ (UTC, ISO 8601).</param>
    /// <param name="to">Đến (UTC, không gồm).</param>
    /// <param name="function">Tên function module, VD Z_ESD016_PRICES_CONDITIONS.</param>
    /// <param name="status">OK · WARNING · ERROR · EXCEPTION.</param>
    /// <param name="correlationId">X-Correlation-Id của EU.</param>
    /// <param name="requestId">requestId (khóa idempotency).</param>
    /// <param name="operation">Thao tác, VD PriceBapiCreate, MasterDataSync.SoldTo.</param>
    /// <param name="user">User gọi API.</param>
    /// <param name="postingId">Id bản ghi rdm.SapPostings.</param>
    /// <param name="q">Tìm trong message / path / mã lỗi.</param>
    /// <param name="page">Trang (từ 1).</param>
    /// <param name="pageSize">Số dòng / trang (tối đa 500).</param>
    /// <param name="ct"></param>
    [HttpGet]
    [ProducesResponseType<SapCallLogPage>(StatusCodes.Status200OK)]
    public Task<SapCallLogPage> Search(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? function, [FromQuery] string? status,
        [FromQuery] string? correlationId, [FromQuery] string? requestId, [FromQuery] string? operation, [FromQuery] string? user,
        [FromQuery] Guid? postingId, [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Tìm theo khóa đối soát (correlationId / requestId / postingId) không giới hạn thời gian mặc định.
        var byKey = !string.IsNullOrWhiteSpace(correlationId) || !string.IsNullOrWhiteSpace(requestId) || postingId is not null;
        return _store.SearchAsync(new SapCallLogFilter
        {
            FromUtc = Utc(from) ?? (byKey ? null : DateTime.UtcNow.AddDays(-1)),
            ToUtc = Utc(to),
            FunctionName = function, Status = status, CorrelationId = correlationId, RequestId = requestId, Operation = operation,
            UserName = user, PostingId = postingId, Text = q, Page = page, PageSize = pageSize
        }, ct);
    }

    /// <summary>Chi tiết 1 lời gọi: thông tin + tham số gửi (request) + kết quả nhận (response) dạng JSON.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<SapCallLogDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SapCallLogDetailResponse>> Get(long id, CancellationToken ct)
    {
        var d = await _store.GetAsync(id, ct);
        if (d is null) return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không tìm thấy", detail: $"Không có lời gọi SAP Id {id}.");
        return new SapCallLogDetailResponse(d.Call, Parse(d.RequestJson), Parse(d.ResponseJson));
    }

    /// <summary>Thống kê theo function module + trạng thái (số lời gọi, thời gian trung bình / lớn nhất). Mặc định 24 giờ gần nhất.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<IReadOnlyList<SapCallLogSummaryRow>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<SapCallLogSummaryRow>> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        _store.SummaryAsync(Utc(from) ?? DateTime.UtcNow.AddDays(-1), Utc(to) ?? DateTime.UtcNow.AddMinutes(1), ct);

    /// <summary>Xuất CSV (UTF-8, không kèm JSON) theo cùng bộ lọc tìm kiếm — tối đa 100.000 dòng.</summary>
    [HttpGet("export")]
    [Produces("text/csv")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? function, [FromQuery] string? status,
        [FromQuery] string? correlationId, [FromQuery] string? requestId, [FromQuery] string? operation, [FromQuery] string? user,
        CancellationToken ct)
    {
        var filter = new SapCallLogFilter
        {
            FromUtc = Utc(from) ?? DateTime.UtcNow.AddDays(-1), ToUtc = Utc(to), FunctionName = function, Status = status,
            CorrelationId = correlationId, RequestId = requestId, Operation = operation, UserName = user, PageSize = 500
        };
        var sb = new StringBuilder();
        sb.AppendLine("Id,CallId,StartedAtUtc,DurationMs,FunctionName,Status,ErrorCode,Message,CorrelationId,RequestId,Operation,PostingId," +
                      "Source,UserName,ClientIp,HttpMethod,Path,TraceId,JobId,Machine,SapSystem,RequestBytes,ResponseBytes,Truncated");
        for (var page = 1; page <= 200; page++)
        {
            var result = await _store.SearchAsync(filter with { Page = page }, ct);
            foreach (var r in result.Items)
                sb.AppendLine(string.Join(",", new object?[]
                {
                    r.Id, r.CallId, r.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), r.DurationMs, r.FunctionName,
                    r.Status, r.ErrorCode, r.Message, r.CorrelationId, r.RequestId, r.Operation, r.PostingId, r.Source, r.UserName, r.ClientIp,
                    r.HttpMethod, r.Path, r.TraceId, r.JobId, r.Machine, r.SapSystem, r.RequestBytes, r.ResponseBytes, r.Truncated
                }.Select(Csv)));
            if (page * result.PageSize >= result.Total) break;
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"sap-calls-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    /// <summary>Trạng thái bộ ghi nền của tiến trình này: đã nhận / đã ghi / bỏ / lỗi, hàng đợi, file dự phòng chờ nạp.</summary>
    [HttpGet("writer")]
    [ProducesResponseType<SapCallLogWriterStats>(StatusCodes.Status200OK)]
    public SapCallLogWriterStats Writer() => _writer.Stats();

    internal static DateTime? Utc(DateTime? d) => d is null ? null : d.Value.Kind switch
    {
        DateTimeKind.Utc => d.Value,
        DateTimeKind.Local => d.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)
    };

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { raw = json }); }
    }

    private static string Csv(object? v)
    {
        var s = v switch { null => "", bool b => b ? "1" : "0", _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
        // Chặn công thức khi mở bằng Excel (=, +, -, @).
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

public sealed record SapCallLogDetailResponse(SapCallLogRow Call, JsonElement Request, JsonElement Response);
