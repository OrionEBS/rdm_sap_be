using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rdm.Application.Abstractions;
using Rdm.Infrastructure.Jobs;
using Rdm.Infrastructure.Persistence;

namespace Rdm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("SqlServer");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "ConnectionStrings:SqlServer chưa cấu hình (appsettings.{Environment}.json hoặc biến môi trường ConnectionStrings__SqlServer).");

        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cs, sql =>
        {
            sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema);
            sql.EnableRetryOnFailure(3);
        }));
        services.AddScoped<ISapPostingRepository, SapPostingRepository>();
        services.AddScoped<IMasterDataRepository, MasterDataRepository>();
        services.AddScoped<Rdm.Application.ApiCatalog.IApiEndpointRepository, ApiEndpointRepository>();
        services.AddScoped<Rdm.Application.ApiCatalog.IApiOutputProfileRepository, ApiOutputProfileRepository>();
        services.AddScoped<Rdm.Application.Auth.IApiUserRepository, ApiUserRepository>();
        // Nhật ký gọi SAP: 1 bộ ghi nền / tiến trình (Rdm.Api và Rdm.Worker đều gọi SAP).
        services.AddSingleton<Rdm.Application.Abstractions.ISapCallLogStore, SapCallLogging.SapCallLogStore>();
        services.AddSingleton<SapCallLogging.SapCallLogWriter>();
        services.AddSingleton<Rdm.Application.Abstractions.ISapCallLogSink>(sp => sp.GetRequiredService<SapCallLogging.SapCallLogWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<SapCallLogging.SapCallLogWriter>());
        services.AddScoped<LogRetentionJob>();
        services.AddScoped<MasterDataSyncJob>();
        services.AddScoped<PostingJob>();
        services.AddScoped<IBackgroundPostingQueue, HangfirePostingQueue>();
        return services;
    }
}
