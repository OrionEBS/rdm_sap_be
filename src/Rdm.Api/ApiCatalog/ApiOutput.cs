using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Rdm.Application.ApiCatalog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Rdm.Api.ApiCatalog;

/// <summary>1 field trong output của API (dò từ kiểu response 2xx). Path theo quy ước <see cref="ApiOutputField"/>.</summary>
public sealed record ApiOutputFieldInfo(string Path, string Name, string Type, int Depth, bool HasChildren);

/// <summary>
/// Cache cấu hình output (rdm.ApiOutputProfiles) — nạp cùng lúc với bảng cấu hình API (khởi động, định kỳ, ngay khi sửa qua API quản trị).
///   - Cấu hình gốc: áp lên output của chính API.
///   - API sao chép: route "api/c/{copyName}/{tham số route API gốc...}" → middleware đổi path sang API gốc trước routing,
///     filter áp cấu hình của bản sao (không áp cấu hình gốc).
/// </summary>
public sealed class ApiOutputRegistry
{
    public const string CopyPrefix = "api/c";
    private const string ItemKey = "Rdm.ApiOutputProfile";

    /// <summary>Cấu hình đã biên dịch. Rules null = output giữ nguyên.</summary>
    public sealed record Compiled(ApiOutputProfile Profile, string HttpMethod, string RouteTemplate, ApiOutputRules? Rules)
    {
        public bool IsCopy => Profile.CopyName is not null;
        public string Route => IsCopy ? ApiOutputRegistry.CopyRoute(Profile.CopyName!, RouteTemplate) : RouteTemplate;
    }

    private sealed record State(Dictionary<string, Compiled> Originals, Dictionary<string, Compiled> Copies);

    private volatile State _state = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<Compiled> Copies => _state.Copies.Values;

    public Compiled? GetOriginal(string endpointKey) => _state.Originals.GetValueOrDefault(endpointKey);

    public Compiled? GetCopy(string copyName) => _state.Copies.GetValueOrDefault(copyName);

    public void Load(IEnumerable<ApiOutputProfile> profiles)
    {
        var originals = new Dictionary<string, Compiled>(StringComparer.OrdinalIgnoreCase);
        var copies = new Dictionary<string, Compiled>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            var (method, template) = SplitKey(p.EndpointKey);
            var compiled = new Compiled(p, method, template, ApiOutputRules.Create(p.GetFields()));
            if (p.CopyName is null) originals[p.EndpointKey] = compiled;
            else copies[p.CopyName] = compiled;
        }
        _state = new State(originals, copies);
    }

    /// <summary>"GET api/x/{id}" → ("GET", "api/x/{id}").</summary>
    public static (string Method, string Template) SplitKey(string key)
    {
        var i = key.IndexOf(' ');
        return i < 0 ? ("GET", key) : (key[..i].ToUpperInvariant(), key[(i + 1)..]);
    }

    /// <summary>Tên tham số route của API gốc theo thứ tự — null nếu route không sao chép được (segment ghép literal + tham số, catch-all).</summary>
    public static IReadOnlyList<string>? RouteParameters(string template)
    {
        var names = new List<string>();
        foreach (var segment in TemplateParser.Parse(template).Segments)
        {
            if (segment.Parts.Count != 1) return null;
            var part = segment.Parts[0];
            if (part.IsParameter)
            {
                if (part.IsCatchAll) return null;
                names.Add(part.Name!);
            }
        }
        return names;
    }

    public static string CopyRoute(string copyName, string sourceTemplate) =>
        $"{CopyPrefix}/{copyName}" + string.Concat((RouteParameters(sourceTemplate) ?? Array.Empty<string>()).Select(n => $"/{{{n}}}"));

    /// <summary>Path API sao chép → path API gốc (giữ nguyên giá trị tham số). null = không khớp.</summary>
    public static string? MapToSource(string sourceTemplate, IReadOnlyList<string> values)
    {
        var segments = new List<string>();
        var next = 0;
        foreach (var segment in TemplateParser.Parse(sourceTemplate).Segments)
        {
            var part = segment.Parts[0];
            if (!part.IsParameter)
            {
                segments.Add(part.Text!);
                continue;
            }
            if (next < values.Count) segments.Add(values[next++]);
            else if (part.IsOptional || part.DefaultValue is not null) break;
            else return null;
        }
        return next == values.Count ? "/" + string.Join('/', segments) : null;
    }

    /// <summary>Request tới API sao chép → cấu hình bản sao (do middleware gắn).</summary>
    public static Compiled? CopyOf(HttpContext ctx) => ctx.Items.TryGetValue(ItemKey, out var v) ? v as Compiled : null;

    /// <summary>
    /// Đặt TRƯỚC UseRouting: "/api/c/{copyName}/..." → path API gốc. Bản sao không có / đang tắt / sai method / sai số tham số → 404.
    /// API gốc đang tắt → middleware bật / tắt API trả 404 như thường.
    /// </summary>
    public static IApplicationBuilder UseApiOutputCopies(IApplicationBuilder app)
    {
        var registry = app.ApplicationServices.GetRequiredService<ApiOutputRegistry>();
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/" + CopyPrefix, StringComparison.OrdinalIgnoreCase, out var rest))
            {
                var parts = (rest.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
                var copy = parts.Length > 0 ? registry.GetCopy(parts[0]) : null;
                var target = copy is { Profile.IsActive: true } && HttpMethods.Equals(copy.HttpMethod, ctx.Request.Method)
                    ? MapToSource(copy.RouteTemplate, parts[1..])
                    : null;
                if (target is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Items[ItemKey] = copy;
                ctx.Request.Path = target;
            }
            await next();
        });
    }

    // ------------------------------------------------------------------ dò field từ kiểu response

    /// <summary>Kiểu response thành công của API (ProducesResponseType / kiểu trả về) — null nếu không xác định.</summary>
    public static Type? ResponseType(ApiDescription api) =>
        api.SupportedResponseTypes
            .Where(r => r.StatusCode is >= 200 and < 300 && r.Type is not null && r.Type != typeof(void))
            .OrderBy(r => r.StatusCode)
            .Select(r => r.Type)
            .FirstOrDefault();

    /// <summary>Mã HTTP trả cùng kiểu với response thành công (VD 200 + 422 cùng PostingResult) — cấu hình output áp cho tất cả.</summary>
    public static IReadOnlySet<string> OutputStatusCodes(ApiDescription api)
    {
        var type = ResponseType(api);
        return api.SupportedResponseTypes.Where(r => type is not null && r.Type == type).Select(r => r.StatusCode.ToString())
            .ToHashSet(StringComparer.Ordinal);
    }

    public static ApiDescription? FindApi(IApiDescriptionGroupCollectionProvider explorer, string endpointKey)
    {
        var (method, template) = SplitKey(endpointKey);
        return explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .FirstOrDefault(a => string.Equals(a.HttpMethod, method, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(a.ActionDescriptor.AttributeRouteInfo?.Template, template, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mọi field (kể cả lồng trong object / mảng, tối đa 6 cấp) theo tên JSON thực tế của serializer.</summary>
    public static IReadOnlyList<ApiOutputFieldInfo> DiscoverFields(Type type, JsonSerializerOptions serializerOptions)
    {
        var options = new JsonSerializerOptions(serializerOptions) { TypeInfoResolver = serializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver() };
        var result = new List<ApiOutputFieldInfo>();
        Walk(type, string.Empty, 0, new HashSet<Type>());
        return result;

        void Walk(Type t, string path, int depth, HashSet<Type> stack)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (depth > 6 || IsLeaf(t) || !stack.Add(t)) return;
            try
            {
                var info = options.GetTypeInfo(t);
                switch (info.Kind)
                {
                    case JsonTypeInfoKind.Object:
                        foreach (var p in info.Properties.Where(p => p.Get is not null))
                        {
                            var childPath = ApiOutputRules.Child(path, p.Name);
                            var childType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                            var index = result.Count;
                            result.Add(new ApiOutputFieldInfo(childPath, p.Name, Label(childType, options), depth, false));
                            Walk(childType, childPath, depth + 1, stack);
                            if (result.Count > index + 1) result[index] = result[index] with { HasChildren = true };
                        }
                        break;
                    case JsonTypeInfoKind.Enumerable when ElementOf(t) is { } element:
                        Walk(element, ApiOutputRules.Element(path), depth, stack);   // phần tử mảng không phải field riêng
                        break;
                }
            }
            finally
            {
                stack.Remove(t);
            }
        }
    }

    private static Type? ElementOf(Type t) =>
        t.IsArray ? t.GetElementType()
        : (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? t : t.GetInterfaces()
              .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))?.GetGenericArguments()[0];

    private static bool IsLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
        || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(object)
        || typeof(JsonNode).IsAssignableFrom(t) || t == typeof(JsonElement);

    private static string Label(Type t, JsonSerializerOptions options)
    {
        if (t == typeof(string) || t == typeof(Guid)) return "string";
        if (t == typeof(bool)) return "boolean";
        if (t.IsEnum) return "string (enum)";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "datetime";
        if (t == typeof(DateOnly)) return "date";
        if (t.IsPrimitive || t == typeof(decimal)) return "number";
        if (IsLeaf(t)) return "any";
        return options.GetTypeInfo(t).Kind switch
        {
            JsonTypeInfoKind.Enumerable => "array",
            JsonTypeInfoKind.Dictionary => "object (map)",
            _ => "object"
        };
    }

    // ------------------------------------------------------------------ Swagger

    /// <summary>Schema output sau khi áp quy tắc (object / mảng bị đổi được tạo inline; phần không đổi giữ $ref).</summary>
    public static OpenApiSchema TransformSchema(OpenApiSchema schema, ApiOutputRules rules, SchemaRepository repository, bool keepDescriptions) =>
        Transform(schema, string.Empty, rules, repository, keepDescriptions, 0);

    private static OpenApiSchema Transform(OpenApiSchema schema, string path, ApiOutputRules rules, SchemaRepository repo, bool keep, int depth)
    {
        if (!rules.Touches(path) || depth > 12) return schema;
        var s = schema.Reference is { } r && repo.Schemas.TryGetValue(r.Id, out var resolved) ? resolved : schema;
        if (s.AllOf.Count == 1 && s.Properties.Count == 0)   // field nullable kiểu object: allOf [$ref]
            return new OpenApiSchema
            {
                AllOf = new List<OpenApiSchema> { Transform(s.AllOf[0], path, rules, repo, keep, depth + 1) },
                Nullable = s.Nullable || schema.Nullable,
                Description = keep ? schema.Description ?? s.Description : null
            };
        if (s.Type == "array" && s.Items is not null)
            return new OpenApiSchema
            {
                Type = "array",
                Items = Transform(s.Items, ApiOutputRules.Element(path), rules, repo, keep, depth + 1),
                Nullable = s.Nullable || schema.Nullable,
                Description = keep ? schema.Description ?? s.Description : null
            };
        if (s.Properties.Count == 0) return schema;

        var result = new OpenApiSchema
        {
            Type = "object",
            Nullable = s.Nullable || schema.Nullable,
            AdditionalPropertiesAllowed = s.AdditionalPropertiesAllowed,
            Description = keep ? schema.Description ?? s.Description : null
        };
        var index = 0;
        foreach (var (name, prop, rule, _) in s.Properties
                     .Select(kv => (kv.Key, kv.Value, Rule: rules.Get(ApiOutputRules.Child(path, kv.Key)), Index: index++))
                     .Where(x => x.Rule is not { Visible: false })
                     .OrderBy(x => x.Rule?.Order ?? int.MaxValue).ThenBy(x => x.Index))
        {
            var newName = string.IsNullOrEmpty(rule?.Alias) ? name : rule!.Alias!;
            result.Properties[newName] = Transform(prop, ApiOutputRules.Child(path, name), rules, repo, keep, depth + 1);
            if (s.Required.Contains(name)) result.Required.Add(newName);
        }
        return result;
    }
}

/// <summary>Áp cấu hình output lên kết quả JSON của action (không đụng ProblemDetails / file).</summary>
public sealed class ApiOutputResultFilter : IAsyncAlwaysRunResultFilter
{
    private readonly ApiOutputRegistry _registry;
    private readonly JsonSerializerOptions _json;
    private readonly IApiDescriptionGroupCollectionProvider _explorer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> _types = new();

    public ApiOutputResultFilter(ApiOutputRegistry registry, IOptions<JsonOptions> json, IApiDescriptionGroupCollectionProvider explorer)
    {
        _registry = registry;
        _json = json.Value.JsonSerializerOptions;
        _explorer = explorer;
    }

    /// <summary>Kiểu response thành công của action (cache theo action) — chỉ áp cấu hình khi kết quả đúng kiểu này (lỗi khác kiểu giữ nguyên).</summary>
    private Type? SuccessType(ActionDescriptor action) => _types.GetOrAdd(action.Id, _ =>
        _explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items).Where(a => a.ActionDescriptor.Id == action.Id)
            .Select(ApiOutputRegistry.ResponseType).FirstOrDefault(t => t is not null));

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: { } value } result && value is not ProblemDetails
            && context.ActionDescriptor is ControllerActionDescriptor { AttributeRouteInfo.Template: { } template })
        {
            var profile = ApiOutputRegistry.CopyOf(context.HttpContext)
                          ?? _registry.GetOriginal(ApiEndpointRegistry.Key(context.HttpContext.Request.Method, template));
            if (profile?.Rules is { } rules && SuccessType(context.ActionDescriptor) is { } type && type.IsInstanceOfType(value))
            {
                result.Value = rules.Apply(JsonSerializer.SerializeToNode(value, value.GetType(), _json));
                result.DeclaredType = typeof(JsonNode);
            }
        }
        return next();
    }
}

/// <summary>Swagger: response của API gốc theo cấu hình output; thêm API sao chép (cùng tham số / request, response theo cấu hình bản sao).</summary>
public sealed class ApiOutputOperationFilter : IOperationFilter
{
    private readonly ApiOutputRegistry _registry;

    public ApiOutputOperationFilter(ApiOutputRegistry registry)
    {
        _registry = registry;
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var api = context.ApiDescription;
        var template = api.ActionDescriptor.AttributeRouteInfo?.Template;
        if (template is null || api.HttpMethod is null) return;
        if (_registry.GetOriginal(ApiEndpointRegistry.Key(api.HttpMethod, template))?.Rules is not { } rules) return;
        var codes = ApiOutputRegistry.OutputStatusCodes(api);
        foreach (var (code, response) in operation.Responses.Where(r => codes.Contains(r.Key)))
            foreach (var media in response.Content.Values.Where(m => m.Schema is not null))
                media.Schema = ApiOutputRegistry.TransformSchema(media.Schema, rules, context.SchemaRepository, context.DocumentName == ApiDocs.Internal);
    }
}

public sealed class ApiOutputDocumentFilter : IDocumentFilter
{
    private readonly ApiOutputRegistry _registry;

    public ApiOutputDocumentFilter(ApiOutputRegistry registry)
    {
        _registry = registry;
    }

    public void Apply(OpenApiDocument doc, DocumentFilterContext context)
    {
        var keep = context.DocumentName == ApiDocs.Internal;
        foreach (var copy in _registry.Copies.Where(c => c.Profile.IsActive).OrderBy(c => c.Profile.CopyName))
        {
            // API gốc không có trong tài liệu này (đang tắt / ẩn với đối tác) → bản sao cũng không hiện.
            if (!doc.Paths.TryGetValue("/" + copy.RouteTemplate, out var sourcePath)
                || !Enum.TryParse<OperationType>(copy.HttpMethod, true, out var opType)
                || !sourcePath.Operations.TryGetValue(opType, out var source))
                continue;

            var op = new OpenApiOperation(source)
            {
                OperationId = null,
                Summary = copy.Profile.Title ?? $"{source.Summary} (bản sao '{copy.Profile.CopyName}')",
                Tags = new List<OpenApiTag> { new() { Name = "API sao chép" } }
            };
            if (keep)
                op.Description = $"API sao chép từ `{copy.HttpMethod} /{copy.RouteTemplate}` — cùng xử lý, output theo cấu hình riêng.\n\n{source.Description}";
            op.Responses = new OpenApiResponses();
            foreach (var (code, response) in source.Responses) op.Responses[code] = new OpenApiResponse(response);

            // Response gốc có thể đã bị đổi theo cấu hình API gốc → dựng lại từ kiểu response rồi áp cấu hình bản sao.
            var api = context.ApiDescriptions.FirstOrDefault(a =>
                string.Equals(a.HttpMethod, copy.HttpMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ActionDescriptor.AttributeRouteInfo?.Template, copy.RouteTemplate, StringComparison.OrdinalIgnoreCase));
            if (api is not null && ApiOutputRegistry.ResponseType(api) is { } type)
            {
                var original = context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository);
                var codes = ApiOutputRegistry.OutputStatusCodes(api);
                foreach (var (code, response) in op.Responses.Where(r => codes.Contains(r.Key)))
                {
                    response.Content = response.Content.ToDictionary(c => c.Key, c => new OpenApiMediaType(c.Value)
                    {
                        Schema = copy.Rules is { } rules
                            ? ApiOutputRegistry.TransformSchema(original, rules, context.SchemaRepository, keep)
                            : original
                    });
                }
            }

            var path = "/" + copy.Route;
            if (!doc.Paths.TryGetValue(path, out var item)) doc.Paths[path] = item = new OpenApiPathItem();
            item.Operations[opType] = op;
        }
    }

}
