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

public sealed class BotMonitorOpsFleetGateTests
{
    [Fact]
    public async Task System_admin_can_drill_fleet_to_user_bots()
    {
        var tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var user = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetTenantAsync(tenant).Returns(new BotMonitorTenant { TenantId = tenant, Name = "Acme" });
        repo.ListBotsForTenantUserAsync(tenant, user).Returns(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?)>());

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { Guid.Empty.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/ops/fleet/tenants/{tenant}/users/{user}/bots");
        ctx.SetRequest(req);

        var res = await fn.OpsFleetUserBots(req, tenant.ToString(), user.ToString(), ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static BotMonitorFunctions BuildFunctions(IBotMonitorRepository repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var cred = BotMonitorTestConfig.Credentials();
        var orgs = Substitute.For<IOrganizationRepository>();
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        return new BotMonitorFunctions(repo, orgs, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }
}
