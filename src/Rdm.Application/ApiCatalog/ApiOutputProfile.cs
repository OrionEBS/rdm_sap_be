using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rdm.Application.ApiCatalog;

/// <summary>
/// Cấu hình cột output của 1 API (rdm.ApiOutputProfiles): ẩn / hiện, đổi tên, thứ tự field trong JSON trả về.
///   - CopyName = null: cấu hình của chính API gốc (EndpointKey). Xóa dòng = "reset như ban đầu".
///   - CopyName có giá trị: API MỚI sao chép từ API gốc — route "api/c/{CopyName}/{tham số route của API gốc}", cùng method, cùng quyền,
///     gọi đúng xử lý của API gốc nhưng output theo cấu hình riêng. API gốc đang tắt → bản sao cũng trả 404.
/// FieldsJson = mảng <see cref="ApiOutputField"/>. Ghi chỉ qua rdm.usp_ApiOutputProfile_* (Sql/ApiOutputProfiles.sql).
/// </summary>
public class ApiOutputProfile
{
    public int ProfileId { get; set; }
    public string EndpointKey { get; set; } = string.Empty;
    public string? CopyName { get; set; }
    /// <summary>Tên hiển thị / tóm tắt trên Swagger của API sao chép.</summary>
    public string? Title { get; set; }
    /// <summary>Chỉ áp dụng cho API sao chép: false = trả 404, không hiện trên Swagger.</summary>
    public bool IsActive { get; set; } = true;
    public string FieldsJson { get; set; } = "[]";
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public IReadOnlyList<ApiOutputField> GetFields() => ApiOutputField.Parse(FieldsJson);
}

/// <summary>
/// 1 field output. Path theo tên JSON gốc: "documentNumber", "items[].name1" ("[]" = phần tử mảng; gốc là mảng: "[].name1").
/// Visible = false: bỏ field (kèm mọi field con). Alias: tên mới (chỉ đổi tên ở cấp đó). Order: thứ tự trong object (nhỏ trước; không có = giữ chỗ cũ, sau các field có Order).
/// </summary>
public sealed record ApiOutputField(string Path, bool Visible = true, string? Alias = null, int? Order = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ApiOutputField> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ApiOutputField>();
        try
        {
            return JsonSerializer.Deserialize<List<ApiOutputField>>(json, Json) ?? new List<ApiOutputField>();
        }
        catch (JsonException)
        {
            return Array.Empty<ApiOutputField>();   // cấu hình hỏng → coi như không cấu hình (output gốc), không làm hỏng API
        }
    }

    public static string Serialize(IEnumerable<ApiOutputField> fields) => JsonSerializer.Serialize(fields, Json);
}

public interface IApiOutputProfileRepository
{
    Task<IReadOnlyList<ApiOutputProfile>> GetAllAsync(CancellationToken ct);

    /// <summary>Thêm (profileId null) hoặc sửa. CopyName null + đã có cấu hình gốc của EndpointKey → sửa dòng đó. Trả ProfileId.
    /// Trùng CopyName → <see cref="ApiOutputConflictException"/>.</summary>
    Task<int> SaveAsync(int? profileId, string endpointKey, string? copyName, string? title, bool isActive, IReadOnlyList<ApiOutputField> fields,
        string updatedBy, DateTime nowUtc, CancellationToken ct);

    /// <summary>Xóa cấu hình (cấu hình gốc → API trả output như ban đầu; bản sao → API sao chép không còn). false = không có.</summary>
    Task<bool> DeleteAsync(int profileId, CancellationToken ct);
}

public sealed class ApiOutputConflictException(string message) : Exception(message);

/// <summary>Kiểm tra dữ liệu cấu hình (dùng chung API quản trị và test).</summary>
public static partial class ApiOutputValidation
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,59}$")]
    public static partial Regex CopyNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    public static partial Regex AliasPattern();

    /// <summary>Lỗi theo field: path không có trong output API, alias sai mẫu, trùng tên sau khi đổi ở cùng 1 object.</summary>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<ApiOutputField> fields, IReadOnlyCollection<string> knownPaths)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string msg)
        {
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = new List<string>();
            list.Add(msg);
        }

        var known = knownPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var dup in fields.GroupBy(f => f.Path, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add(dup.Key, "Field khai báo nhiều lần.");
        foreach (var f in fields)
        {
            if (!known.Contains(f.Path)) Add(f.Path, "Không có field này trong output của API.");
            if (f.Alias is { Length: > 0 } a && !AliasPattern().IsMatch(a))
                Add(f.Path, $"Tên mới '{a}' không hợp lệ: chữ cái / số / gạch dưới, bắt đầu bằng chữ cái hoặc '_', tối đa 100 ký tự.");
        }

        // Tên sau khi đổi không được trùng trong cùng 1 object (chỉ xét field còn hiện).
        var rules = fields.Where(f => f.Path.Length > 0).GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First());
        foreach (var level in known.GroupBy(ParentOf, StringComparer.Ordinal))
        {
            var names = level
                .Select(p => rules.TryGetValue(p, out var r) ? (Path: p, Visible: r.Visible, Name: string.IsNullOrEmpty(r.Alias) ? NameOf(p) : r.Alias!)
                    : (Path: p, Visible: true, Name: NameOf(p)))
                .Where(x => x.Visible);
            foreach (var clash in names.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var x in clash)
                    Add(x.Path, $"Tên '{clash.Key}' trùng với field khác cùng cấp ({string.Join(", ", clash.Select(c => c.Path).Where(p => p != x.Path))}).");
        }
        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    /// <summary>"items[].name1" → "items[]"; "total" → "".</summary>
    public static string ParentOf(string path)
    {
        var i = path.LastIndexOf('.');
        return i < 0 ? string.Empty : path[..i];
    }

    public static string NameOf(string path)
    {
        var i = path.LastIndexOf('.');
        return i < 0 ? path : path[(i + 1)..];
    }
}

/// <summary>Bộ quy tắc đã biên dịch — áp lên JSON output (JsonNode). Thread-safe (chỉ đọc).</summary>
public sealed class ApiOutputRules
{
    private readonly Dictionary<string, ApiOutputField> _byPath;
    private readonly HashSet<string> _touched;   // node có quy tắc ở chính nó hoặc bên dưới

    private ApiOutputRules(Dictionary<string, ApiOutputField> byPath)
    {
        _byPath = byPath;
        _touched = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (var path in byPath.Keys)
        {
            _touched.Add(path);
            for (var i = 0; i < path.Length; i++)
            {
                if (path[i] is '.' or '[') _touched.Add(path[..i]);
                else if (path[i] == ']') _touched.Add(path[..(i + 1)]);
            }
        }
    }

    /// <summary>null = không có quy tắc nào làm thay đổi output (mọi field hiện, không đổi tên, không xếp lại).</summary>
    public static ApiOutputRules? Create(IEnumerable<ApiOutputField> fields)
    {
        var effective = fields
            .Where(f => !string.IsNullOrEmpty(f.Path) && (!f.Visible || !string.IsNullOrEmpty(f.Alias) || f.Order is not null))
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return effective.Count == 0 ? null : new ApiOutputRules(effective);
    }

    public bool Touches(string nodePath) => _touched.Contains(nodePath);

    public ApiOutputField? Get(string path) => _byPath.TryGetValue(path, out var f) ? f : null;

    public static string Child(string parentPath, string name) => parentPath.Length == 0 ? name : parentPath + "." + name;

    public static string Element(string arrayPath) => arrayPath + "[]";

    /// <summary>Áp quy tắc (sửa tại chỗ) và trả lại node.</summary>
    public JsonNode? Apply(JsonNode? node)
    {
        Apply(node, string.Empty);
        return node;
    }

    private void Apply(JsonNode? node, string path)
    {
        if (node is null || !_touched.Contains(path)) return;
        switch (node)
        {
            case JsonArray array:
                var elementPath = Element(path);
                foreach (var item in array) Apply(item, elementPath);
                break;
            case JsonObject obj:
                var props = new List<(string Name, JsonNode? Value, int Order, int Index)>();
                var index = 0;
                foreach (var (name, value) in obj.ToList())
                {
                    var childPath = Child(path, name);
                    var rule = Get(childPath);
                    index++;
                    if (rule is { Visible: false }) continue;
                    Apply(value, childPath);
                    props.Add((string.IsNullOrEmpty(rule?.Alias) ? name : rule!.Alias!, value, rule?.Order ?? int.MaxValue, index));
                }
                obj.Clear();   // tách node con khỏi cha → gắn lại theo thứ tự / tên mới
                foreach (var p in props.OrderBy(p => p.Order).ThenBy(p => p.Index))
                    obj[p.Name] = p.Value;
                break;
        }
    }
}
