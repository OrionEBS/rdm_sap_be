using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rdm.Application.Auth;

namespace Rdm.Api.Auth;

/// <summary>Cấu hình JWT (section "Jwt").</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "Rdm.Api";
    public string Audience { get; set; } = "Rdm.Api.Clients";
    /// <summary>Khóa ký HMAC-SHA256, tối thiểu 32 byte. Production đặt qua biến môi trường Jwt__SigningKey.</summary>
    public string SigningKey { get; set; } = string.Empty;
    /// <summary>Thời hạn token (phút). Hết hạn phải gọi lại POST /api/auth/token.</summary>
    public int ExpiryMinutes { get; set; } = 60;

    public SymmetricSecurityKey CreateKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey ?? string.Empty);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey phải có ít nhất 32 byte (đặt qua cấu hình hoặc biến môi trường Jwt__SigningKey).");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTime ExpiresAtUtc, string Username, string Role);

public sealed class JwtTokenService
{
    public const string RoleClaim = "role";

    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly TimeProvider _clock;

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _credentials = new SigningCredentials(_options.CreateKey(), SecurityAlgorithms.HmacSha256);
        _clock = clock;
    }

    public TokenResponse Create(ApiUser user)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(Math.Max(1, _options.ExpiryMinutes));
        var expires = now.Add(lifetime);

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(RoleClaim, user.Role)
            })
        });

        return new TokenResponse(token, "Bearer", (int)lifetime.TotalSeconds, expires, user.Username, user.Role);
    }
}
