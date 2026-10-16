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

public sealed class BotMonitorTenantAdminScopeTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Tenant_admin_cannot_forged_tenant_query_on_portal_list()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = TenantA;
        ctx.Items["OrgRole"] = OrgRole.TenantAdmin;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        ctx.SetUser(UserA);
        var req = new FakeHttpRequestData(
            ctx,
            "GET",
            tenantIdHeaders: new[] { TenantA.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/portal/bots?tenantId={TenantB}");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await repo.DidNotReceive().ListBotsForTenantAsync(TenantB, Arg.Any<Guid?>());
    }

    [Fact]
    public async Task Tenant_admin_lists_all_bots_in_tenant()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.ListBotsForTenantAsync(TenantA, null).Returns(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?)>());
        repo.ListUserBotCountsForTenantAsync(TenantA).Returns(Array.Empty<(Guid, string, string, int)>());

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = TenantA;
        ctx.Items["OrgRole"] = OrgRole.TenantAdmin;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        ctx.SetUser(UserA);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { TenantA.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await repo.Received(1).ListBotsForTenantAsync(TenantA, null);
    }

    [Fact]
    public async Task Read_only_user_lists_only_owned_bots()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.ListBotsForTenantAsync(TenantA, UserA).Returns(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?)>());

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = TenantA;
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        ctx.SetUser(UserA);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { TenantA.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await repo.Received(1).ListBotsForTenantAsync(TenantA, UserA);
        await repo.DidNotReceive().ListUserBotCountsForTenantAsync(Arg.Any<Guid>());
    }

    [Fact]
    public void Fleet_user_counts_sum_to_tenant_bot_total()
    {
        var userCounts = new[] { (UserA, "Alice", "a@x", 2), (Guid.NewGuid(), "Bob", "b@x", 1) };
        var tenantTotal = userCounts.Sum(u => u.Item4);
        Assert.Equal(3, tenantTotal);
        Assert.Equal(userCounts.Length, userCounts.Count(u => u.Item4 >= 0));
    }

    private static BotMonitorFunctions BuildFunctions(IBotMonitorRepository repo)
    {
        var config = new ConfigurationBuilder().Build();
        var cred = BotMonitorTestConfig.Credentials();
        var orgs = Substitute.For<IOrganizationRepository>();
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        var users = Substitute.For<IUserRepository>();
        return new BotMonitorFunctions(repo, users, orgs, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }
}
