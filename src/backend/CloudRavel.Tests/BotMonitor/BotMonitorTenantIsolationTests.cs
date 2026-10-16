using CloudRavel.Api.Functions;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorTenantIsolationTests
{
    [Fact]
    public async Task Portal_list_rejects_forged_tenant_query()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var repo = Substitute.For<IBotMonitorRepository>();
        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = tenantA;
        ctx.Items["OrgRole"] = "read_only";
        ctx.SetUser(ctx.CredentialUserId);
        var req = new FakeHttpRequestData(
            ctx,
            "GET",
            tenantIdHeaders: new[] { tenantA.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/portal/bots?tenantId={tenantB}");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, res.StatusCode);
        await repo.DidNotReceive().ListBotsForTenantAsync(tenantB);
    }

    [Fact]
    public async Task Portal_get_by_id_hides_bot_outside_session_tenant()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var botInB = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByIdAsync(tenantA, botInB).Returns((BotMonitorBot?)null);

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = tenantA;
        ctx.Items["OrgRole"] = "read_only";
        ctx.SetUser(ctx.CredentialUserId);
        var req = new FakeHttpRequestData(
            ctx,
            "GET",
            tenantIdHeaders: new[] { tenantA.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/portal/bots/{botInB}");
        ctx.SetRequest(req);

        var res = await fn.PortalGetBot(req, botInB.ToString(), ctx);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }

    private static BotMonitorFunctions BuildFunctions(IBotMonitorRepository repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var cred = new BotMonitorCredentialService(config);
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        return new BotMonitorFunctions(repo, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }
}
