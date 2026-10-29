using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rdm.Api.ApiCatalog;
using Rdm.Application.ApiCatalog;
using Rdm.Application.Auth;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Swashbuckle.AspNetCore.Swagger;

namespace Rdm.Api.Controllers;

/// <summary>
/// Bảng cấu hình active / inactive API (rdm.ApiEndpoints) — chỉ role Admin. API inactive: không hiện trên Swagger và trả 404 như
/// URL không tồn tại (kể cả khi không có token). Áp dụng ngay trên instance này; instance khác sau tối đa ApiEndpoints:RefreshSeconds.
/// API đăng nhập (api/auth) và chính API này luôn active (IsLocked).
/// </summary>
[ApiController]
[Route("api/admin/endpoints")]
[Authorize(Roles = ApiRoles.Admin)]
[AlwaysActive]
[InternalDoc]   // quản trị: không hiện trong tài liệu đối tác
[Produces("application/json")]
public sealed class ApiEndpointsController : ControllerBase
{
    private readonly IApiEndpointRepository _repository;
    private readonly ApiEndpointRegistry _registry;
    private readonly TimeProvider _clock;

    public ApiEndpointsController(IApiEndpointRepository repository, ApiEndpointRegistry registry, TimeProvider clock)
    {
        _repository = repository;
        _registry = registry;
        _clock = clock;
    }

    /// <summary>Danh sách API + trạng thái. Lọc tùy chọn: group (tên controller), active (true / false).</summary>
    [HttpGet]
    [ProducesResponseType<ApiEndpointList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? group, [FromQuery] bool? active, CancellationToken ct)
    {
        var rows = await _repository.GetAllAsync(ct);
        var items = rows
            .Where(r => group is null || r.GroupName.Equals(group, StringComparison.OrdinalIgnoreCase))
            .Where(r => active is null || r.IsActive == active)
            .Select(ToDto)
            .ToList();
        return Ok(new ApiEndpointList(
            rows.Count, rows.Count(r => r.IsActive), rows.Count(r => !r.IsActive),
            rows.Select(r => r.GroupName).Distinct().Order().ToList(), _registry.LoadedAtUtc, items));
    }

    /// <summary>
    /// Bật / tắt API theo danh sách khóa ("METHOD route", lấy ở GET) và / hoặc theo nhóm (tên controller, VD "PricesBapi").
    /// API khóa (đăng nhập, quản trị) được bỏ qua.
    /// </summary>
    [HttpPut("status")]
    [ProducesResponseType<ApiEndpointStatusResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetStatus([FromBody] ApiEndpointStatusRequest request, CancellationToken ct)
    {
        var keys = request.Keys?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList() ?? new List<string>();
        var groups = request.Groups?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList() ?? new List<string>();
        if (keys.Count == 0 && groups.Count == 0)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["keys"] = new[] { "Truyền ít nhất 1 khóa API (keys) hoặc 1 nhóm (groups)." }
            }));

        var rows = await _repository.GetAllAsync(ct);
        var byKey = rows.ToDictionary(r => r.EndpointKey, StringComparer.OrdinalIgnoreCase);
        var knownGroups = rows.Select(r => r.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownKeys = keys.Where(k => !byKey.ContainsKey(k)).ToList();
        var unknownGroups = groups.Where(g => !knownGroups.Contains(g)).ToList();
        var targeted = rows.Where(r => keys.Contains(r.EndpointKey, StringComparer.OrdinalIgnoreCase)
                                       || groups.Contains(r.GroupName, StringComparer.OrdinalIgnoreCase)).ToList();

        var user = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.Identity?.Name ?? "admin";
        var changed = await _repository.SetActiveAsync(keys, groups, request.IsActive, user, request.Note, _clock.GetUtcNow().UtcDateTime, ct);
        _registry.Load(await _repository.GetAllAsync(ct), _clock.GetUtcNow().UtcDateTime);   // áp dụng ngay

        return Ok(new ApiEndpointStatusResult(
            request.IsActive,
            changed,
            targeted.Where(r => r.IsLocked).Select(r => r.EndpointKey).ToList(),
            targeted.Where(r => !r.IsLocked && r.IsActive == request.IsActive).Select(r => r.EndpointKey).ToList(),
            unknownKeys,
            unknownGroups));
    }

    /// <summary>Tổng hợp theo nhóm (controller): số API, đang bật, đang tắt, bị khóa — cho màn hình quản trị.</summary>
    [HttpGet("groups")]
    [ProducesResponseType<IReadOnlyList<ApiEndpointGroup>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Groups(CancellationToken ct)
    {
        var rows = await _repository.GetAllAsync(ct);
        return Ok(rows.GroupBy(r => r.GroupName)
            .Select(g => new ApiEndpointGroup(g.Key, g.Count(), g.Count(r => r.IsActive), g.Count(r => !r.IsActive), g.Count(r => r.IsLocked),
                g.Max(r => r.UpdatedAtUtc)))
            .OrderBy(g => g.Group)
            .ToList());
    }

    /// <summary>Nạp lại bảng cấu hình ngay (khi sửa trực tiếp bằng procedure trên DB).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<ApiEndpointList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        _registry.Load(await _repository.GetAllAsync(ct), _clock.GetUtcNow().UtcDateTime);
        return await List(null, null, ct);
    }

    /// <summary>
    /// Lưu mô tả cho ĐỐI TÁC của 1 API (Swagger công khai /swagger) — không đưa chi tiết kỹ thuật SAP. Trống = xóa (Swagger đối tác
    /// để trống). publicHidden = true: ẩn API khỏi tài liệu đối tác (vẫn gọi được nếu đang bật). Mô tả nội bộ vẫn lấy từ code.
    /// </summary>
    [HttpPut("doc")]
    [ProducesResponseType<ApiEndpointDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDoc([FromBody] ApiEndpointDocRequest request, CancellationToken ct)
    {
        if (request.PublicSummary?.Trim().Length > 200 || request.PublicDescription?.Trim().Length > 2000)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["publicSummary"] = new[] { "Tóm tắt tối đa 200 ký tự, mô tả tối đa 2000 ký tự." }
            }));
        var user = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.Identity?.Name ?? "admin";
        if (!await _repository.SetPublicDocAsync(request.Key.Trim(), request.PublicSummary, request.PublicDescription, request.PublicHidden,
                user, _clock.GetUtcNow().UtcDateTime, ct))
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không có API này", detail: request.Key);

        var rows = await _repository.GetAllAsync(ct);
        _registry.Load(rows, _clock.GetUtcNow().UtcDateTime);   // Swagger đối tác đổi ngay
        return Ok(ToDto(rows.First(r => r.EndpointKey.Equals(request.Key.Trim(), StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Tải tài liệu kỹ thuật (TS) NỘI BỘ dạng Markdown — mọi API, mô tả nội bộ + đối tác, tham số, request / response, kiểu dữ liệu.</summary>
    [HttpGet("export/ts")]
    [Produces("text/markdown")]
    public async Task<IActionResult> ExportTs([FromServices] IApiDescriptionGroupCollectionProvider explorer, CancellationToken ct)
    {
        var md = ApiTsDocument.Build(await _repository.GetAllAsync(ct), explorer, AppVersion);
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(md)).ToArray(),
            "text/markdown; charset=utf-8", $"RDM-API-TS-{DateTime.Now:yyyyMMdd-HHmm}.md");
    }

    /// <summary>Tải OpenAPI NỘI BỘ (đầy đủ XML doc, mọi API) — dùng cho Postman / sinh client / tài liệu nội bộ.</summary>
    [HttpGet("export/openapi-internal")]
    [Produces("application/json")]
    public IActionResult ExportOpenApi([FromServices] ISwaggerProvider swagger)
    {
        var doc = swagger.GetSwagger(ApiDocs.Internal);
        var json = doc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"RDM-API-openapi-internal-{DateTime.Now:yyyyMMdd-HHmm}.json");
    }

    private static string AppVersion => typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";

    private ApiEndpointDto ToDto(ApiEndpointSetting r) => new(
        r.EndpointKey, r.HttpMethod, "/" + r.RouteTemplate, r.GroupName, r.ActionName, r.Description, r.IsActive, r.IsLocked,
        r.LastSeenAtUtc >= _registry.StartedAtUtc.AddMinutes(-1), r.Note, r.UpdatedBy, r.UpdatedAtUtc,
        r.PublicSummary, r.PublicDescription, r.PublicHidden || _registry.IsInternalDoc(r.EndpointKey), _registry.IsInternalDoc(r.EndpointKey),
        r.DocUpdatedBy, r.DocUpdatedAtUtc);
}

public sealed class ApiEndpointDocRequest
{
    /// <summary>Khóa API "METHOD route".</summary>
    /// <example>POST api/sd/prices-bapi/jobs/create</example>
    [System.ComponentModel.DataAnnotations.Required]
    public string Key { get; set; } = string.Empty;

    /// <summary>Tóm tắt cho đối tác (≤ 200 ký tự). Trống = xóa.</summary>
    public string? PublicSummary { get; set; }

    /// <summary>Mô tả chi tiết cho đối tác (markdown, ≤ 2000 ký tự). Trống = xóa.</summary>
    public string? PublicDescription { get; set; }

    /// <summary>true = ẩn khỏi tài liệu đối tác.</summary>
    public bool PublicHidden { get; set; }
}

public sealed class ApiEndpointStatusRequest
{
    /// <summary>Khóa API dạng "METHOD route" (VD "POST api/sd/prices-bapi/jobs/create").</summary>
    /// <example>["POST api/sd/prices-bapi/jobs/create"]</example>
    public List<string>? Keys { get; set; }

    /// <summary>Nhóm = tên controller (VD "PricesBapi", "Prices", "Fi", "MasterData").</summary>
    /// <example>[]</example>
    public List<string>? Groups { get; set; }

    /// <summary>true = bật (active), false = tắt (inactive: ẩn khỏi Swagger, trả 404).</summary>
    public bool IsActive { get; set; }

    /// <summary>Ghi chú lý do (lưu vào bảng).</summary>
    public string? Note { get; set; }
}

public sealed record ApiEndpointDto(
    // Description = mô tả NỘI BỘ (kỹ thuật, từ code)
    string Key, string HttpMethod, string Route, string Group, string Action, string? Description, bool IsActive, bool IsLocked,
    // false = API không còn trong code (giữ lại để tra cứu)
    bool InCode, string? Note, string? UpdatedBy, DateTime? UpdatedAtUtc,
    // Mô tả ĐỐI TÁC (Swagger /swagger); PublicHidden = không hiện trong tài liệu đối tác; InternalOnly = luôn ẩn (API quản trị, theo code)
    string? PublicSummary, string? PublicDescription, bool PublicHidden, bool InternalOnly, string? DocUpdatedBy, DateTime? DocUpdatedAtUtc);

public sealed record ApiEndpointList(
    int Total, int ActiveCount, int InactiveCount, IReadOnlyList<string> Groups, DateTime? CacheLoadedAtUtc, IReadOnlyList<ApiEndpointDto> Items);

public sealed record ApiEndpointStatusResult(
    bool IsActive, IReadOnlyList<string> Changed, IReadOnlyList<string> SkippedLocked, IReadOnlyList<string> AlreadyInState,
    IReadOnlyList<string> UnknownKeys, IReadOnlyList<string> UnknownGroups);

public sealed record ApiEndpointGroup(string Group, int Total, int ActiveCount, int InactiveCount, int LockedCount, DateTime? LastUpdatedAtUtc);
