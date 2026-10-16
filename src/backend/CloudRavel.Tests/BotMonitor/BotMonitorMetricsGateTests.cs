using System.Net;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorMetricsGateTests
{
    [Fact]
    public async Task Portal_metrics_requires_bot_entitlement()
    {
        var fn = BuildFunctions();
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(new FeatureEntitlements(false, true));
        ctx.SetUser(ctx.CredentialUserId);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/metrics");
        ctx.SetRequest(req);

        var res = await fn.PortalMetrics(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Ops_metrics_requires_system_admin()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetTenantAsync(Arg.Any<Guid>()).Returns(new BotMonitorTenant { TenantId = Guid.NewGuid(), Name = "t" });
        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = Guid.Empty;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { Guid.Empty.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/ops/metrics?tenantId={tenant}&userId={user}");
        ctx.SetRequest(req);

        var res = await fn.OpsMetrics(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private static BotMonitorFunctions BuildFunctions(IBotMonitorRepository? repo = null)
    {
        repo ??= Substitute.For<IBotMonitorRepository>();
        var config = new ConfigurationBuilder().Build();
        var cred = BotMonitorTestConfig.Credentials();
        var orgs = Substitute.For<IOrganizationRepository>();
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        var users = Substitute.For<IUserRepository>();
        return new BotMonitorFunctions(repo, users, orgs, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }
}
