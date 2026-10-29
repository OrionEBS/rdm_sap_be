using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Rdm.Application.Abstractions;
using Rdm.Application.Customers;

namespace Rdm.Api.Controllers;

/// <summary>
/// Hạn mức tín dụng khách hàng — KNKK qua hàm RFC Z_ESD017_READ_KNKK (Sap:CustomerCreditFunction; kiểm tra quyền F_KNKA_KKB,
/// số tiền giá trị thực). Sap:CustomerCreditSource = Auto (mặc định): hàm chưa có trên SAP thì đọc RFC_READ_TABLE như cũ.
/// </summary>
[ApiController]
[Route("api/customer-credits")]
[Produces("application/json")]
public sealed class CustomerCreditsController : ControllerBase
{
    private readonly ISapMasterDataClient _sap;
    private readonly TimeProvider _clock;

    public CustomerCreditsController(ISapMasterDataClient sap, TimeProvider clock)
    {
        _sap = sap;
        _clock = clock;
    }

    /// <summary>Đọc KNKK của tất cả khách hàng.</summary>
    [HttpGet]
    [ProducesResponseType<CustomerCreditList>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<CustomerCreditList> GetAll(CancellationToken ct)
    {
        var items = await _sap.GetCustomerCreditsAsync(null, null, ct);
        return new CustomerCreditList(items.Count, _clock.GetUtcNow().UtcDateTime, items);
    }

    /// <summary>Đọc KNKK theo khách hàng (KUNNR) và credit control area (KKBER, bỏ trống = mọi credit control area).</summary>
    /// <param name="customer">KUNNR, vd. 100166 (tự thêm 0 đầu).</param>
    /// <param name="creditControlArea">KKBER, vd. G110.</param>
    /// <param name="ct"></param>
    [HttpGet("{customer}")]
    [ProducesResponseType<CustomerCreditList>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CustomerCreditList>> GetByCustomer(
        [StringLength(10, MinimumLength = 1)] string customer,
        [FromQuery, StringLength(4)] string? creditControlArea,
        CancellationToken ct)
    {
        var items = await _sap.GetCustomerCreditsAsync(customer, creditControlArea, ct);
        if (items.Count == 0)
            return Problem(
                title: "Không tìm thấy",
                detail: $"Không có KNKK cho khách hàng {customer}" +
                        (string.IsNullOrWhiteSpace(creditControlArea) ? "." : $", credit control area {creditControlArea}."),
                statusCode: StatusCodes.Status404NotFound);
        return new CustomerCreditList(items.Count, _clock.GetUtcNow().UtcDateTime, items);
    }
}
