using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Rdm.Api.ApiCatalog;
using Rdm.Api.DbSync;
using Rdm.Application.Auth;
using Rdm.Infrastructure.DbSync;

namespace Rdm.Api.Controllers;

/// <summary>
/// Đồng bộ dữ liệu DB của API này (master data md.*, cấu hình API) sang DB Dev (DbSync:TargetConnectionString) — nút bấm trên
/// trang quản trị. Ghi DB đích chỉ qua stored procedure (md.usp_*_Replace, rdm.usp_ApiEndpoint_Import). Chỉ role Admin.
/// </summary>
[ApiController]
[Route("api/admin/db-sync")]
[Authorize(Roles = ApiRoles.Admin)]
[AlwaysActive]
[InternalDoc]
[Produces("application/json")]
public sealed class DbSyncController : ControllerBase
{
    private readonly DbSyncRunner _runner;

    public DbSyncController(DbSyncRunner runner)
    {
        _runner = runner;
    }

    /// <summary>Cấu hình hiện tại (server / database nguồn - đích, không lộ mật khẩu), đang chạy hay không, lần đồng bộ gần nhất.</summary>
    [HttpGet]
    [ProducesResponseType<DbSyncStatus>(StatusCodes.Status200OK)]
    public ActionResult<DbSyncStatus> GetStatus()
    {
        if (!_runner.Enabled)
            return new DbSyncStatus(false, null, null, null, Array.Empty<string>(), false, false, null);
        return new DbSyncStatus(true, DbSyncService.Describe(_runner.SourceConnectionString!), DbSyncService.Describe(_runner.TargetConnectionString!),
            _runner.TargetDatabase, _runner.Tables.Select(t => t.Name).ToList(), _runner.AllowMigrate, _runner.IsRunning, _runner.LastRun);
    }

    /// <summary>Số dòng từng bảng ở DB nguồn và DB đích (null = DB đích chưa có bảng).</summary>
    [HttpGet("compare")]
    [ProducesResponseType<IReadOnlyList<DbCompareRow>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<DbCompareRow>>> Compare(CancellationToken ct)
    {
        if (!_runner.Enabled) return NotEnabled();
        try
        {
            return Ok(await _runner.CompareAsync(ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "Không kết nối được DB", detail: ex.Message);
        }
    }

    /// <summary>
    /// Đồng bộ (dryRun = true: chỉ xem trước). Mode IfEmpty: chỉ nạp bảng đích đang trống, không đè cấu hình API đã chỉnh bên đích;
    /// Replace: thay toàn bộ — phải gửi confirmTarget = tên database đích. migrate = true: cho phép chạy EF migration trên DB đích.
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType<DbSyncRunResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DbSyncRunResult>> Run([FromBody] DbSyncRunRequest request)
    {
        if (!_runner.Enabled) return NotEnabled();
        if (request.Mode == DbImportMode.Replace && !request.DryRun
            && !string.Equals(request.ConfirmTarget?.Trim(), _runner.TargetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(request.ConfirmTarget)] = new[] { $"Chế độ Replace ghi đè dữ liệu DB đích — gõ đúng tên database đích ({_runner.TargetDatabase}) để xác nhận." }
            }));
        }
        var user = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.Identity?.Name ?? "admin";
        try
        {
            return Ok(await _runner.RunAsync(request.Mode, request.DryRun, request.Migrate, user));
        }
        catch (DbSyncBusyException ex)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Đang đồng bộ", detail: ex.Message);
        }
    }

    private ObjectResult NotEnabled() =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: "Chưa cấu hình",
            detail: "Chưa cấu hình DbSync:TargetConnectionString trên API — tính năng đồng bộ DB Dev đang tắt.");
}

public sealed record DbSyncStatus(
    bool Enabled, string? Source, string? Target, string? TargetDatabase, IReadOnlyList<string> Tables, bool AllowMigrate, bool Running,
    DbSyncRunResult? LastRun);

public sealed class DbSyncRunRequest
{
    /// <summary>IfEmpty (mặc định) hoặc Replace.</summary>
    [EnumDataType(typeof(DbImportMode))]
    public DbImportMode Mode { get; set; } = DbImportMode.IfEmpty;
    /// <summary>true = chỉ xem trước, không ghi.</summary>
    public bool DryRun { get; set; }
    /// <summary>Cho phép chạy EF migration trên DB đích (DB mới / DB cũ hơn).</summary>
    public bool Migrate { get; set; }
    /// <summary>Bắt buộc khi Mode = Replace và không phải xem trước: tên database đích.</summary>
    [MaxLength(128)]
    public string? ConfirmTarget { get; set; }
}
