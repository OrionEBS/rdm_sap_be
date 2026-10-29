using System.Text.RegularExpressions;

namespace Rdm.Infrastructure.Persistence;

/// <summary>
/// Script SQL nhúng (Persistence/Sql/*.sql) — table type + stored procedure, chạy bởi EF migration.
/// Tách batch theo dòng "GO".
/// </summary>
public static partial class SqlScripts
{
    public static IEnumerable<string> Batches(string name)
    {
        var asm = typeof(SqlScripts).Assembly;
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return GoSeparator().Split(reader.ReadToEnd())
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !IsOnlyComments(b));
    }

    private static bool IsOnlyComments(string batch) =>
        batch.Split('\n').All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("--", StringComparison.Ordinal));

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
