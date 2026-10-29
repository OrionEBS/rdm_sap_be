using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rdm.Api.ApiCatalog;
using Rdm.Application.Auth;
using Rdm.Infrastructure.Hosting;

namespace Rdm.Api.Controllers;

/// <summary>
/// Môi trường đang chạy (cho trang quản trị hiện rõ đang trỏ DB / SAP nào): tên môi trường, nhãn, DB (server / database),
/// SAP (host / client), file cấu hình, có tự khởi động lại khi sửa cấu hình không. Không trả mật khẩu. Chỉ role Admin.
/// </summary>
[ApiController]
[Route("api/admin/system")]
[Authorize(Roles = ApiRoles.Admin)]
[AlwaysActive]
[InternalDoc]
[Produces("application/json")]
public sealed class SystemController : ControllerBase
{
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ConfigChangeRestart? _restart;

    public SystemController(IConfiguration config, IWebHostEnvironment env, IServiceProvider services)
    {
        _config = config;
        _env = env;
        _restart = services.GetService<ConfigChangeRestart>();
    }

    /// <summary>Thông tin môi trường đang chạy.</summary>
    [HttpGet]
    [ProducesResponseType<SystemInfo>(StatusCodes.Status200OK)]
    public SystemInfo Get()
    {
        string? db = null;
        var cs = _config.GetConnectionString("SqlServer");
        if (!string.IsNullOrWhiteSpace(cs))
        {
            var b = new SqlConnectionStringBuilder(cs);
            db = $"{b.DataSource} / {b.InitialCatalog}";
        }
        var sap = _config["Sap:AppServerHost"] is { Length: > 0 } host
            ? $"{host} · {_config["Sap:SystemId"]} · client {_config["Sap:Client"]}"
            : null;
        var shared = Environment.GetEnvironmentVariable(RdmHosting.ConfigFileVariable);
        return new SystemInfo(
            _env.EnvironmentName,
            _config["Environment:Label"] is { Length: > 0 } label ? label : _env.EnvironmentName,
            _config["Environment:Color"],
            Environment.MachineName,
            typeof(SystemController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            StartedAtUtc,
            db,
            sap,
            string.IsNullOrWhiteSpace(shared) ? null : Path.GetFileName(shared),
            _config.GetValue("Hangfire:ServerEnabled", true),
            _restart is not null,
            _restart?.Files.Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToList() ?? new List<string>());
    }
}

public sealed record SystemInfo(
    string Environment, string Label, string? Color, string Machine, string? Version, DateTime StartedAtUtc, string? Database, string? Sap,
    string? ConfigFile, bool HangfireServer, bool RestartOnConfigChange, IReadOnlyList<string> WatchedConfigFiles);
