using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rdm.Application.Abstractions;

namespace Rdm.Sap;

public static class DependencyInjection
{
    public static IServiceCollection AddSap(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SapOptions>(configuration.GetSection(SapOptions.SectionName));
        // Nhật ký gọi SAP (tạo JSON trên luồng NCo — RfcTableReader là static nên cấu hình tĩnh).
        RfcAudit.Options = configuration.GetSection(RfcAuditOptions.SectionName).Get<RfcAuditOptions>() ?? new RfcAuditOptions();
        services.AddSingleton<SapDestinationProvider>();
        services.AddSingleton<SapResilience>();
        services.AddSingleton<ISapClient, SapClient>();
        services.AddSingleton<ISapMasterDataClient, SapMasterDataClient>();
        return services;
    }
}
