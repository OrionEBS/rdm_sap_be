using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Rdm.Api.ApiCatalog;
using Rdm.Application.ApiCatalog;
using Rdm.Application.Auth;

namespace Rdm.Api.Controllers;

/// <summary>
/// Cấu hình cột output theo API — chỉ role Admin, áp dụng ngay (instance khác sau tối đa ApiEndpoints:RefreshSeconds):
///   - Ẩn / hiện, đổi tên, sắp xếp field trong JSON trả về của 1 API (cấu hình gốc). DELETE cấu hình gốc = reset như ban đầu.
///   - Sao chép thành API mới: route /api/c/{copyName}/{tham số route API gốc}, cùng method / request / quyền, output theo cấu hình riêng.
/// Swagger (đối tác + nội bộ) hiển thị response theo cấu hình và liệt kê API sao chép (tag "API sao chép").
/// </summary>
[ApiController]
[Route("api/admin/endpoints/output")]
[Authorize(Roles = ApiRoles.Admin)]
[AlwaysActive]
[InternalDoc]
[Produces("application/json")]
public sealed class ApiOutputController : ControllerBase
{
    private readonly IApiOutputProfileRepository _repository;
    private readonly IApiEndpointRepository _endpoints;
    private readonly ApiOutputRegistry _registry;
    private readonly IApiDescriptionGroupCollectionProvider _explorer;
    private readonly JsonOptions _json;
    private readonly TimeProvider _clock;

    public ApiOutputController(IApiOutputProfileRepository repository, IApiEndpointRepository endpoints, ApiOutputRegistry registry,
        IApiDescriptionGroupCollectionProvider explorer, IOptions<JsonOptions> json, TimeProvider clock)
    {
        _repository = repository;
        _endpoints = endpoints;
        _registry = registry;
        _explorer = explorer;
        _json = json.Value;
        _clock = clock;
    }

    /// <summary>Danh sách field output của 1 API (dò từ kiểu response) + cấu hình gốc + các API sao chép từ API này.
    /// key = khóa API "METHOD route" (VD "GET api/master-data/sold-to").</summary>
    [HttpGet]
    [ProducesResponseType<ApiOutputDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromQuery] string key, CancellationToken ct)
    {
        var api = ApiOutputRegistry.FindApi(_explorer, key.Trim());
        if (api is null) return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không có API này trong code", detail: key);
        var type = ApiOutputRegistry.ResponseType(api);
        var fields = type is null ? Array.Empty<ApiOutputFieldInfo>() : ApiOutputRegistry.DiscoverFields(type, _json.JsonSerializerOptions);
        var endpointKey = ApiEndpointRegistry.Key(api.HttpMethod!, api.ActionDescriptor.AttributeRouteInfo!.Template!);
        var profiles = (await _repository.GetAllAsync(ct)).Where(p => p.EndpointKey.Equals(endpointKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var active = await SourceActiveAsync(ct);
        return Ok(new ApiOutputDetail(
            endpointKey, type is null ? null : FriendlyName(type), ApiOutputRegistry.RouteParameters(api.ActionDescriptor.AttributeRouteInfo.Template!) is not null,
            fields, profiles.Where(p => p.CopyName is null).Select(p => ToDto(p, active)).FirstOrDefault(),
            profiles.Where(p => p.CopyName is not null).Select(p => ToDto(p, active)).ToList()));
    }

    /// <summary>Mọi cấu hình output + API sao chép (cho màn hình tổng hợp).</summary>
    [HttpGet("profiles")]
    [ProducesResponseType<IReadOnlyList<ApiOutputProfileDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var active = await SourceActiveAsync(ct);
        return Ok((await _repository.GetAllAsync(ct)).Select(p => ToDto(p, active)).ToList());
    }

    /// <summary>
    /// Lưu cấu hình. profileId trống + copyName trống = cấu hình gốc của API (đã có thì ghi đè).
    /// profileId trống + copyName = tạo API sao chép mới (copyName: chữ thường / số / '-', 2–60 ký tự; trùng → 409).
    /// fields: chỉ cần field có thay đổi (visible = false, alias, order); path phải có trong GET.
    /// </summary>
    [HttpPut("profiles")]
    [ProducesResponseType<ApiOutputProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Save([FromBody] ApiOutputSaveRequest request, CancellationToken ct)
    {
        var all = await _repository.GetAllAsync(ct);
        var existing = request.ProfileId is { } id ? all.FirstOrDefault(p => p.ProfileId == id) : null;
        if (request.ProfileId is not null && existing is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không có cấu hình này", detail: request.ProfileId.ToString());

        var api = ApiOutputRegistry.FindApi(_explorer, (existing?.EndpointKey ?? request.EndpointKey).Trim());
        if (api is null) return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không có API này trong code", detail: request.EndpointKey);
        var template = api.ActionDescriptor.AttributeRouteInfo!.Template!;
        var endpointKey = ApiEndpointRegistry.Key(api.HttpMethod!, template);
        var copyName = existing is { CopyName: null } ? null : request.CopyName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(copyName)) copyName = null;

        var errors = new Dictionary<string, string[]>();
        if (copyName is not null && !ApiOutputValidation.CopyNamePattern().IsMatch(copyName))
            errors["copyName"] = new[] { "Tên API sao chép: chữ thường, số, dấu '-', 2–60 ký tự, bắt đầu bằng chữ / số (VD 'soldto-ngan-gon')." };
        if (copyName is not null && ApiOutputRegistry.RouteParameters(template) is null)
            errors["copyName"] = new[] { "Route của API này không sao chép được (segment ghép tham số / catch-all)." };
        if (request.Title?.Trim().Length > 200) errors["title"] = new[] { "Tiêu đề tối đa 200 ký tự." };

        var fields = (request.Fields ?? new List<ApiOutputField>())
            .Select(f => f with { Path = f.Path?.Trim() ?? string.Empty, Alias = string.IsNullOrWhiteSpace(f.Alias) ? null : f.Alias.Trim() })
            .Where(f => !f.Visible || f.Alias is not null || f.Order is not null)   // chỉ lưu field có thay đổi
            .ToList();
        var type = ApiOutputRegistry.ResponseType(api);
        var known = type is null ? Array.Empty<string>()
            : ApiOutputRegistry.DiscoverFields(type, _json.JsonSerializerOptions).Select(f => f.Path).ToArray();
        foreach (var (field, messages) in ApiOutputValidation.Validate(fields, known)) errors[$"fields[{field}]"] = messages;
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));

        int savedId;
        try
        {
            savedId = await _repository.SaveAsync(existing?.ProfileId, endpointKey, copyName, request.Title, request.IsActive, fields, CurrentUser,
                _clock.GetUtcNow().UtcDateTime, ct);
        }
        catch (ApiOutputConflictException ex)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Trùng tên API sao chép", detail: ex.Message);
        }

        var profiles = await ReloadAsync(ct);
        return Ok(ToDto(profiles.First(p => p.ProfileId == savedId), await SourceActiveAsync(ct)));
    }

    /// <summary>Xóa cấu hình: cấu hình gốc → API trả output như ban đầu (reset); API sao chép → xóa API đó (gọi lại trả 404).</summary>
    [HttpDelete("profiles/{profileId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int profileId, CancellationToken ct)
    {
        if (!await _repository.DeleteAsync(profileId, ct))
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Không có cấu hình này", detail: profileId.ToString());
        await ReloadAsync(ct);
        return NoContent();
    }

    private async Task<IReadOnlyList<ApiOutputProfile>> ReloadAsync(CancellationToken ct)
    {
        var profiles = await _repository.GetAllAsync(ct);
        _registry.Load(profiles);   // áp dụng ngay trên instance này
        return profiles;
    }

    private async Task<Dictionary<string, bool>> SourceActiveAsync(CancellationToken ct) =>
        (await _endpoints.GetAllAsync(ct)).ToDictionary(e => e.EndpointKey, e => e.IsActive, StringComparer.OrdinalIgnoreCase);

    private string CurrentUser => User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.Identity?.Name ?? "admin";

    private static ApiOutputProfileDto ToDto(ApiOutputProfile p, IReadOnlyDictionary<string, bool> sourceActive)
    {
        var (method, template) = ApiOutputRegistry.SplitKey(p.EndpointKey);
        var route = p.CopyName is null ? template : ApiOutputRegistry.CopyRoute(p.CopyName, template);
        return new ApiOutputProfileDto(p.ProfileId, p.EndpointKey, method, "/" + template, p.CopyName, "/" + route, p.Title, p.IsActive,
            sourceActive.GetValueOrDefault(p.EndpointKey, true), p.GetFields(), p.UpdatedBy, p.UpdatedAtUtc, p.CreatedAtUtc);
    }

    private static string FriendlyName(Type t) => t.IsGenericType
        ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(FriendlyName))}>"
        : t.Name;
}

public sealed class ApiOutputSaveRequest
{
    /// <summary>Trống = tạo mới (hoặc ghi đè cấu hình gốc nếu copyName trống).</summary>
    public int? ProfileId { get; set; }

    /// <summary>Khóa API gốc "METHOD route".</summary>
    /// <example>GET api/master-data/sold-to</example>
    public string EndpointKey { get; set; } = string.Empty;

    /// <summary>Tên API sao chép (route /api/c/{copyName}/...). Trống = cấu hình của chính API gốc.</summary>
    /// <example>soldto-ngan-gon</example>
    public string? CopyName { get; set; }

    /// <summary>Tóm tắt hiển thị trên Swagger cho API sao chép.</summary>
    public string? Title { get; set; }

    /// <summary>Chỉ dùng cho API sao chép: false = tắt (404).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Field thay đổi: path (theo GET), visible, alias (tên mới), order.</summary>
    public List<ApiOutputField>? Fields { get; set; }
}

public sealed record ApiOutputProfileDto(
    int ProfileId, string EndpointKey, string HttpMethod, string SourceRoute, string? CopyName, string Route, string? Title, bool IsActive,
    bool SourceIsActive, IReadOnlyList<ApiOutputField> Fields, string? UpdatedBy, DateTime? UpdatedAtUtc, DateTime CreatedAtUtc);

public sealed record ApiOutputDetail(
    string EndpointKey, string? ResponseType, bool CanCopy, IReadOnlyList<ApiOutputFieldInfo> Fields, ApiOutputProfileDto? Original,
    IReadOnlyList<ApiOutputProfileDto> Copies);
