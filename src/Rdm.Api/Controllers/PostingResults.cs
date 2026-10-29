using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Postings;

namespace Rdm.Api.Controllers;

/// <summary>
/// Map trạng thái posting → HTTP status. Body luôn là <see cref="PostingResponse"/> để client đọc message SAP nguyên văn.
///   200 Succeeded (kể cả Replayed) · 422 lỗi nghiệp vụ SAP · 503 không kết nối/đăng nhập được SAP (gửi lại được)
///   502 Unknown (KHÔNG gửi lại khi chưa kiểm tra SAP) · 500 lỗi nội bộ.
/// </summary>
internal static class PostingResults
{
    public static IActionResult ToActionResult(this PostingResponse r)
    {
        var status = r.Status switch
        {
            PostingStatus.Succeeded => StatusCodes.Status200OK,
            PostingStatus.Unknown => StatusCodes.Status502BadGateway,
            PostingStatus.Failed => r.ErrorCode switch
            {
                SapErrorCodes.Unavailable or SapErrorCodes.Logon => StatusCodes.Status503ServiceUnavailable,
                SapErrorCodes.Internal => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status422UnprocessableEntity
            },
            _ => StatusCodes.Status500InternalServerError
        };
        return new ObjectResult(r) { StatusCode = status };
    }
}
