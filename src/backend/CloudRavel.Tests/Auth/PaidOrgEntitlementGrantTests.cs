using System.Net;
using System.Text;
using System.Text.Json;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class PaidOrgEntitlementGrantTests
{
    [Fact]
    public async Task Org_admin_cannot_grant_bot_when_org_is_cloud_only()
    {
        var orgId = Guid.NewGuid();
        var orgRepo = Substitute.For<IOrganizationRepository>();
        orgRepo.GetFeatureEntitlementsAsync(orgId).Returns(new FeatureEntitlements(false, true));

        var fn = BuildOrgAdmin(orgRepo);
        var ctx = OrgAdminContext(orgId);
        var req = JsonPost(ctx, $"http://localhost:7071/api/organizations/{orgId}/users",
            new { role = OrgRole.ReadOnly, entitlementBotMonitor = true, entitlementCloudMonitor = true, email = "a@example.com" });

        var res = await fn.AddOrgUser(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Org_admin_cannot_raise_organization_entitlements()
    {
        var orgId = Guid.NewGuid();
        var orgRepo = Substitute.For<IOrganizationRepository>();
        orgRepo.GetByIdAsync(orgId).Returns(new Core.Models.Organization { OrgId = orgId, Name = "Acme" });

        var fn = new AdminFunctions(
            Substitute.For<ISystemSettingsRepository>(),
            Substitute.For<IUserRepository>(),
            orgRepo,
            NullLogger<AdminFunctions>.Instance);

        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.Items["OrgRole"] = OrgRole.OrgAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/system/organizations/{orgId}/entitlements",
            new { entitlementBotMonitor = true, entitlementCloudMonitor = true }, "PATCH");

        var res = await fn.UpdateOrgEntitlements(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await orgRepo.DidNotReceive().UpdateFeatureEntitlementsAsync(Arg.Any<Guid>(), Arg.Any<FeatureEntitlements>());
    }

    [Fact]
    public async Task System_admin_can_license_bot_monitoring_for_organization()
    {
        var orgId = Guid.NewGuid();
        var orgRepo = Substitute.For<IOrganizationRepository>();
        orgRepo.GetByIdAsync(orgId).Returns(new Core.Models.Organization { OrgId = orgId, Name = "Acme" });

        var fn = new AdminFunctions(
            Substitute.For<ISystemSettingsRepository>(),
            Substitute.For<IUserRepository>(),
            orgRepo,
            NullLogger<AdminFunctions>.Instance);

        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/system/organizations/{orgId}/entitlements",
            new { entitlementBotMonitor = true, entitlementCloudMonitor = false }, "PATCH");

        var res = await fn.UpdateOrgEntitlements(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await orgRepo.Received(1).UpdateFeatureEntitlementsAsync(orgId,
            Arg.Is<FeatureEntitlements>(e => e.BotMonitoring && !e.CloudMonitoring));
    }

    [Fact]
    public async Task System_admin_can_set_organization_fully_unlicensed()
    {
        var orgId = Guid.NewGuid();
        var orgRepo = Substitute.For<IOrganizationRepository>();
        orgRepo.GetByIdAsync(orgId).Returns(new Core.Models.Organization { OrgId = orgId, Name = "Acme" });

        var fn = new AdminFunctions(
            Substitute.For<ISystemSettingsRepository>(),
            Substitute.For<IUserRepository>(),
            orgRepo,
            NullLogger<AdminFunctions>.Instance);

        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/system/organizations/{orgId}/entitlements",
            new { entitlementBotMonitor = false, entitlementCloudMonitor = false }, "PATCH");

        var res = await fn.UpdateOrgEntitlements(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await orgRepo.Received(1).UpdateFeatureEntitlementsAsync(orgId, FeatureEntitlements.None);
    }

    [Fact]
    public async Task System_admin_can_raise_organization_entitlements()
    {
        var orgId = Guid.NewGuid();
        var orgRepo = Substitute.For<IOrganizationRepository>();
        orgRepo.GetByIdAsync(orgId).Returns(new Core.Models.Organization { OrgId = orgId, Name = "Acme" });

        var fn = new AdminFunctions(
            Substitute.For<ISystemSettingsRepository>(),
            Substitute.For<IUserRepository>(),
            orgRepo,
            NullLogger<AdminFunctions>.Instance);

        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = JsonPost(ctx, $"http://localhost:7071/api/system/organizations/{orgId}/entitlements",
            new { entitlementBotMonitor = true, entitlementCloudMonitor = true }, "PATCH");

        var res = await fn.UpdateOrgEntitlements(req, orgId, ctx);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await orgRepo.Received(1).UpdateFeatureEntitlementsAsync(orgId, Arg.Is<FeatureEntitlements>(e => e.BotMonitoring && e.CloudMonitoring));
    }

    private static OrgAdminFunctions BuildOrgAdmin(IOrganizationRepository orgRepo) =>
        new(
            Substitute.For<IUserRepository>(),
            Substitute.For<IOrgSsoRepository>(),
            orgRepo,
            Substitute.For<ITenantRepository>(),
            NullLogger<OrgAdminFunctions>.Instance);

    private static TestFunctionContext OrgAdminContext(Guid orgId)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["TenantId"] = orgId;
        ctx.Items["OrgRole"] = OrgRole.OrgAdmin;
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetUser(Guid.NewGuid());
        return ctx;
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
        public override HttpResponseData CreateResponse() => new FakeHttpResponseData(this);
    }
}
