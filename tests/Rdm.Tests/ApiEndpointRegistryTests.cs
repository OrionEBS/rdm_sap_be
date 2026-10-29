using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Rdm.Api.ApiCatalog;
using Rdm.Application.ApiCatalog;

namespace Rdm.Tests;

public class ApiEndpointRegistryTests
{
    private static ApiEndpointSetting Row(string method, string route, bool active, bool locked = false) => new()
    {
        EndpointKey = ApiEndpointRegistry.Key(method, route), HttpMethod = method, RouteTemplate = route,
        GroupName = "PricesBapi", ActionName = "X", IsActive = active, IsLocked = locked
    };

    [Fact]
    public void Inactive_endpoint_is_hidden_others_and_unknown_stay_active()
    {
        var r = new ApiEndpointRegistry();
        r.Load(new[]
        {
            Row("POST", "api/sd/prices-bapi/jobs/create", active: false),
            Row("POST", "api/sd/prices-bapi", active: true)
        }, DateTime.UtcNow);

        r.IsActive("post", "api/sd/prices-bapi/jobs/create").Should().BeFalse();   // không phân biệt hoa thường method
        r.IsActive("POST", "api/sd/prices-bapi").Should().BeTrue();
        r.IsActive("GET", "api/sd/prices/new-endpoint").Should().BeTrue();          // chưa có trong bảng → active
        r.InactiveCount.Should().Be(1);
    }

    [Fact]
    public void Locked_endpoint_is_always_active_even_if_flag_is_off()
    {
        var r = new ApiEndpointRegistry();
        r.Load(new[] { Row("POST", "api/auth/token", active: false, locked: true) }, DateTime.UtcNow);

        r.IsActive("POST", "api/auth/token").Should().BeTrue();
    }

    [Fact]
    public void Wrong_method_on_fully_inactive_route_is_reported_as_missing()
    {
        // Mọi method của route đều inactive → GET (routing trả 405) cũng phải thành 404, không lộ route tồn tại.
        var r = new ApiEndpointRegistry();
        r.Load(new[]
        {
            Row("POST", "api/sd/prices-bapi/jobs/create", active: false),
            Row("GET", "api/sd/prices-bapi/jobs/{operation}/{requestId}", active: false),
            Row("POST", "api/sd/prices-bapi/change", active: false),
            Row("PUT", "api/sd/prices-bapi/change", active: true)
        }, DateTime.UtcNow);

        r.IsFullyInactivePath(new PathString("/api/sd/prices-bapi/jobs/create")).Should().BeTrue();
        r.IsFullyInactivePath(new PathString("/api/sd/prices-bapi/jobs/create/REQ-1")).Should().BeTrue();
        r.IsFullyInactivePath(new PathString("/api/sd/prices-bapi/change")).Should().BeFalse();   // còn PUT active → giữ 405
    }

    [Fact]
    public void Reload_reactivates_endpoints()
    {
        var r = new ApiEndpointRegistry();
        r.Load(new[] { Row("POST", "api/fi/fb01", active: false) }, DateTime.UtcNow);
        r.Load(new[] { Row("POST", "api/fi/fb01", active: true) }, DateTime.UtcNow);

        r.IsActive("POST", "api/fi/fb01").Should().BeTrue();
        r.IsFullyInactivePath(new PathString("/api/fi/fb01")).Should().BeFalse();
    }
}
