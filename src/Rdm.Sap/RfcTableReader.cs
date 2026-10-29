using System.Globalization;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Đọc bảng qua RFC_READ_TABLE. Cắt cột theo OFFSET/LENGTH SAP trả trong FIELDS (không dùng delimiter
/// nên giá trị chứa ký tự '|' vẫn đúng). Tổng độ rộng các cột phải ≤ 512 (giới hạn DATA-WA).
/// </summary>
public static class RfcTableReader
{
    public const string FunctionName = "RFC_READ_TABLE";
    private const int OptionLineLength = 72;

    public static List<Dictionary<string, string>> Read(
        RfcDestination dest, string table, IReadOnlyList<string> fields, IReadOnlyList<string> where, int batchSize)
    {
        var rows = new List<Dictionary<string, string>>();
        for (var skip = 0; ; skip += batchSize)
        {
            var fn = dest.Repository.CreateFunction(FunctionName);
            fn.SetValue("QUERY_TABLE", table);
            fn.SetValue("ROWCOUNT", batchSize);
            fn.SetValue("ROWSKIPS", skip);

            var options = fn.GetTable("OPTIONS");
            foreach (var line in where)
            {
                if (line.Length > OptionLineLength)
                    throw new ArgumentException($"Điều kiện RFC_READ_TABLE dài quá {OptionLineLength} ký tự: {line}");
                options.Append();
                options.SetValue("TEXT", line);
            }

            var fieldTable = fn.GetTable("FIELDS");
            foreach (var f in fields)
            {
                fieldTable.Append();
                fieldTable.SetValue("FIELDNAME", f);
            }

            fn.InvokeAudited(dest);

            var layout = new List<(string Name, int Offset, int Length)>();
            foreach (IRfcStructure f in fn.GetTable("FIELDS"))
                layout.Add((f.GetString("FIELDNAME"), int.Parse(f.GetString("OFFSET")), int.Parse(f.GetString("LENGTH"))));

            var data = fn.GetTable("DATA");
            foreach (IRfcStructure r in data)
                rows.Add(Split(r.GetString("WA"), layout));

            if (data.RowCount < batchSize) return rows;
        }
    }

    internal static Dictionary<string, string> Split(string wa, IReadOnlyList<(string Name, int Offset, int Length)> layout)
    {
        var row = new Dictionary<string, string>(layout.Count, StringComparer.Ordinal);
        foreach (var (name, offset, length) in layout)
        {
            // NCo cắt khoảng trắng cuối WA → cột cuối có thể ngắn hơn LENGTH.
            row[name] = offset >= wa.Length ? string.Empty : wa.Substring(offset, Math.Min(length, wa.Length - offset)).Trim();
        }
        return row;
    }

    /// <summary>Dòng điều kiện WHERE với literal đã escape dấu nháy.</summary>
    public static string Condition(string field, string value, bool and = false) =>
        $"{(and ? "AND " : string.Empty)}{field} = '{value.Replace("'", "''")}'";

    /// <summary>
    /// WHERE "field thuộc 1 trong các giá trị", mỗi điều kiện 1 dòng OPTIONS (≤ 72 ký tự); nhiều giá trị → ( ... OR ... ).
    /// Danh sách rỗng = không lọc.
    /// </summary>
    public static IReadOnlyList<string> AnyOf(string field, IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToUpperInvariant()).Distinct().ToList();
        if (list.Count <= 1) return list.Select(v => Condition(field, v)).ToList();

        return list.Select((v, i) => (i == 0 ? "( " : "OR ") + Condition(field, v) + (i == list.Count - 1 ? " )" : "")).ToList();
    }

    /// <summary>Thêm "AND ( field = v1 OR ... )" vào WHERE đang có; danh sách rỗng = không thêm gì.</summary>
    public static void AndAnyOf(List<string> where, string field, IEnumerable<string> values)
    {
        var lines = AnyOf(field, values);
        if (lines.Count == 0) return;
        where.Add("AND");
        where.AddRange(lines);
    }

    /// <summary>Số kiểu P qua RFC_READ_TABLE: "     112941.20-" (dấu âm ở cuối).</summary>
    public static decimal ParseDecimal(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return 0m;
        var negative = s.EndsWith('-');
        if (negative) s = s[..^1].Trim();
        var value = decimal.Parse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    /// <summary>Kiểu D: YYYYMMDD; "00000000" / rỗng → null.</summary>
    public static DateOnly? ParseDate(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.Trim('0').Length == 0) return null;
        return DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// Giá trị CURR lưu nội bộ luôn 2 số lẻ; tiền tệ có số lẻ khác (TCURX-CURRDEC, vd. VND = 0) phải nhân 10^(2 - CURRDEC).
    /// </summary>
    public static decimal ToExternalAmount(decimal internalAmount, int currencyDecimals)
    {
        var shift = 2 - currencyDecimals;
        var factor = 1m;
        for (var i = 0; i < Math.Abs(shift); i++) factor *= 10m;
        return shift >= 0 ? internalAmount * factor : internalAmount / factor;
    }

    public static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
