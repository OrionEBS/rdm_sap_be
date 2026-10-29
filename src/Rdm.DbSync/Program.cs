using System.Reflection;
using Microsoft.Extensions.Configuration;
using Rdm.Infrastructure.DbSync;
using Rdm.Infrastructure.Hosting;

// Rdm.DbSync — đồng bộ dữ liệu DB RDM (master data md.*, cấu hình API) sang DB khác theo cấu hình DbSync,
// hoặc qua file snapshot đóng kèm gói triển khai: môi trường mới có dữ liệu ngay, không phải đồng bộ lại từ SAP.
// Ghi DB đích chỉ qua stored procedure. Mã thoát: 0 OK, 1 lỗi, 2 sai tham số / cấu hình, 3 cần xác nhận (--yes).

const string Usage = """
Rdm.DbSync <lệnh> [tùy chọn]

Lệnh:
  compare   So sánh số dòng từng bảng giữa DB nguồn và DB đích
  sync      Chép thẳng DB nguồn → DB đích
  export    Xuất DB nguồn ra file snapshot           (--out <file.json.gz>)
  import    Nạp file snapshot vào DB đích             (--in <file.json.gz>)

Tùy chọn:
  --config <file.json>   Nạp thêm file cấu hình (lặp được; file sau ưu tiên hơn)
  --source <conn>        DB nguồn  (mặc định DbSync:SourceConnectionString, rồi ConnectionStrings:SqlServer)
  --target <conn>        DB đích   (mặc định DbSync:TargetConnectionString; riêng import: rồi ConnectionStrings:SqlServer)
  --tables <mẫu,...>     Bảng cần đồng bộ (mặc định DbSync:Tables = md.*,rdm.ApiEndpoints; tài khoản: rdm.ApiUsers)
  --mode IfEmpty|Replace IfEmpty (mặc định): chỉ nạp bảng đích đang trống, không đè cấu hình API đã chỉnh bên đích.
                         Replace: thay toàn bộ master data, chép đè cấu hình API.
  --dry-run              Chỉ in việc sẽ làm, không ghi
  --no-migrate           Không tự chạy EF migration trên DB đích
  --yes                  Xác nhận ghi đè dữ liệu đang có ở DB đích (bắt buộc khi Replace mà đích đã có dữ liệu)

Ví dụ:
  Rdm.DbSync compare
  Rdm.DbSync sync --dry-run
  Rdm.DbSync sync --mode Replace --yes
  Rdm.DbSync export --out data\rdm-data.json.gz
  Rdm.DbSync import --in data\rdm-data.json.gz --config D:\Apps\Rdm\config\appsettings.Production.json
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Options opt;
try
{
    opt = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Lỗi tham số: {ex.Message}\n");
    Console.WriteLine(Usage);
    return 2;
}
if (opt.Command is null or "help" or "-h" or "--help")
{
    Console.WriteLine(Usage);
    return opt.Command is null ? 2 : 0;
}

IConfiguration config;
try
{
    config = BuildConfig(opt.ConfigFiles);
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException)
{
    return ConfigError(ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message);
}
var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var sync = new DbSyncService(Console.WriteLine);

try
{
    var tables = DbSyncTables.Select(opt.Tables ?? Split(config["DbSync:Tables"]) ?? config.GetSection("DbSync:Tables").Get<string[]>());
    var mode = opt.Mode ?? Enum.Parse<DbImportMode>(config["DbSync:Mode"] is { Length: > 0 } m ? m : nameof(DbImportMode.IfEmpty), ignoreCase: true);
    string? Source() => First(opt.Source, config["DbSync:SourceConnectionString"], config.GetConnectionString("SqlServer"));
    string? Target(bool fallbackToMain) =>
        First(opt.Target, config["DbSync:TargetConnectionString"], fallbackToMain ? config.GetConnectionString("SqlServer") : null);

    Console.WriteLine($"Rdm.DbSync {version} — {opt.Command}, bảng: {string.Join(", ", tables.Select(t => t.Name))}");
    switch (opt.Command)
    {
        case "compare":
        {
            var src = Source();
            var dst = Target(false);
            if (src is null && dst is null) return ConfigError("cần DB nguồn và / hoặc DB đích (--source / --target hoặc DbSync:*ConnectionString).");
            Console.WriteLine($"  nguồn: {(src is null ? "-" : DbSyncService.Describe(src))}\n  đích:  {(dst is null ? "-" : DbSyncService.Describe(dst))}\n");
            Console.WriteLine($"  {"Bảng",-28} {"Nguồn",10} {"Đích",10}");
            foreach (var r in await sync.CompareAsync(src, dst, tables, cts.Token))
                Console.WriteLine($"  {r.Table,-28} {(src is null ? "-" : N(r.SourceRows)),10} {(dst is null ? "-" : N(r.TargetRows)),10}" +
                                  (src is not null && dst is not null && r.SourceRows != r.TargetRows ? "   ≠" : ""));
            return 0;
        }
        case "export":
        {
            var src = Source() ?? (string?)null;
            if (src is null) return ConfigError("thiếu DB nguồn.");
            if (opt.Out is null) return ConfigError("thiếu --out <file>.");
            Console.WriteLine($"  nguồn: {DbSyncService.Describe(src)}");
            var snapshot = await sync.ExportAsync(src, tables, version, cts.Token);
            await DbSyncService.SaveAsync(snapshot, opt.Out, cts.Token);
            Console.WriteLine($"Xong: {Path.GetFullPath(opt.Out)} ({new FileInfo(opt.Out).Length / 1024.0:N0} KB, {snapshot.RowCounts.Values.Sum():N0} dòng, schema {snapshot.LastMigration})");
            return 0;
        }
        case "import":
        case "sync":
        {
            var dst = Target(opt.Command == "import");
            if (dst is null) return ConfigError("thiếu DB đích (--target hoặc DbSync:TargetConnectionString).");

            DbSnapshot snapshot;
            if (opt.Command == "import")
            {
                if (opt.In is null) return ConfigError("thiếu --in <file>.");
                snapshot = await DbSyncService.LoadAsync(opt.In, cts.Token);
                Console.WriteLine($"  snapshot: {opt.In} — {snapshot.SourceServer} / {snapshot.SourceDatabase}, {snapshot.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC, schema {snapshot.LastMigration}");
            }
            else
            {
                var src = Source();
                if (src is null) return ConfigError("thiếu DB nguồn.");
                if (DbSyncService.SameDatabase(src, dst)) return ConfigError($"DB nguồn và DB đích trùng nhau ({DbSyncService.Describe(dst)}).");
                Console.WriteLine($"  nguồn: {DbSyncService.Describe(src)}");
                snapshot = await sync.ExportAsync(src, tables, version, cts.Token);
            }
            Console.WriteLine($"  đích:  {DbSyncService.Describe(dst)} — chế độ {mode}{(opt.DryRun ? ", THỬ (không ghi)" : "")}");

            // Replace mà đích đã có dữ liệu → phải xác nhận (--yes, hoặc gõ tên DB đích khi chạy tay).
            if (!opt.DryRun && mode == DbImportMode.Replace && !opt.Yes)
            {
                var plan = await sync.ImportAsync(dst, snapshot, tables, mode, dryRun: true, migrate: false, cts.Token).ContinueWith(t =>
                    t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
                if (plan is null || plan.Any(r => r.TargetRowsBefore > 0 && (r.Action == "replace" || r.Action == "merge")))
                {
                    var db = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(dst).InitialCatalog;
                    if (Console.IsInputRedirected)
                    {
                        Console.Error.WriteLine("Chế độ Replace sẽ ghi đè dữ liệu đang có ở DB đích — chạy lại với --yes.");
                        return 3;
                    }
                    Console.Write($"\nGhi đè dữ liệu ở DB đích {DbSyncService.Describe(dst)}. Gõ tên DB đích ({db}) để tiếp tục: ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), db, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("Đã hủy.");
                        return 3;
                    }
                }
            }

            var results = await sync.ImportAsync(dst, snapshot, tables, mode, opt.DryRun, !opt.NoMigrate, cts.Token);
            var written = results.Count(r => r.Action is "replace" or "merge");
            Console.WriteLine(opt.DryRun
                ? $"Thử xong: {written} bảng sẽ được nạp, {results.Count - written} bảng bỏ qua."
                : $"Xong: nạp {written} bảng, bỏ qua {results.Count - written} bảng.");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Lệnh không hợp lệ: {opt.Command}\n");
            Console.WriteLine(Usage);
            return 2;
    }
}
catch (ArgumentException ex)
{
    return ConfigError(ex.Message);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Đã hủy (Ctrl+C).");
    return 1;
}
catch (Microsoft.Data.SqlClient.SqlException ex)
{
    Console.Error.WriteLine($"LỖI SQL {ex.Number}: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"LỖI: {ex.Message}");
    return 1;
}

static int ConfigError(string message)
{
    Console.Error.WriteLine($"Lỗi cấu hình: {message}");
    return 2;
}

static string N(int? n) => n is null ? "(chưa có)" : n.Value.ToString("N0");

static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static string[]? Split(string? csv) =>
    string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// appsettings.json cạnh exe → appsettings.Development.json (chỉ máy dev, không đóng gói) → RDM_CONFIG_FILE → --config → biến môi trường.
static IConfiguration BuildConfig(IReadOnlyList<string> extraFiles)
{
    var b = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddRdmSharedConfig();
    foreach (var f in extraFiles)
    {
        if (!File.Exists(f)) throw new FileNotFoundException($"Không có file cấu hình {f}.", f);
        b.AddJsonFile(Path.GetFullPath(f), optional: false);
    }
    return b.AddEnvironmentVariables().Build();
}

internal sealed class Options
{
    public string? Command { get; private set; }
    public List<string> ConfigFiles { get; } = new();
    public string? Source { get; private set; }
    public string? Target { get; private set; }
    public string[]? Tables { get; private set; }
    public DbImportMode? Mode { get; private set; }
    public string? Out { get; private set; }
    public string? In { get; private set; }
    public bool DryRun { get; private set; }
    public bool NoMigrate { get; private set; }
    public bool Yes { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new ArgumentException($"{a} cần giá trị.");
            switch (a.ToLowerInvariant())
            {
                case "--config": o.ConfigFiles.Add(Value()); break;
                case "--source": o.Source = Value(); break;
                case "--target": o.Target = Value(); break;
                case "--tables": o.Tables = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--mode":
                    o.Mode = Enum.TryParse<DbImportMode>(Value(), ignoreCase: true, out var m) && Enum.IsDefined(m)
                        ? m
                        : throw new ArgumentException("--mode chỉ nhận IfEmpty hoặc Replace.");
                    break;
                case "--out": o.Out = Value(); break;
                case "--in": o.In = Value(); break;
                case "--dry-run": o.DryRun = true; break;
                case "--no-migrate": o.NoMigrate = true; break;
                case "--yes": case "-y": o.Yes = true; break;
                default:
                    if (a.StartsWith('-') && a is not ("-h" or "--help")) throw new ArgumentException($"Tùy chọn không hỗ trợ: {a}");
                    if (o.Command is not null) throw new ArgumentException($"Thừa tham số: {a}");
                    o.Command = a.ToLowerInvariant();
                    break;
            }
        }
        return o;
    }
}
