using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Tests.Assessment;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class TenantListCloudGateTests
{
    [Fact]
    public async Task Get_tenants_requires_cloud_monitoring_after_org_intersection()
    {
        var (ctx, _) = ContextFor("/api/tenants", cloud: false, bot: true);
        var middleware = new FeatureRouteEntitlementMiddleware();
        var nextCalled = false;
        await middleware.Invoke(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/api/tenants")]
    [InlineData("/api/tenants/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public void Tenant_get_routes_are_cloud_mapped(string path)
    {
        var requirement = RouteEntitlementCatalog.ResolveRequirement(path, "GET");
        Assert.Equal(RouteEntitlementCatalog.RequirementCloudMonitoring, requirement);
    }

    private static (Assessment.TestFunctionContext Ctx, FakeHttpRequestData Req) ContextFor(string path, bool cloud, bool bot)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetFeatureEntitlements(new FeatureEntitlements(bot, cloud));
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [Guid.NewGuid().ToString()],
            url: "http://localhost:7071" + path);
        ctx.SetRequest(req);
        return (ctx, req);
    }
}
