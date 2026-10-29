using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Rdm.Infrastructure.Hosting;

/// <summary>Cấu hình dùng chung cho Rdm.Api (IIS) và Rdm.Worker (Windows Service).</summary>
public static class RdmHosting
{
    /// <summary>Biến môi trường trỏ tới file cấu hình production dùng chung (ngoài thư mục ứng dụng, giới hạn quyền đọc).</summary>
    public const string ConfigFileVariable = "RDM_CONFIG_FILE";

    /// <summary>
    /// Nạp file cấu hình dùng chung nếu có biến môi trường RDM_CONFIG_FILE (bắt buộc tồn tại khi đã đặt biến),
    /// rồi nạp lại biến môi trường để biến môi trường vẫn ưu tiên cao nhất.
    /// </summary>
    public static IConfigurationBuilder AddRdmSharedConfig(this IConfigurationBuilder configuration)
    {
        var file = Environment.GetEnvironmentVariable(ConfigFileVariable);
        if (string.IsNullOrWhiteSpace(file)) return configuration;
        if (!File.Exists(file))
            throw new FileNotFoundException($"{ConfigFileVariable} trỏ tới file cấu hình không tồn tại.", file);
        return configuration.AddJsonFile(file, optional: false, reloadOnChange: false).AddEnvironmentVariables();
    }

    /// <summary>Hangfire dùng SQL Server (schema hangfire) — cùng job store cho API (đưa job vào hàng đợi) và Worker (chạy job).</summary>
    public static IServiceCollection AddRdmHangfire(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("SqlServer")
                 ?? throw new InvalidOperationException("ConnectionStrings:SqlServer chưa cấu hình.");
        return services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(cs, new SqlServerStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(configuration.GetValue("Hangfire:QueuePollSeconds", 15)),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));
    }
}
