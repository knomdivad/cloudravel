using System.Net;
using System.Text.Json;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Infrastructure.Data;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

[Trait("Category", "BotMonitorSqlIntegration")]
public sealed class BotMonitorSqlIntegrationTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("CLOUDRAVEL_INTEGRATION_SQL_CONNECTION");

    private static BotMonitorRepository CreateRepository()
    {
        var cs = ConnectionString ?? throw new InvalidOperationException("CLOUDRAVEL_INTEGRATION_SQL_CONNECTION is required.");
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CloudRavelDatabase"] = cs,
            })
            .Build();
        var factory = new TenantDbConnectionFactory(config, NullLogger<TenantDbConnectionFactory>.Instance);
        return new BotMonitorRepository(factory);
    }

    [Fact]
    public async Task ListAllBotsAsync_returns_tenant_name_and_state_when_bot_exists()
    {
        var repo = CreateRepository();
        var rows = await repo.ListAllBotsAsync(null);
        Assert.NotEmpty(rows);
        var row = rows.First(r => r.Bot.DisplayName.Contains("integration", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(row.TenantName));
        Assert.NotNull(row.State);
        Assert.Equal("pass", row.LastKeepCheck);
    }

    [Fact]
    public async Task ListBotsForTenantAsync_and_CountBotsByTypeAsync_match_fleet_queries()
    {
        var repo = CreateRepository();
        var all = await repo.ListAllBotsAsync(null);
        var tenantId = all[0].Bot.TenantId;

        var tenantBots = await repo.ListBotsForTenantAsync(tenantId);
        Assert.NotEmpty(tenantBots);

        var typeRows = await repo.CountBotsByTypeAsync(tenantId, null, null);
        Assert.True(typeRows.Sum(x => x.Count) >= tenantBots.Count);

        var fleetTypes = await repo.CountBotsByTypeAsync(null, null, null);
        Assert.True(fleetTypes.Sum(x => x.Count) >= all.Count);
    }

    [Fact]
    public async Task OpsListBots_returns_200_json_when_sql_has_bots()
    {
        var repo = CreateRepository();
        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.Items["TenantId"] = Guid.Empty;
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [Guid.Empty.ToString()],
            url: "http://localhost:7071/api/bot-monitor/ops/bots");
        ctx.SetRequest(req);

        var response = await fn.OpsListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await FakeHttpResponseDataExtensions.ReadBodyAsync(response);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("bots").GetArrayLength() >= 1);
        Assert.True(doc.RootElement.GetProperty("typeCounts").GetArrayLength() >= 1);
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
