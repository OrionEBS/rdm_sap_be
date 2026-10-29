using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing.Template;
using Rdm.Application.ApiCatalog;

namespace Rdm.Api.ApiCatalog;

/// <summary>Đánh dấu controller / action luôn active (không tắt được qua bảng cấu hình): đăng nhập, quản trị bảng cấu hình.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class AlwaysActiveAttribute : Attribute;

/// <summary>Luôn ẩn khỏi Swagger đối tác (chỉ có trong tài liệu nội bộ) — API quản trị, quản lý tài khoản.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class InternalDocAttribute : Attribute;

/// <summary>
/// Trạng thái active / inactive của API (cache từ rdm.ApiEndpoints, làm mới định kỳ + ngay khi đổi qua API quản trị).
/// Khóa = "METHOD route-template" theo attribute route của action (VD "POST api/sd/prices-bapi/jobs/create").
/// API chưa có trong bảng (thêm code mà chưa đồng bộ) được coi là active.
/// </summary>
public sealed class ApiEndpointRegistry
{
    /// <summary>Mô tả cho đối tác (Swagger công khai) của 1 API.</summary>
    public sealed record PublicDoc(string? Summary, string? Description, bool Hidden);

    private sealed record State(HashSet<string> Inactive, IReadOnlyList<TemplateMatcher> FullyInactiveRoutes,
        Dictionary<string, PublicDoc> Docs, DateTime? LoadedAtUtc);

    private volatile State _state = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<TemplateMatcher>(),
        new Dictionary<string, PublicDoc>(StringComparer.OrdinalIgnoreCase), null);

    private HashSet<string> _internalDoc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>API mang [InternalDoc] (theo code) — luôn ẩn khỏi tài liệu đối tác.</summary>
    public bool IsInternalDoc(string key) => _internalDoc.Contains(key);

    public void SetInternalDocKeys(IEnumerable<string> keys) => _internalDoc = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Khóa của mọi action mang [InternalDoc].</summary>
    public static IEnumerable<string> DiscoverInternalDocKeys(IActionDescriptorCollectionProvider provider) =>
        provider.ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Where(ad => ad.AttributeRouteInfo?.Template is not null && ad.EndpointMetadata.OfType<InternalDocAttribute>().Any())
            .SelectMany(ad => ad.EndpointMetadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods)
                .Select(m => Key(m, ad.AttributeRouteInfo!.Template!)));

    /// <summary>Mô tả đối tác; null = chưa soạn (Swagger công khai để trống — không lộ mô tả kỹ thuật).</summary>
    public PublicDoc? GetPublicDoc(string httpMethod, string routeTemplate) =>
        _state.Docs.TryGetValue(Key(httpMethod, routeTemplate), out var d) ? d : null;

    /// <summary>Thời điểm API khởi động (đồng bộ bảng) — LastSeenAtUtc cũ hơn = API không còn trong code.</summary>
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LoadedAtUtc => _state.LoadedAtUtc;
    public int InactiveCount => _state.Inactive.Count;

    public static string Key(string httpMethod, string routeTemplate) => $"{httpMethod.ToUpperInvariant()} {routeTemplate}";

    public bool IsActive(string httpMethod, string routeTemplate) => !_state.Inactive.Contains(Key(httpMethod, routeTemplate));

    /// <summary>Đường dẫn thuộc route mà MỌI method đều inactive — dùng khi routing trả 405 (sai method) để vẫn trả 404.</summary>
    public bool IsFullyInactivePath(PathString path)
    {
        var values = new RouteValueDictionary();
        foreach (var matcher in _state.FullyInactiveRoutes)
        {
            values.Clear();
            if (matcher.TryMatch(path, values)) return true;
        }
        return false;
    }

    public void Load(IReadOnlyList<ApiEndpointSetting> rows, DateTime nowUtc)
    {
        var inactive = rows.Where(r => !r.IsActive && !r.IsLocked).Select(r => r.EndpointKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fullyInactive = rows.GroupBy(r => r.RouteTemplate, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(r => !r.IsActive && !r.IsLocked))
            .Select(g => new TemplateMatcher(TemplateParser.Parse(g.Key), new RouteValueDictionary()))
            .ToList();
        var docs = rows.ToDictionary(r => r.EndpointKey, r => new PublicDoc(r.PublicSummary, r.PublicDescription, r.PublicHidden),
            StringComparer.OrdinalIgnoreCase);
        _state = new State(inactive, fullyInactive, docs, nowUtc);
    }

    /// <summary>Mọi action controller có attribute route trong code — mỗi HTTP method 1 khóa; mô tả lấy từ XML doc (như Swagger).</summary>
    public static IReadOnlyList<ApiEndpointInfo> Discover(IActionDescriptorCollectionProvider provider, string? xmlDocPath = null)
    {
        var docs = LoadSummaries(xmlDocPath);
        var result = new Dictionary<string, ApiEndpointInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var ad in provider.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var template = ad.AttributeRouteInfo?.Template;
            if (string.IsNullOrEmpty(template)) continue;
            var methods = ad.EndpointMetadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).Distinct().ToList();
            if (methods.Count == 0) methods.Add("ANY");
            var locked = ad.EndpointMetadata.OfType<AlwaysActiveAttribute>().Any();
            docs.TryGetValue($"{ad.ControllerTypeInfo.FullName}.{ad.MethodInfo.Name}", out var description);
            foreach (var method in methods)
            {
                var key = Key(method, template);
                result[key] = new ApiEndpointInfo(key, method.ToUpperInvariant(), template, ad.ControllerName, ad.ActionName, locked, description);
            }
        }
        return result.Values.ToList();
    }

    /// <summary>"Namespace.Controller.Method" → nội dung &lt;summary&gt; (gộp khoảng trắng; &lt;see cref&gt; → tên cuối).</summary>
    private static Dictionary<string, string> LoadSummaries(string? xmlDocPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (xmlDocPath is null || !File.Exists(xmlDocPath)) return result;
        try
        {
            foreach (var member in System.Xml.Linq.XDocument.Load(xmlDocPath).Descendants("member"))
            {
                var name = (string?)member.Attribute("name");
                var summary = member.Element("summary");
                if (name is null || !name.StartsWith("M:", StringComparison.Ordinal) || summary is null) continue;
                foreach (var see in summary.Descendants().Where(e => e.Name.LocalName is "see" or "paramref").ToList())
                    see.ReplaceWith(((string?)see.Attribute("cref") ?? (string?)see.Attribute("name") ?? see.Value).Split('.', ':').Last());
                var text = System.Text.RegularExpressions.Regex.Replace(summary.Value, @"\s+", " ").Trim();
                var method = name[2..].Split('(')[0];      // bỏ tham số (không có overload trong controller)
                if (text.Length > 0) result[method] = text;
            }
        }
        catch (Exception)
        {
            // XML doc lỗi → chỉ thiếu mô tả, không ảnh hưởng bật / tắt API.
        }
        return result;
    }
}

/// <summary>Làm mới cache định kỳ (nhiều instance API dùng chung bảng; đổi ở instance khác áp dụng sau tối đa RefreshSeconds).</summary>
public sealed class ApiEndpointRefreshService : BackgroundService
{
    private readonly ApiEndpointRegistry _registry;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<ApiEndpointRefreshService> _logger;

    public ApiEndpointRefreshService(ApiEndpointRegistry registry, IServiceScopeFactory scopes, IConfiguration config,
        ILogger<ApiEndpointRefreshService> logger)
    {
        _registry = registry;
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(5, _config.GetValue("ApiEndpoints:RefreshSeconds", 30));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var rows = await scope.ServiceProvider.GetRequiredService<IApiEndpointRepository>().GetAllAsync(stoppingToken);
                _registry.Load(rows, DateTime.UtcNow);
                scope.ServiceProvider.GetRequiredService<ApiOutputRegistry>()
                    .Load(await scope.ServiceProvider.GetRequiredService<IApiOutputProfileRepository>().GetAllAsync(stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Giữ trạng thái đã nạp gần nhất — không mở lại API đang tắt chỉ vì DB tạm lỗi.
                _logger.LogWarning(ex, "Không làm mới được bảng cấu hình API — giữ trạng thái nạp lúc {LoadedAtUtc}", _registry.LoadedAtUtc);
            }
        }
    }
}

public static class ApiEndpointPipeline
{
    /// <summary>
    /// API inactive → 404 giống URL không tồn tại (đặt TRƯỚC authentication: không token cũng 404, không phải 401).
    /// Gồm cả trường hợp sai method trên route mà mọi method đều inactive (routing trả 405 → đổi thành 404).
    /// </summary>
    public static IApplicationBuilder UseApiEndpointSwitch(this IApplicationBuilder app)
    {
        var registry = app.ApplicationServices.GetRequiredService<ApiEndpointRegistry>();
        return app.Use(async (ctx, next) =>
        {
            var endpoint = ctx.GetEndpoint();
            var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
            var hidden = action?.AttributeRouteInfo?.Template is { } template
                ? !registry.IsActive(ctx.Request.Method, template) && endpoint!.Metadata.GetMetadata<AlwaysActiveAttribute>() is null
                : endpoint is not null && action is null && endpoint.DisplayName?.StartsWith("405", StringComparison.Ordinal) == true
                  && registry.IsFullyInactivePath(ctx.Request.Path);
            if (hidden)
            {
                ctx.SetEndpoint(null);
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;   // UseStatusCodePages định dạng như URL không tồn tại
                return;
            }
            await next();
        });
    }
}
