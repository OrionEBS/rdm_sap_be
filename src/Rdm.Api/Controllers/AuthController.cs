using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Rdm.Api.Auth;
using Rdm.Application.Auth;

namespace Rdm.Api.Controllers;

/// <summary>
/// Chứng thực JWT. Lấy token qua POST /api/auth/token rồi gửi header "Authorization: Bearer &lt;token&gt;" cho mọi API khác.
/// Token hết hạn sau Jwt:ExpiryMinutes (mặc định 60 phút) → gọi lại /api/auth/token.
/// </summary>
[ApiController]
[Route("api/auth")]
[Rdm.Api.ApiCatalog.AlwaysActive]   // đăng nhập luôn active — không tắt được qua bảng cấu hình API
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly JwtTokenService _tokens;
    private readonly IApiUserRepository _users;

    public AuthController(AuthService auth, JwtTokenService tokens, IApiUserRepository users)
    {
        _auth = auth;
        _tokens = tokens;
        _users = users;
    }

    public sealed class TokenRequest
    {
        /// <example>admin</example>
        [Required, StringLength(64)]
        public string Username { get; set; } = string.Empty;

        [Required, StringLength(256)]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class SaveUserRequest
    {
        /// <summary>Bỏ trống khi cập nhật = giữ mật khẩu cũ. Tối thiểu 8 ký tự.</summary>
        [StringLength(256)]
        public string? Password { get; set; }

        /// <summary>Admin hoặc User.</summary>
        /// <example>User</example>
        [Required]
        public string Role { get; set; } = ApiRoles.User;

        public bool IsActive { get; set; } = true;
    }

    /// <summary>Đăng nhập lấy access token (không cần token).</summary>
    [AllowAnonymous]
    [HttpPost("token")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Token([FromBody] TokenRequest request, CancellationToken ct)
    {
        var user = await _auth.ValidateAsync(request.Username, request.Password, ct);
        if (user is null)
            return Problem(title: "Đăng nhập thất bại", detail: "Sai username / mật khẩu hoặc tài khoản bị khóa.",
                statusCode: StatusCodes.Status401Unauthorized);
        return Ok(_tokens.Create(user));
    }

    /// <summary>Thông tin tài khoản của token hiện tại.</summary>
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        Username = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
        Role = User.FindFirst(JwtTokenService.RoleClaim)?.Value,
        ExpiresAtUtc = long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : (DateTime?)null
    });

    /// <summary>Danh sách tài khoản (chỉ Admin).</summary>
    [Authorize(Roles = ApiRoles.Admin)]
    [HttpGet("users")]
    [Rdm.Api.ApiCatalog.InternalDoc]   // quản lý tài khoản: không hiện trong tài liệu đối tác
    public async Task<IEnumerable<ApiUserInfo>> ListUsers(CancellationToken ct) =>
        (await _users.ListAsync(ct)).Select(ApiUserInfo.From);

    /// <summary>Tạo / cập nhật tài khoản: đổi mật khẩu, role, khóa / mở khóa (chỉ Admin).</summary>
    [Authorize(Roles = ApiRoles.Admin)]
    [HttpPut("users/{username}")]
    [Rdm.Api.ApiCatalog.InternalDoc]
    [ProducesResponseType<ApiUserInfo>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ApiUserInfo> SaveUser(string username, [FromBody] SaveUserRequest request, CancellationToken ct) =>
        await _auth.SaveUserAsync(username, string.IsNullOrEmpty(request.Password) ? null : request.Password,
            request.Role, request.IsActive, ct);
}
