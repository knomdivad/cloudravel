using System.Net;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using CloudRavel.Tests.BotMonitor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using InventorySnapshot = CloudRavel.Core.Models.InventorySnapshot;

namespace CloudRavel.Tests.Auth;

public sealed class FeatureEntitlementGateTests
{
    [Fact]
    public async Task Bot_only_user_blocked_from_inventory()
    {
        var inventory = BuildInventoryFunctions();
        var ctx = PortalContext(botOnly: true, cloudOnly: false);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/inventory/resources");
        ctx.SetRequest(req);

        var res = await inventory.GetResources(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_only_user_blocked_from_portal_bots()
    {
        var fn = BuildBotMonitorFunctions();
        var ctx = PortalContext(botOnly: false, cloudOnly: true);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Dual_entitled_user_reaches_portal_bots()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.ListBotsForTenantAsync(Arg.Any<Guid>(), Arg.Any<Guid?>()).Returns(Array.Empty<(Core.BotMonitor.BotMonitorBot, Core.BotMonitor.BotMonitorBotState?, string?)>());

        var fn = BuildBotMonitorFunctions(repo);
        var ctx = PortalContext(botOnly: true, cloudOnly: true);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Dual_entitled_user_reaches_inventory()
    {
        var inventoryRepo = Substitute.For<IInventoryRepository>();
        inventoryRepo.GetResourcesAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<InventoryResource>());
        inventoryRepo.GetResourceCountAsync(Arg.Any<Guid>(), Arg.Any<long?>()).Returns(0);
        inventoryRepo.GetLatestSnapshotAsync(Arg.Any<Guid>()).Returns((InventorySnapshot?)null);

        var inventory = BuildInventoryFunctions(inventoryRepo);
        var ctx = PortalContext(botOnly: true, cloudOnly: true);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/inventory/resources");
        ctx.SetRequest(req);

        var res = await inventory.GetResources(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task User_with_no_entitlements_blocked_from_portal_bots()
    {
        var fn = BuildBotMonitorFunctions();
        var ctx = PortalContext(botOnly: false, cloudOnly: false);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Cross_user_within_tenant_cannot_read_another_users_bot()
    {
        var tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var owner = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var other = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var botId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByIdAsync(tenant, botId).Returns(new Core.BotMonitor.BotMonitorBot
        {
            BotId = botId,
            TenantId = tenant,
            OwnerUserId = owner,
            DisplayName = "owned",
        });

        var fn = BuildBotMonitorFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = tenant;
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        ctx.SetUser(other);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { tenant.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/portal/bots/{botId}");
        ctx.SetRequest(req);

        var res = await fn.PortalGetBot(req, botId.ToString(), ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Ops_fleet_user_bots_requires_system_admin()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetTenantAsync(Arg.Any<Guid>()).Returns(new Core.BotMonitor.BotMonitorTenant { TenantId = Guid.NewGuid(), Name = "t" });
        repo.ListBotsForTenantUserAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Array.Empty<(Core.BotMonitor.BotMonitorBot, Core.BotMonitor.BotMonitorBotState?, string?)>());

        var fn = BuildBotMonitorFunctions(repo);
        var ctx = PortalContext(botOnly: true, cloudOnly: true);
        var tenant = (Guid)ctx.Items["TenantId"]!;
        var user = ctx.CredentialUserId;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { Guid.Empty.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/ops/fleet/tenants/{tenant}/users/{user}/bots");
        ctx.SetRequest(req);

        var res = await fn.OpsFleetUserBots(req, tenant.ToString(), user.ToString(), ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private static TestFunctionContext PortalContext(bool botOnly, bool cloudOnly)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(new FeatureEntitlements(botOnly, cloudOnly));
        ctx.SetUser(ctx.CredentialUserId);
        return ctx;
    }

    private static BotMonitorFunctions BuildBotMonitorFunctions(IBotMonitorRepository? repo = null)
    {
        repo ??= Substitute.For<IBotMonitorRepository>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var cred = BotMonitorTestConfig.Credentials();
        var orgs = Substitute.For<IOrganizationRepository>();
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        return new BotMonitorFunctions(repo, orgs, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }

    private static InventoryFunctions BuildInventoryFunctions(IInventoryRepository? inventoryRepo = null)
    {
        inventoryRepo ??= Substitute.For<IInventoryRepository>();
        var collection = Substitute.For<IInventoryCollectionService>();
        var cloudAccount = Substitute.For<ICloudAccountRepository>();
        var cloudOrg = Substitute.For<ICloudOrgRepository>();
        var platform = Substitute.For<IPlatformInfo>();
        return new InventoryFunctions(inventoryRepo, collection, cloudAccount, cloudOrg, platform, NullLogger<InventoryFunctions>.Instance);
    }
}
