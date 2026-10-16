using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorBotTypeTests
{
    [Fact]
    public async Task Registry_lists_and_creates_bot_types()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.ListBotTypesAsync(false).Returns([
            new BotMonitorBotType { TypeKey = "grokbot", DisplayName = "Grokbot", IsActive = true },
        ]);
        repo.GetBotTypeByKeyAsync("custom_agent").Returns((BotMonitorBotType?)null);
        repo.InsertBotTypeAsync("custom_agent", "Custom agent").Returns(
            new BotMonitorBotType { TypeKey = "custom_agent", DisplayName = "Custom agent", IsActive = true });

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        var listReq = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [Guid.Empty.ToString()],
            url: "http://localhost:7071/api/bot-monitor/registry/bot-types");
        ctx.SetRequest(listReq);
        var listRes = await fn.RegistryBotTypes(listReq, ctx);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        var body = JsonSerializer.Serialize(new { typeKey = "custom_agent", displayName = "Custom agent" });
        var createReq = new JsonPostRequest(ctx, "http://localhost:7071/api/bot-monitor/registry/bot-types", body);
        ctx.SetRequest(createReq);
        var createRes = await fn.RegistryBotTypes(createReq, ctx);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        await repo.Received(1).InsertBotTypeAsync("custom_agent", "Custom agent");
    }

    [Fact]
    public async Task Portal_bot_types_requires_bot_monitor_entitlement()
    {
        var repo = Substitute.For<IBotMonitorRepository>();
        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        var tenant = Guid.NewGuid();
        ctx.Items["TenantId"] = tenant;
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(new Core.Auth.FeatureEntitlements(false, true));
        ctx.SetUser(Guid.NewGuid());
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [tenant.ToString()],
            url: "http://localhost:7071/api/bot-monitor/portal/bot-types");
        ctx.SetRequest(req);

        var res = await fn.PortalBotTypes(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await repo.DidNotReceive().ListBotTypesAsync(Arg.Any<bool>());
    }

    [Fact]
    public async Task Portal_dashboard_type_counts_match_visible_bots()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var repo = Substitute.For<IBotMonitorRepository>();
        var grok = MakeBot("grokbot", "Grokbot");
        var chat = MakeBot("chatgpt", "ChatGPT");
        repo.ListBotsForTenantAsync(tenant, user, null).Returns([
            (grok, null, null),
            (chat, null, null),
        ]);
        repo.CountBotsByTypeAsync(tenant, user, null).Returns([
            ("grokbot", "Grokbot", 1),
            ("chatgpt", "ChatGPT", 1),
        ]);
        repo.GetWeeklyHistoryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>())
            .Returns(Array.Empty<BotMonitorWeeklyBucket>());
        repo.GetRecentCheckinsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>())
            .Returns(Array.Empty<(DateTime, string)>());
        repo.GetBotMetricsSnapshotAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns((BotMonitorHealthMetricsSnapshot?)null);

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = tenant;
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(Core.Auth.FeatureEntitlements.Both);
        ctx.SetUser(user);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [tenant.ToString()],
            url: "http://localhost:7071/api/bot-monitor/portal/bots");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var typeRows = await repo.CountBotsByTypeAsync(tenant, user, null);
        var listed = await repo.ListBotsForTenantAsync(tenant, user, null);
        Assert.Equal(listed.Count, typeRows.Sum(r => r.Count));
    }

    [Fact]
    public async Task Portal_list_passes_bot_type_filter_to_repository()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.ListBotsForTenantAsync(tenant, user, "grokbot").Returns([
            (MakeBot("grokbot", "Grokbot"), null, null),
        ]);
        repo.CountBotsByTypeAsync(tenant, user, "grokbot").Returns([("grokbot", "Grokbot", 1)]);
        repo.GetWeeklyHistoryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>())
            .Returns(Array.Empty<BotMonitorWeeklyBucket>());
        repo.GetRecentCheckinsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>())
            .Returns(Array.Empty<(DateTime, string)>());
        repo.GetBotMetricsSnapshotAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns((BotMonitorHealthMetricsSnapshot?)null);

        var fn = BuildFunctions(repo);
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = tenant;
        ctx.Items["OrgRole"] = OrgRole.ReadOnly;
        ctx.SetFeatureEntitlements(Core.Auth.FeatureEntitlements.Both);
        ctx.SetUser(user);
        var req = new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: [tenant.ToString()],
            url: "http://localhost:7071/api/bot-monitor/portal/bots?botTypeKey=grokbot");
        ctx.SetRequest(req);

        var res = await fn.PortalListBots(req, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await repo.Received(1).ListBotsForTenantAsync(tenant, user, "grokbot");
    }

    private static BotMonitorBot MakeBot(string key, string display) => new()
    {
        BotId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        DisplayName = "demo",
        BotTypeKey = key,
        BotTypeDisplayName = display,
        Platform = BotMonitorPlatform.Other,
        TokenHash = "hash",
        HmacSecretEnc = "enc",
        CheckinIntervalMinutes = 60,
    };

    private static BotMonitorFunctions BuildFunctions(IBotMonitorRepository repo)
    {
        var config = new ConfigurationBuilder().Build();
        var cred = BotMonitorTestConfig.Credentials();
        var orgs = Substitute.For<IOrganizationRepository>();
        var hb = new BotMonitorHeartbeatService(repo, cred, config, () => DateTime.UtcNow);
        return new BotMonitorFunctions(repo, orgs, cred, hb, config, NullLogger<BotMonitorFunctions>.Instance);
    }

    private sealed class JsonPostRequest : HttpRequestData
    {
        public JsonPostRequest(FunctionContext ctx, string url, string json)
            : base(ctx)
        {
            Method = "POST";
            Url = new Uri(url);
            Headers = new HttpHeadersCollection();
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        public override Stream Body { get; }
        public override HttpHeadersCollection Headers { get; }
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; }
        public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
        public override string Method { get; }
        public override HttpResponseData CreateResponse() => new FakeHttpResponseData(this);
    }
}
