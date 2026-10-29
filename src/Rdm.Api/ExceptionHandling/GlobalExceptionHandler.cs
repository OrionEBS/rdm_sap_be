using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;

namespace Rdm.Api.ExceptionHandling;

/// <summary>
/// Exception → ProblemDetails (RFC 7807).
///   PostingConflictException → 409 (kèm bản ghi posting hiện có) · SapReadException → 503 (kết nối/logon) hoặc 502 · còn lại → 500.
/// Lỗi phía SAP không đi qua đây — SapClient trả về trong PostingResponse.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        if (exception is PostingConflictException conflict)
        {
            _logger.LogWarning("409: {Message}", conflict.Message);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Posting conflict",
                Detail = conflict.Message
            };
            if (conflict.Existing is { } existing)
                problem.Extensions["posting"] = PostingResponse.From(existing);
        }
        else if (exception is Rdm.Application.MasterData.MasterDataSyncInProgressException busy)
        {
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Đang đồng bộ",
                Detail = busy.Message
            };
        }
        else if (exception is SapReadException read)
        {
            var unavailable = read.ErrorCode is SapErrorCodes.Unavailable or SapErrorCodes.Logon;
            var (status, title) = read.ErrorCode switch
            {
                Rdm.Application.Customers.CreditErrorCodes.CustomerNotFound or Rdm.Application.Customers.CreditErrorCodes.AreaNotFound
                    => (StatusCodes.Status404NotFound, "Không tìm thấy"),
                Rdm.Application.Customers.CreditErrorCodes.NoAuthority => (StatusCodes.Status403Forbidden, "Không có quyền trên SAP"),
                _ when unavailable => (StatusCodes.Status503ServiceUnavailable, "Không kết nối được SAP"),
                _ => (StatusCodes.Status502BadGateway, "SAP trả lỗi")
            };
            problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = read.Message
            };
            problem.Extensions["errorCode"] = read.ErrorCode;
            problem.Extensions["function"] = read.FunctionName;
        }
        else
        {
            _logger.LogError(exception, "Unhandled exception");
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error",
                Detail = exception.Message
            };
            problem.Extensions["exceptionType"] = exception.GetType().FullName;
            if (_env.IsDevelopment()) problem.Extensions["stackTrace"] = exception.ToString();
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
