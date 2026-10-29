using System.Diagnostics;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.JsonWebTokens;
using Rdm.Application.Abstractions;

namespace Rdm.Api.SapCallLogging;

/// <summary>
/// Gắn ngữ cảnh request vào mọi lời gọi SAP phát sinh trong request (nhật ký rdm.SapCallLog):
/// X-Correlation-Id (hoặc X-Request-Id) do EU gửi — không có thì dùng trace id; user (JWT sub), IP, method + path.
/// Luôn trả lại header X-Correlation-Id để EU lưu và đối soát.
/// </summary>
public static partial class SapCallContextMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const string AltHeaderName = "X-Request-Id";

    public static IApplicationBuilder UseSapCallContext(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
            var correlation = Clean(ctx.Request.Headers[HeaderName].FirstOrDefault())
                              ?? Clean(ctx.Request.Headers[AltHeaderName].FirstOrDefault())
                              ?? traceId;
            ctx.Response.Headers[HeaderName] = correlation;

            var user = ctx.User.Identity?.IsAuthenticated == true
                ? ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ctx.User.Identity.Name
                : null;

            using var _ = SapCallContext.Begin(new SapCallContext
            {
                Source = "http",
                CorrelationId = correlation,
                TraceId = traceId,
                UserName = user,
                ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
                HttpMethod = ctx.Request.Method,
                Path = ctx.Request.Path.Value
            });
            await next();
        });

    /// <summary>Chỉ nhận chữ, số và . _ - : (tối đa 100 ký tự) — giá trị lạ bị bỏ, dùng trace id.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length <= 100 && SafeId().IsMatch(v) ? v : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+$")]
    private static partial Regex SafeId();
}
