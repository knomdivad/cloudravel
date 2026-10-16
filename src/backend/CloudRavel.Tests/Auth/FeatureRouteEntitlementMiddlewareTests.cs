using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class FeatureRouteEntitlementMiddlewareTests
{
    [Fact]
    public async Task Unmapped_route_is_denied()
    {
        var (ctx, req) = ContextFor("/api/unknown/surface", cloud: true, bot: true);
        var nextCalled = false;
        var middleware = new FeatureRouteEntitlementMiddleware();

        await middleware.Invoke(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        var response = ((Assessment.TestFunctionContext)ctx).GetInvocationResult();
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.Forbidden, response!.StatusCode);
    }

    [Fact]
    public async Task Get_tenants_allowed_without_cloud_entitlement()
    {
        var (ctx, _) = ContextFor("/api/tenants", cloud: false, bot: true);
        var middleware = new FeatureRouteEntitlementMiddleware();
        var nextCalled = false;
        await middleware.Invoke(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Post_tenants_blocked_without_cloud_entitlement()
    {
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetFeatureEntitlements(new FeatureEntitlements(true, false));
        var req = new FakeHttpRequestData(ctx, "POST", tenantIdHeaders: [Guid.NewGuid().ToString()],
            url: "http://localhost:7071/api/tenants");
        ctx.SetRequest(req);
        var nextCalled = false;
        var middleware = new FeatureRouteEntitlementMiddleware();
        await middleware.Invoke(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/api/inventory/resources")]
    [InlineData("/api/bot-monitor/portal/bots")]
    public async Task Middleware_blocks_when_feature_entitlements_are_off(string path)
    {
        var (ctx, req) = ContextFor(path, cloud: false, bot: false);
        var nextCalled = false;
        var middleware = new FeatureRouteEntitlementMiddleware();

        await middleware.Invoke(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/api/inventory/resources")]
    [InlineData("/api/changes/recent")]
    [InlineData("/api/recommendations/advisor")]
    [InlineData("/api/ai/query")]
    [InlineData("/api/ai/chat")]
    [InlineData("/api/anomalies")]
    [InlineData("/api/incidents/1")]
    [InlineData("/api/remediations")]
    [InlineData("/api/cloud-orgs")]
    [InlineData("/api/cloud-accounts")]
    [InlineData("/api/operations/summary")]
    public async Task Cloud_routes_require_cloud_monitoring(string path)
    {
        var (ctx, _) = ContextFor(path, cloud: false, bot: true);
        var middleware = new FeatureRouteEntitlementMiddleware();
        var nextCalled = false;
        await middleware.Invoke(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/api/bot-monitor/portal/bots")]
    [InlineData("/api/bot-monitor/portal/bot-types")]
    [InlineData("/api/bot-monitor/ops/fleet")]
    [InlineData("/api/bot-monitor/registry/bots")]
    [InlineData("/api/bot-monitor/portal/magic-link")]
    public async Task Bot_routes_require_bot_monitoring(string path)
    {
        var (ctx, _) = ContextFor(path, cloud: true, bot: false);
        var middleware = new FeatureRouteEntitlementMiddleware();
        var nextCalled = false;
        await middleware.Invoke(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.False(nextCalled);
    }

    private static (FunctionContext Ctx, FakeHttpRequestData Req) ContextFor(string path, bool cloud, bool bot)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetFeatureEntitlements(new FeatureEntitlements(bot, cloud));
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [Guid.NewGuid().ToString()], url: "http://localhost:7071" + path);
        ctx.SetRequest(req);
        return (ctx, req);
    }
}
