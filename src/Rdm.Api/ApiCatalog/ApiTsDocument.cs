using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Rdm.Application.ApiCatalog;

namespace Rdm.Api.ApiCatalog;

/// <summary>
/// Tài liệu kỹ thuật (TS) NỘI BỘ dạng Markdown: theo nhóm → từng API (method, route, trạng thái, mô tả nội bộ + đối tác,
/// tham số, request / response) + phụ lục kiểu dữ liệu (thuộc tính, bắt buộc, mô tả XML doc — có chi tiết SAP).
/// </summary>
public static class ApiTsDocument
{
    public static string Build(IReadOnlyList<ApiEndpointSetting> rows, IApiDescriptionGroupCollectionProvider explorer, string version)
    {
        var docs = LoadXmlDocs(new[] { "Rdm.Api.xml", "Rdm.Application.xml" }.Select(f => Path.Combine(AppContext.BaseDirectory, f)));
        var apis = explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(a => a.ActionDescriptor.AttributeRouteInfo?.Template is not null && a.HttpMethod is not null)
            .GroupBy(a => ApiEndpointRegistry.Key(a.HttpMethod!, a.ActionDescriptor.AttributeRouteInfo!.Template!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var types = new List<Type>();

        var sb = new StringBuilder();
        sb.AppendLine("# Tài liệu kỹ thuật (TS) — RDM SAP Integration API");
        sb.AppendLine();
        sb.AppendLine($"> **NỘI BỘ** — có chi tiết kỹ thuật SAP, không chia sẻ cho đối tác. Phiên bản `{version}`, xuất lúc {DateTime.Now:dd/MM/yyyy HH:mm}.");
        sb.AppendLine($"> {rows.Count} API · {rows.Count(r => r.IsActive)} đang bật · {rows.Count(r => !r.IsActive)} đang tắt · " +
                      $"{rows.Count(r => r.PublicSummary is null)} chưa có mô tả đối tác.");
        sb.AppendLine();
        sb.AppendLine("Chứng thực: `POST /api/auth/token` → header `Authorization: Bearer <token>` (hạn 60 phút). " +
                      "Request ghi bắt buộc có `requestId` (idempotency). Swagger đối tác: `/swagger`; nội bộ: `/swagger-internal` (chỉ trên máy chủ).");
        sb.AppendLine();

        sb.AppendLine("## Mục lục");
        sb.AppendLine();
        sb.AppendLine("| Nhóm | Method | Route | Trạng thái | Mô tả đối tác |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows.OrderBy(r => r.GroupName).ThenBy(r => r.RouteTemplate).ThenBy(r => r.HttpMethod))
            sb.AppendLine($"| {r.GroupName} | {r.HttpMethod} | `/{r.RouteTemplate}` | {Status(r)} | {Cell(r.PublicSummary ?? "_(chưa có)_")} |");
        sb.AppendLine();

        foreach (var group in rows.GroupBy(r => r.GroupName).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var r in group.OrderBy(r => r.RouteTemplate).ThenBy(r => r.HttpMethod))
            {
                sb.AppendLine($"### {r.HttpMethod} `/{r.RouteTemplate}`");
                sb.AppendLine();
                sb.AppendLine($"- **Trạng thái:** {Status(r)}{(r.Note is null ? "" : $" — {r.Note}")}");
                sb.AppendLine($"- **Khóa cấu hình:** `{r.EndpointKey}` · action `{r.ActionName}`");
                sb.AppendLine($"- **Mô tả nội bộ:** {r.Description ?? "_(chưa có XML doc)_"}");
                sb.AppendLine($"- **Mô tả đối tác:** {(r.PublicSummary is null ? "_(chưa có — Swagger đối tác để trống)_" : r.PublicSummary)}" +
                              $"{(r.PublicHidden ? " · **ẩn khỏi tài liệu đối tác**" : "")}");
                if (r.PublicDescription is not null) sb.AppendLine($"  > {r.PublicDescription.Replace("\n", "\n  > ")}");

                if (apis.TryGetValue(r.EndpointKey, out var api))
                {
                    var parameters = api.ParameterDescriptions
                        .Where(p => p.Source == BindingSource.Path || p.Source == BindingSource.Query || p.Source == BindingSource.Header)
                        .ToList();
                    if (parameters.Count > 0)
                        sb.AppendLine("- **Tham số:** " + string.Join(", ", parameters.Select(p =>
                            $"`{p.Name}` ({p.Source.DisplayName.ToLowerInvariant()}, {TypeName(p.Type)}{(p.IsRequired ? ", bắt buộc" : "")})")));
                    var body = api.ParameterDescriptions.FirstOrDefault(p => p.Source == BindingSource.Body)?.Type;
                    if (body is not null)
                    {
                        sb.AppendLine($"- **Body:** `{TypeName(body)}`");
                        Collect(body, types, 0);
                    }
                    var responses = api.SupportedResponseTypes.OrderBy(t => t.StatusCode).ToList();
                    if (responses.Count > 0)
                        sb.AppendLine("- **Phản hồi:** " + string.Join(" · ", responses.Select(t =>
                            $"{t.StatusCode}{(t.Type is null || t.Type == typeof(void) ? "" : $" `{TypeName(t.Type)}`")}")));
                    foreach (var t in responses.Where(t => t.Type is not null)) Collect(t.Type!, types, 0);
                }
                else sb.AppendLine("- _(API không còn trong code)_");
                sb.AppendLine();
            }
        }

        if (types.Count > 0)
        {
            sb.AppendLine("## Phụ lục — kiểu dữ liệu");
            sb.AppendLine();
            foreach (var t in types.OrderBy(t => t.Name))
            {
                sb.AppendLine($"### `{t.Name}`");
                if (docs.TryGetValue($"T:{t.FullName}", out var typeDoc)) sb.AppendLine().AppendLine(typeDoc);
                sb.AppendLine();
                sb.AppendLine("| Thuộc tính | Kiểu | Bắt buộc | Mô tả |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var p in Properties(t))
                {
                    docs.TryGetValue($"P:{p.DeclaringType!.FullName}.{p.Name}", out var pd);
                    var required = p.GetCustomAttribute<RequiredAttribute>() is not null ? "✓" : "";
                    sb.AppendLine($"| `{JsonName(p.Name)}` | {Cell(TypeName(p.PropertyType))} | {required} | {Cell(pd ?? "")} |");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Status(ApiEndpointSetting r) => r.IsLocked ? "🔒 khóa (luôn bật)" : r.IsActive ? "✅ bật" : "⛔ tắt";
    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
    private static string JsonName(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<PropertyInfo> Properties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0);

    /// <summary>Kiểu DTO của dự án (namespace Rdm.*) + kiểu lồng, tối đa 3 cấp.</summary>
    private static void Collect(Type type, List<Type> acc, int depth)
    {
        var t = Unwrap(type);
        if (depth > 3 || t.Namespace?.StartsWith("Rdm.", StringComparison.Ordinal) != true || t.IsEnum || acc.Contains(t)) return;
        acc.Add(t);
        foreach (var p in Properties(t)) Collect(p.PropertyType, acc, depth + 1);
    }

    private static Type Unwrap(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } n) return n;
        if (t.IsArray) return Unwrap(t.GetElementType()!);
        if (t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType)
            return Unwrap(t.GetGenericArguments().Last());
        return t;
    }

    private static string TypeName(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } n) return TypeName(n) + "?";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            if (t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t) && args.Length == 1) return TypeName(args[0]) + "[]";
            return $"{t.Name.Split('`')[0]}<{string.Join(", ", args.Select(TypeName))}>";
        }
        return t switch
        {
            _ when t == typeof(string) => "string",
            _ when t == typeof(int) => "int",
            _ when t == typeof(long) => "long",
            _ when t == typeof(decimal) => "decimal",
            _ when t == typeof(bool) => "bool",
            _ when t == typeof(DateTime) => "datetime",
            _ when t == typeof(DateOnly) => "date",
            _ when t == typeof(Guid) => "guid",
            _ => t.Name
        };
    }

    /// <summary>Tên member XML doc ("T:...", "P:...", "M:...") → summary (gộp khoảng trắng, &lt;see cref&gt; → tên cuối).</summary>
    private static Dictionary<string, string> LoadXmlDocs(IEnumerable<string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(File.Exists))
        {
            try
            {
                foreach (var member in XDocument.Load(file).Descendants("member"))
                {
                    var name = (string?)member.Attribute("name");
                    var summary = member.Element("summary");
                    if (name is null || summary is null) continue;
                    foreach (var see in summary.Descendants().Where(e => e.Name.LocalName is "see" or "paramref").ToList())
                        see.ReplaceWith(((string?)see.Attribute("cref") ?? (string?)see.Attribute("name") ?? see.Value).Split('.', ':').Last());
                    var text = Regex.Replace(summary.Value, @"\s+", " ").Trim();
                    if (text.Length > 0) result[name.Split('(')[0]] = text;
                }
            }
            catch (Exception)
            {
                // XML doc lỗi → thiếu mô tả, không chặn xuất tài liệu
            }
        }
        return result;
    }
}
