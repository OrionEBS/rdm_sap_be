using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Rdm.Infrastructure.Persistence;

/// <summary>Cho `dotnet ef` — đọc connection string từ appsettings của Rdm.Api.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var apiDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Rdm.Api"));
        var basePath = Directory.Exists(apiDir) ? apiDir : Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("SqlServer")
                 ?? throw new InvalidOperationException("ConnectionStrings:SqlServer chưa cấu hình.");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema))
            .Options;
        return new AppDbContext(options);
    }
}
