using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Tests.TestSupport;

namespace CloudRavel.Tests.Middleware;

/// <summary>
/// Every mutating endpoint in the API gates itself with a one-line call into
/// these helpers, so a defect here is a silent authorization bypass across the
/// whole surface. The pre-existing OrgRoleRankTests only asserted that the role
/// constants hold the expected strings; none of the ranking, comparison, or
/// IDOR logic below was covered.
/// </summary>
public class AuthorizationExtensionsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ---- Role ladder -------------------------------------------------------

    [Theory]
    [InlineData(OrgRole.ReadOnly, OrgRole.ReadOnly, true)]
    [InlineData(OrgRole.ReadOnly, OrgRole.CloudAdmin, false)]
    [InlineData(OrgRole.ReadOnly, OrgRole.OrgAdmin, false)]
    [InlineData(OrgRole.CloudAdmin, OrgRole.ReadOnly, true)]
    [InlineData(OrgRole.CloudAdmin, OrgRole.CloudAdmin, true)]
    [InlineData(OrgRole.CloudAdmin, OrgRole.OrgAdmin, false)]
    [InlineData(OrgRole.OrgAdmin, OrgRole.ReadOnly, true)]
    [InlineData(OrgRole.OrgAdmin, OrgRole.CloudAdmin, true)]
    [InlineData(OrgRole.OrgAdmin, OrgRole.OrgAdmin, true)]
    public void HasOrgRole_RanksReadOnlyBelowCloudAdminBelowOrgAdmin(string held, string required, bool expected)
    {
        var context = TestFunctionContext.With(orgRole: held);

        Assert.Equal(expected, context.HasOrgRole(required));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not_a_real_role")]
    [InlineData("READ_ONLY")]  // ranking is case-sensitive by design
    public void HasOrgRole_UnrecognizedRoleFailsClosed(string held)
    {
        var context = TestFunctionContext.With(orgRole: held);

        Assert.False(context.HasOrgRole(OrgRole.ReadOnly));
        Assert.False(context.HasOrgRole(OrgRole.CloudAdmin));
        Assert.False(context.HasOrgRole(OrgRole.OrgAdmin));
    }

    [Fact]
    public void HasOrgRole_AbsentRoleFailsClosed()
    {
        // TenantContextMiddleware leaves OrgRole unset for global/registry scope.
        var context = new TestFunctionContext();

        Assert.False(context.HasOrgRole(OrgRole.ReadOnly));
    }

    // ---- System role -------------------------------------------------------

    [Fact]
    public void GetSystemRole_DefaultsToMemberWhenAbsent()
    {
        Assert.Equal(SystemRole.Member, new TestFunctionContext().GetSystemRole());
        Assert.False(new TestFunctionContext().IsSystemAdmin());
    }

    [Fact]
    public void IsSystemAdmin_TrueOnlyForExactRoleValue()
    {
        Assert.True(TestFunctionContext.With(systemRole: SystemRole.SystemAdmin).IsSystemAdmin());
        Assert.False(TestFunctionContext.With(systemRole: "System_Admin").IsSystemAdmin());
        Assert.False(TestFunctionContext.With(systemRole: SystemRole.Member).IsSystemAdmin());
    }

    // ---- RequireOrgRoleAsync ----------------------------------------------

    [Fact]
    public async Task RequireOrgRole_AllowsSufficientRole()
    {
        var context = TestFunctionContext.With(orgRole: OrgRole.CloudAdmin);
        var req = new TestHttpRequestData(context);

        Assert.Null(await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin));
    }

    [Fact]
    public async Task RequireOrgRole_RejectsInsufficientRoleWith403()
    {
        var context = TestFunctionContext.With(orgRole: OrgRole.ReadOnly);
        var req = new TestHttpRequestData(context);

        var response = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.Forbidden, response!.StatusCode);
        Assert.Contains("FORBIDDEN_ROLE", response.ReadBody());
    }

    [Fact]
    public async Task RequireOrgRole_SystemAdminPassesWithoutAnyOrgRole()
    {
        // A system admin has no user_tenant_access row, so the org role is empty;
        // the bypass is what lets platform operators work across workspaces.
        var context = TestFunctionContext.With(systemRole: SystemRole.SystemAdmin, orgRole: string.Empty);
        var req = new TestHttpRequestData(context);

        Assert.Null(await context.RequireOrgRoleAsync(req, OrgRole.OrgAdmin));
    }

    [Fact]
    public async Task RequireSystemAdmin_RejectsOrgAdminWith403()
    {
        // The highest org role must still not reach system-tier endpoints.
        var context = TestFunctionContext.With(systemRole: SystemRole.Member, orgRole: OrgRole.OrgAdmin);
        var req = new TestHttpRequestData(context);

        var response = await context.RequireSystemAdminAsync(req);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.Forbidden, response!.StatusCode);
    }

    [Fact]
    public async Task RequireSystemAdmin_AllowsSystemAdmin()
    {
        var context = TestFunctionContext.With(systemRole: SystemRole.SystemAdmin);
        var req = new TestHttpRequestData(context);

        Assert.Null(await context.RequireSystemAdminAsync(req));
    }

    // ---- Path/header IDOR --------------------------------------------------

    [Fact]
    public async Task RequirePathTenantMatch_AllowsMatchingIds()
    {
        var context = TestFunctionContext.With(tenantId: TenantA);
        var req = new TestHttpRequestData(context);

        Assert.Null(await context.RequirePathTenantMatchAsync(req, TenantA));
    }

    [Fact]
    public async Task RequirePathTenantMatch_RejectsHeaderPointingAtADifferentTenant()
    {
        // The middleware authorized TenantA; the route asks for TenantB.
        var context = TestFunctionContext.With(tenantId: TenantA);
        var req = new TestHttpRequestData(context);

        var response = await context.RequirePathTenantMatchAsync(req, TenantB);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.BadRequest, response!.StatusCode);
        Assert.Contains("TENANT_MISMATCH", response.ReadBody());
    }

    [Fact]
    public async Task RequirePathTenantMatch_RejectsWhenNoTenantWasAuthorized()
    {
        var context = new TestFunctionContext();
        var req = new TestHttpRequestData(context);

        var response = await context.RequirePathTenantMatchAsync(req, TenantA);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.BadRequest, response!.StatusCode);
    }
}
