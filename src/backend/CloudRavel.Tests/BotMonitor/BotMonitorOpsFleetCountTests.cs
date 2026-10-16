using System.Net;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorOpsFleetCountTests
{
    [Fact]
    public async Task Fleet_users_bot_counts_sum_matches_tenant_total()
    {
        var tenantId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetTenantAsync(tenantId).Returns(new BotMonitorTenant { TenantId = tenantId, Name = "Acme" });
        repo.ListFleetTenantSummariesAsync(Arg.Any<DateTime>()).Returns([
            (tenantId, "Acme", 3, 2),
        ]);
        repo.ListFleetUsersForTenantAsync(tenantId).Returns([
            (user1, "Alice", "a@acme", 2),
            (user2, "Bob", "b@acme", 1),
        ]);

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.Items["TenantId"] = Guid.Empty;
        var fleetReq = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { Guid.Empty.ToString() },
            url: "http://localhost:7071/api/bot-monitor/ops/fleet");
        ctx.SetRequest(fleetReq);
        var fleetRes = await fn.OpsFleet(fleetReq, ctx);
        Assert.Equal(HttpStatusCode.OK, fleetRes.StatusCode);

        var usersReq = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { Guid.Empty.ToString() },
            url: $"http://localhost:7071/api/bot-monitor/ops/fleet/tenants/{tenantId}/users");
        ctx.SetRequest(usersReq);
        var usersRes = await fn.OpsFleetUsers(usersReq, tenantId.ToString(), ctx);
        Assert.Equal(HttpStatusCode.OK, usersRes.StatusCode);

        var tenants = await repo.ListFleetTenantSummariesAsync(DateTime.UtcNow);
        var users = await repo.ListFleetUsersForTenantAsync(tenantId);
        Assert.Equal(3, tenants[0].BotCount);
        Assert.Equal(tenants[0].BotCount, users.Sum(u => u.BotCount));
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
