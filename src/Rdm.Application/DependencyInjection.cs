using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;

namespace Rdm.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPostingService, PostingService>();
        services.AddScoped<MasterData.IMasterDataSyncService, MasterData.MasterDataSyncService>();
        services.TryAddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<Auth.ApiUser>,
            Microsoft.AspNetCore.Identity.PasswordHasher<Auth.ApiUser>>();
        services.AddScoped<Auth.AuthService>();
        return services;
    }
}
