using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rdm.Infrastructure.Hosting;

/// <summary>
/// Sửa file cấu hình là có hiệu lực ngay (không phải vào IIS / services.msc): theo dõi appsettings.json, appsettings.{Env}.json
/// và file RDM_CONFIG_FILE; file đổi nội dung + JSON hợp lệ → dừng tiến trình có kiểm soát (ghi nốt log, job đang chạy theo
/// ShutdownTimeout) để chạy lại với cấu hình mới:
///   - Rdm.Api trên IIS: ASP.NET Core Module khởi động lại ngay ở request kế tiếp.
///   - Rdm.Worker (Windows Service): thoát mã 3 → recovery của service khởi động lại.
/// JSON lỗi → KHÔNG khởi động lại (giữ cấu hình đang chạy), ghi log lỗi. Bật / tắt: Hosting:RestartOnConfigChange
/// (mặc định bật, trừ môi trường Development – chạy console không có ai khởi động lại).
/// </summary>
public sealed class ConfigChangeRestart : IHostedService, IDisposable
{
    public const int ExitCode = 3;

    private readonly IHostEnvironment _env;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConfigChangeRestart> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Timer? _debounce;
    private bool _stopping;

    public ConfigChangeRestart(IHostEnvironment env, IHostApplicationLifetime lifetime, ILogger<ConfigChangeRestart> logger)
    {
        _env = env;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Các file cấu hình đang được nạp (chỉ những file tồn tại).</summary>
    public IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var files = new List<string>
        {
            Path.Combine(_env.ContentRootPath, "appsettings.json"),
            Path.Combine(_env.ContentRootPath, $"appsettings.{_env.EnvironmentName}.json")
        };
        var shared = Environment.GetEnvironmentVariable(RdmHosting.ConfigFileVariable);
        if (!string.IsNullOrWhiteSpace(shared)) files.Add(Path.GetFullPath(shared));
        Files = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var f in Files) _hashes[f] = Hash(f);
        foreach (var group in Files.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
        {
            var w = new FileSystemWatcher(group.Key)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            var names = group.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            void OnEvent(object _, FileSystemEventArgs e)
            {
                if (names.Contains(e.Name ?? string.Empty)) Schedule();
            }
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Renamed += (s, e) => OnEvent(s, e);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        _logger.LogInformation("Theo dõi thay đổi cấu hình: {Files} — đổi là tự khởi động lại để áp dụng", string.Join(", ", Files));
        return Task.CompletedTask;
    }

    // Trình soạn thảo / robocopy ghi file nhiều lần liên tiếp → gom trong 2 giây.
    private void Schedule()
    {
        lock (_lock)
        {
            if (_stopping) return;
            _debounce ??= new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    private void Check()
    {
        var changed = new List<string>();
        foreach (var f in Files)
        {
            var h = Hash(f);
            if (h == _hashes[f]) continue;   // chỉ đổi thời gian ghi, nội dung như cũ
            var error = Validate(f);
            if (error is not null)
            {
                _logger.LogError("Cấu hình {File} vừa sửa KHÔNG hợp lệ ({Error}) — giữ cấu hình đang chạy, chưa khởi động lại", f, error);
                return;
            }
            changed.Add(f);
        }
        if (changed.Count == 0) return;

        lock (_lock)
        {
            if (_stopping) return;
            _stopping = true;
        }
        _logger.LogWarning("Cấu hình thay đổi: {Files} — khởi động lại để áp dụng", string.Join(", ", changed.Select(Path.GetFileName)));
        Environment.ExitCode = ExitCode;   // Windows Service: mã khác 0 → recovery khởi động lại
        _lifetime.StopApplication();
    }

    internal static string? Validate(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "gốc phải là object { }";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return ex.Message;
        }
    }

    private static string? Hash(string file)
    {
        try { return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ReadShared(file)))); }
        catch (IOException) { return null; }
    }

    // Đọc cả khi trình soạn thảo đang giữ file.
    private static string ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var w in _watchers) w.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce?.Dispose();
    }
}

public static class ConfigChangeRestartExtensions
{
    /// <summary>Bật khởi động lại khi sửa cấu hình (Hosting:RestartOnConfigChange; mặc định bật trừ Development).</summary>
    public static IServiceCollection AddConfigChangeRestart(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        if (!configuration.GetValue("Hosting:RestartOnConfigChange", !env.IsDevelopment())) return services;
        services.AddSingleton<ConfigChangeRestart>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigChangeRestart>());
        return services;
    }
}
