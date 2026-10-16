using System.Net;
using System.Text;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class NewOrgFailClosedEntitlementTests
{
    [Fact]
    public void New_organization_model_has_no_feature_entitlements_by_default()
    {
        var org = new Organization { Name = "Fresh workspace" };
        Assert.False(org.EntitlementBotMonitor);
        Assert.False(org.EntitlementCloudMonitor);
    }

    [Fact]
    public async Task Create_organization_persists_no_org_entitlements()
    {
        var orgRepo = Substitute.For<IOrganizationRepository>();
        Organization? captured = null;
        orgRepo.CreateAsync(Arg.Do<Organization>(o => captured = o))
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Organization>()));
        orgRepo.GetByIdAsync(Arg.Any<Guid>()).Returns(callInfo => captured);

        var tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Tenant?)null);

        var userRepo = Substitute.For<IUserRepository>();
        var fn = new OrganizationFunctions(
            orgRepo,
            tenantRepo,
            Substitute.For<ICloudOrgRepository>(),
            Substitute.For<ICloudAccountRepository>(),
            userRepo,
            Substitute.For<IInventoryCollectionService>(),
            Substitute.For<IPlatformInfo>(),
            NullLogger<OrganizationFunctions>.Instance);

        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.SystemAdmin;
        ctx.SetUser(Guid.NewGuid());
        var req = new JsonBodyRequest(ctx, "POST", "http://localhost:7071/api/organizations", """{"name":"Fresh"}""");
        ctx.SetRequest(req);

        var res = await fn.CreateOrganization(req, ctx);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.NotNull(captured);
        Assert.False(captured!.EntitlementBotMonitor);
        Assert.False(captured.EntitlementCloudMonitor);
        Assert.True(captured.EntitlementBackfillV014);
        await userRepo.Received(1).GrantTenantAccessAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            OrgRole.OrgAdmin,
            Arg.Any<Guid>(),
            FeatureEntitlements.None);
    }

    [Fact]
    public async Task Tenant_context_applies_org_cap_even_when_member_has_both_flags()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(userId).Returns(new User
        {
            UserId = userId,
            DisplayName = "Admin",
            Email = "a@example.com",
            GlobalRole = "member",
            IsActive = true,
            AuthProvider = "local",
        });
        users.GetTenantRoleAsync(userId, orgId).Returns(OrgRole.OrgAdmin);
        users.GetFeatureEntitlementsAsync(userId, orgId).Returns(FeatureEntitlements.Both);

        var orgs = Substitute.For<IOrganizationRepository>();
        orgs.GetFeatureEntitlementsAsync(orgId).Returns(FeatureEntitlements.None);

        var middleware = new TenantContextMiddleware(users, orgs, NullLogger<TenantContextMiddleware>.Instance);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetUser(userId);
        ctx.SetRequest(new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { orgId.ToString() },
            url: "http://localhost:7071/api/inventory/resources"));

        await middleware.Invoke(ctx, _ => Task.CompletedTask);
        Assert.Equal(FeatureEntitlements.None, ctx.GetFeatureEntitlements());
    }

    [Theory]
    [InlineData("/api/inventory/resources")]
    [InlineData("/api/bot-monitor/portal/bots")]
    public async Task Member_of_unlicensed_org_denied_on_cloud_and_bot_routes(string path)
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(userId).Returns(new User
        {
            UserId = userId,
            DisplayName = "Admin",
            Email = "a@example.com",
            GlobalRole = "member",
            IsActive = true,
            AuthProvider = "local",
        });
        users.GetTenantRoleAsync(userId, orgId).Returns(OrgRole.OrgAdmin);
        users.GetFeatureEntitlementsAsync(userId, orgId).Returns(FeatureEntitlements.Both);

        var orgs = Substitute.For<IOrganizationRepository>();
        orgs.GetFeatureEntitlementsAsync(orgId).Returns(FeatureEntitlements.None);

        var tenantMiddleware = new TenantContextMiddleware(users, orgs, NullLogger<TenantContextMiddleware>.Instance);
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = SystemRole.Member;
        ctx.SetUser(userId);
        ctx.SetRequest(new FakeHttpRequestData(ctx, "GET", tenantIdHeaders: new[] { orgId.ToString() },
            url: "http://localhost:7071" + path));

        await tenantMiddleware.Invoke(ctx, _ => Task.CompletedTask);

        var routeMiddleware = new FeatureRouteEntitlementMiddleware();
        var nextCalled = false;
        await routeMiddleware.Invoke(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        var response = ctx.GetInvocationResult() as HttpResponseData;
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.Forbidden, response!.StatusCode);
    }

    private sealed class JsonBodyRequest : HttpRequestData
    {
        public JsonBodyRequest(FunctionContext ctx, string method, string url, string json)
            : base(ctx)
        {
            Method = method;
            Url = new Uri(url);
            Headers = new HttpHeadersCollection();
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
