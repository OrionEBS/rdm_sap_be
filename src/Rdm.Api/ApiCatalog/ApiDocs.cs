using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Rdm.Api.ApiCatalog;

/// <summary>
/// 2 tài liệu Swagger:
///   - <see cref="Public"/> ("v1", /swagger): cho ĐỐI TÁC — chỉ API active, không API [InternalDoc] / PublicHidden; mô tả = mô tả đối tác
///     soạn trên trang quản trị (chưa soạn → để trống); XÓA mọi mô tả kỹ thuật lấy từ XML doc ở tham số / schema (tên BAPI, bảng SAP...).
///   - <see cref="Internal"/> ("internal"): NỘI BỘ — đầy đủ XML doc, API đang bật (ApiDocs:InternalShowInactive = true: cả API đang tắt,
///     ghi "[ĐANG TẮT]"); tài liệu TS (.md) luôn liệt kê mọi API kèm trạng thái;
///     chỉ mở từ chính máy chủ (localhost), ApiDocs:InternalOpen = true, hoặc tải qua API quản trị (Admin).
/// </summary>
public static class ApiDocs
{
    public const string Public = "v1";
    public const string Internal = "internal";

    public static bool IsInternalOnly(ApiDescription api) =>
        api.ActionDescriptor.EndpointMetadata.OfType<InternalDocAttribute>().Any();

    /// <summary>
    /// Lọc API theo tài liệu (dùng trong DocInclusionPredicate). API đang tắt không hiện ở CẢ 2 tài liệu;
    /// internalShowInactive = true (ApiDocs:InternalShowInactive) → tài liệu nội bộ vẫn liệt kê, đánh dấu "[ĐANG TẮT]".
    /// </summary>
    public static bool Include(string docName, ApiDescription api, ApiEndpointRegistry registry, bool internalShowInactive = false)
    {
        var template = api.ActionDescriptor.AttributeRouteInfo?.Template;
        if (docName == Internal)
            return internalShowInactive || template is null || api.HttpMethod is null || registry.IsActive(api.HttpMethod, template);
        if (docName != Public || template is null || api.HttpMethod is null) return docName == Public;
        return registry.IsActive(api.HttpMethod, template)
               && !IsInternalOnly(api)
               && registry.GetPublicDoc(api.HttpMethod, template) is not { Hidden: true };
    }

    /// <summary>Tài liệu NỘI BỘ truy cập được khi: request từ chính máy chủ (localhost), hoặc cấu hình ApiDocs:InternalOpen = true.</summary>
    public static bool InternalAllowed(HttpContext ctx, IConfiguration config) =>
        config.GetValue("ApiDocs:InternalOpen", false)
        || (ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip));
}

/// <summary>Tài liệu đối tác: thay mô tả bằng mô tả đối tác; tài liệu nội bộ: đánh dấu API đang tắt.</summary>
public sealed class ApiDocsOperationFilter : IOperationFilter
{
    private readonly ApiEndpointRegistry _registry;

    public ApiDocsOperationFilter(ApiEndpointRegistry registry)
    {
        _registry = registry;
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var api = context.ApiDescription;
        var template = api.ActionDescriptor.AttributeRouteInfo?.Template;
        if (template is null || api.HttpMethod is null) return;

        if (context.DocumentName == ApiDocs.Internal)
        {
            if (!_registry.IsActive(api.HttpMethod, template)) operation.Summary = "[ĐANG TẮT] " + operation.Summary;
            return;
        }
        if (context.DocumentName != ApiDocs.Public) return;

        var doc = _registry.GetPublicDoc(api.HttpMethod, template);
        operation.Summary = doc?.Summary;
        operation.Description = doc?.Description;
        foreach (var p in operation.Parameters)
        {
            p.Description = null;
            if (p.Schema is not null) p.Schema.Description = null;
        }
        if (operation.RequestBody is not null) operation.RequestBody.Description = null;
        foreach (var r in operation.Responses.Values) r.Description = ReasonPhrase(r.Description);
    }

    private static string ReasonPhrase(string? current) => string.IsNullOrWhiteSpace(current) ? "Response" : current switch
    {
        "Success" or "OK" or "Created" or "Accepted" or "Bad Request" or "Unauthorized" or "Forbidden" or "Not Found" or "Conflict"
            or "Unprocessable Content" or "Unprocessable Entity" or "Bad Gateway" or "Service Unavailable" or "Server Error" => current,
        _ => "Response"
    };
}

/// <summary>Tài liệu đối tác: xóa mô tả XML doc của mọi schema / thuộc tính (thường chứa tên field / bảng / BAPI SAP); đổi phần giới thiệu.</summary>
public sealed class ApiDocsDocumentFilter : IDocumentFilter
{
    private readonly IConfiguration _config;

    public ApiDocsDocumentFilter(IConfiguration config)
    {
        _config = config;
    }

    public void Apply(OpenApiDocument doc, DocumentFilterContext context)
    {
        if (context.DocumentName != ApiDocs.Public) return;
        var seen = new HashSet<OpenApiSchema>(ReferenceEqualityComparer.Instance);
        foreach (var schema in doc.Components.Schemas.Values) Strip(schema, seen);
        foreach (var tag in doc.Tags ?? new List<OpenApiTag>()) tag.Description = null;
    }

    private static void Strip(OpenApiSchema? s, HashSet<OpenApiSchema> seen)
    {
        if (s is null || !seen.Add(s)) return;
        s.Description = null;
        foreach (var p in s.Properties.Values) Strip(p, seen);
        Strip(s.Items, seen);
        Strip(s.AdditionalProperties, seen);
        foreach (var x in s.AllOf.Concat(s.AnyOf).Concat(s.OneOf)) Strip(x, seen);
    }
}
