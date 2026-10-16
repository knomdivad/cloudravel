using System.Net;
using System.Text;
using System.Text.Json;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Tests.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class FeatureEntitlementAuditDenialTests
{
    [Fact]
    public async Task Tenant_admin_cannot_trigger_inventory_snapshot()
    {
        var inventory = Substitute.For<IInventoryRepository>();
        var fn = new InventoryFunctions(
            inventory,
            Substitute.For<IInventoryCollectionService>(),
            Substitute.For<ICloudAccountRepository>(),
            Substitute.For<ICloudOrgRepository>(),
            Substitute.For<IPlatformInfo>(),
            NullLogger<InventoryFunctions>.Instance);

        var ctx = CloudRavel.Tests.TestSupport.TestFunctionContext.With(orgRole: OrgRole.TenantAdmin, tenantId: Guid.NewGuid());
        ctx.SetFeatureEntitlements(new FeatureEntitlements(true, true));
        var req = new TestHttpRequestData(ctx, method: "POST", url: "http://localhost:7071/api/inventory/snapshots/trigger");

        var res = await fn.TriggerSnapshot(req, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Portal_bot_with_null_owner_denied_for_org_admin()
    {
        var tenant = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByIdAsync(tenant, botId).Returns(new Core.BotMonitor.BotMonitorBot
        {
            BotId = botId,
            TenantId = tenant,
            OwnerUserId = null,
            DisplayName = "orphan",
        });

        var fn = FeatureEntitlementGateTests.BuildBotMonitorFunctions(repo);
        var ctx = new Assessment.TestFunctionContext();
        ctx.Items["TenantId"] = tenant;
        ctx.Items["OrgRole"] = OrgRole.OrgAdmin;
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetFeatureEntitlements(FeatureEntitlements.Both);
        ctx.SetUser(Guid.NewGuid());

        var req = new Assessment.FakeHttpRequestData(ctx, "GET", [tenant.ToString()],
            url: $"http://localhost:7071/api/bot-monitor/portal/bots/{botId}");
        ctx.SetRequest(req);

        var res = await fn.PortalGetBot(req, botId.ToString(), ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Add_org_user_without_entitlements_is_rejected()
    {
        var orgId = Guid.NewGuid();
        var fn = new OrgAdminFunctions(
            Substitute.For<IUserRepository>(),
            Substitute.For<IOrgSsoRepository>(),
            Substitute.For<IOrganizationRepository>(),
            Substitute.For<ITenantRepository>(),
            NullLogger<OrgAdminFunctions>.Instance);

        var ctx = new Assessment.TestFunctionContext();
        ctx.Items["TenantId"] = orgId;
        ctx.Items["OrgRole"] = OrgRole.OrgAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/organizations/{orgId}/users",
            new { role = OrgRole.ReadOnly, entitlementBotMonitor = false, entitlementCloudMonitor = false, email = "a@example.com" });

        var res = await fn.AddOrgUser(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Update_org_user_without_entitlements_is_rejected()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var fn = new OrgAdminFunctions(
            Substitute.For<IUserRepository>(),
            Substitute.For<IOrgSsoRepository>(),
            Substitute.For<IOrganizationRepository>(),
            Substitute.For<ITenantRepository>(),
            NullLogger<OrgAdminFunctions>.Instance);

        var ctx = new Assessment.TestFunctionContext();
        ctx.Items["TenantId"] = orgId;
        ctx.Items["OrgRole"] = OrgRole.OrgAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/organizations/{orgId}/users/{userId}",
            new { role = OrgRole.ReadOnly, entitlementBotMonitor = false, entitlementCloudMonitor = false }, "PATCH");

        var res = await fn.UpdateOrgUserRole(req, orgId, userId, ctx);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public void Tenant_admin_passes_read_only_gate_but_not_cloud_admin_rank()
    {
        var ctx = CloudRavel.Tests.TestSupport.TestFunctionContext.With(orgRole: OrgRole.TenantAdmin);
        Assert.True(ctx.HasOrgRole(OrgRole.ReadOnly));
        Assert.False(ctx.HasOrgRole(OrgRole.CloudAdmin));
        Assert.False(ctx.CanPerformCloudWrite());
    }

    private static JsonBodyRequest JsonPost(FunctionContext ctx, string url, object body, string method = "POST")
    {
        var json = JsonSerializer.Serialize(body);
        var tenantId = ctx.TryGetTenantId()?.ToString();
        return new JsonBodyRequest(ctx, method, url, json, tenantId);
    }

    private sealed class JsonBodyRequest : HttpRequestData
    {
        public JsonBodyRequest(FunctionContext ctx, string method, string url, string json, string? tenantId = null)
            : base(ctx)
        {
            Method = method;
            Url = new Uri(url);
            Headers = new HttpHeadersCollection();
            if (tenantId is not null)
                Headers.Add("X-Tenant-Id", tenantId);
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        public override Stream Body { get; }
        public override HttpHeadersCollection Headers { get; }
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; }
        public override IEnumerable<System.Security.Claims.ClaimsIdentity> Identities { get; } = Array.Empty<System.Security.Claims.ClaimsIdentity>();
        public override string Method { get; }
        public override HttpResponseData CreateResponse() => new Assessment.FakeHttpResponseData(this);
    }
}
