using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Rdm.Application.Auth;

public static class ApiRoles
{
    /// <summary>Gọi mọi API + quản trị tài khoản.</summary>
    public const string Admin = "Admin";
    /// <summary>Gọi mọi API nghiệp vụ.</summary>
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = new[] { Admin, User };
}

/// <summary>Tài khoản gọi API (rdm.ApiUsers). Mật khẩu lưu dạng băm PBKDF2.</summary>
public sealed class ApiUser
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = ApiRoles.User;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

public interface IApiUserRepository
{
    Task<ApiUser?> FindAsync(string username, CancellationToken ct);
    Task<List<ApiUser>> ListAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    /// <summary>Tạo / cập nhật. passwordHash = null → giữ mật khẩu cũ.</summary>
    Task UpsertAsync(string username, string? passwordHash, string role, bool isActive, DateTime nowUtc, CancellationToken ct);
    Task SetLastLoginAsync(string username, DateTime nowUtc, CancellationToken ct);
}

public sealed record ApiUserInfo(string Username, string Role, bool IsActive, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? LastLoginAtUtc)
{
    public static ApiUserInfo From(ApiUser u) => new(u.Username, u.Role, u.IsActive, u.CreatedAtUtc, u.UpdatedAtUtc, u.LastLoginAtUtc);
}

public sealed class AuthService
{
    public const int MinPasswordLength = 8;

    private readonly IApiUserRepository _users;
    private readonly IPasswordHasher<ApiUser> _hasher;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IApiUserRepository users, IPasswordHasher<ApiUser> hasher, TimeProvider clock, ILogger<AuthService> logger)
    {
        _users = users;
        _hasher = hasher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Kiểm tra username/password. null nếu sai, không tồn tại hoặc bị khóa (không phân biệt để tránh dò tài khoản).</summary>
    public async Task<ApiUser?> ValidateAsync(string username, string password, CancellationToken ct)
    {
        var user = await _users.FindAsync(username.Trim(), ct);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Đăng nhập thất bại: {Username} không tồn tại hoặc bị khóa", username);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Đăng nhập thất bại: sai mật khẩu cho {Username}", user.Username);
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            await _users.UpsertAsync(user.Username, _hasher.HashPassword(user, password), user.Role, user.IsActive, now, ct);
        await _users.SetLastLoginAsync(user.Username, now, ct);
        user.LastLoginAtUtc = now;
        return user;
    }

    /// <summary>Tạo hoặc cập nhật tài khoản. password = null → giữ mật khẩu cũ (bắt buộc khi tạo mới).</summary>
    public async Task<ApiUserInfo> SaveUserAsync(string username, string? password, string role, bool isActive, CancellationToken ct)
    {
        username = username.Trim();
        if (username.Length is 0 or > 64) throw new ArgumentException("Username phải từ 1 đến 64 ký tự.");
        var normalizedRole = ApiRoles.All.FirstOrDefault(r => r.Equals(role, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException($"Role phải là một trong: {string.Join(", ", ApiRoles.All)}.");

        var existing = await _users.FindAsync(username, ct);
        if (existing is null && string.IsNullOrEmpty(password))
            throw new ArgumentException("Tạo tài khoản mới phải có mật khẩu.");
        if (password is not null && password.Length < MinPasswordLength)
            throw new ArgumentException($"Mật khẩu phải có ít nhất {MinPasswordLength} ký tự.");

        var hash = password is null ? null : _hasher.HashPassword(existing ?? new ApiUser { Username = username }, password);
        await _users.UpsertAsync(existing?.Username ?? username, hash, normalizedRole, isActive, _clock.GetUtcNow().UtcDateTime, ct);
        _logger.LogInformation("Lưu tài khoản API {Username} role={Role} active={IsActive} đổi mật khẩu={PasswordChanged}",
            username, normalizedRole, isActive, password is not null);

        return ApiUserInfo.From((await _users.FindAsync(username, ct))!);
    }

    /// <summary>Lần chạy đầu (chưa có tài khoản nào): tạo admin từ cấu hình Auth:BootstrapAdmin.</summary>
    public async Task EnsureBootstrapAdminAsync(string? username, string? password, CancellationToken ct)
    {
        if (await _users.CountAsync(ct) > 0) return;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning("Chưa có tài khoản API nào và chưa cấu hình Auth:BootstrapAdmin — không ai gọi được API.");
            return;
        }
        await SaveUserAsync(username, password, ApiRoles.Admin, isActive: true, ct);
        _logger.LogWarning("Đã tạo tài khoản admin khởi tạo {Username} — hãy đổi mật khẩu.", username);
    }
}
