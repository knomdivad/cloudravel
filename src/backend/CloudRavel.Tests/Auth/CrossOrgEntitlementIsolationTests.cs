using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class CrossOrgEntitlementIsolationTests
{
    private static readonly Guid OrgA = EntitlementMiddlewarePipelineHost.WorkspaceId;
    private static readonly Guid OrgB = EntitlementMiddlewarePipelineHost.OrgBId;
    private static readonly Guid UserId = EntitlementMiddlewarePipelineHost.UserId;

    [Fact]
    public async Task Cloud_route_allowed_only_in_cloud_org_B()
    {
        var host = BuildCrossOrgHost();
        var (allowedStatus, allowedNext, _) = await host.InvokeAsync(
            "GET", "/api/inventory/resources", workspaceId: OrgB);
        Assert.True(allowedNext);
        Assert.Null(allowedStatus);

        var (deniedStatus, deniedNext, _) = await host.InvokeAsync(
            "GET", "/api/inventory/resources", workspaceId: OrgA);
        Assert.False(deniedNext);
        Assert.Equal(HttpStatusCode.Forbidden, deniedStatus);
    }

    [Fact]
    public async Task Bot_route_allowed_only_in_bot_org_A()
    {
        var host = BuildCrossOrgHost();
        var (allowedStatus, allowedNext, _) = await host.InvokeAsync(
            "GET", "/api/bot-monitor/portal/bots", workspaceId: OrgA);
        Assert.True(allowedNext);
        Assert.Null(allowedStatus);

        var (deniedStatus, deniedNext, _) = await host.InvokeAsync(
            "GET", "/api/bot-monitor/portal/bots", workspaceId: OrgB);
        Assert.False(deniedNext);
        Assert.Equal(HttpStatusCode.Forbidden, deniedStatus);
    }

    [Fact]
    public async Task Bot_registry_without_workspace_header_is_bad_request_for_member()
    {
        var host = BuildCrossOrgHost();
        var (status, next, _) = await host.InvokeAsync(
            "GET", "/api/bot-monitor/registry/tenants", sendTenantHeader: false);

        Assert.False(next);
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    private static EntitlementMiddlewarePipelineHost BuildCrossOrgHost()
    {
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(UserId).Returns(new User
        {
            UserId = UserId,
            DisplayName = "Cross Org User",
            Email = "cross@example.test",
            GlobalRole = SystemRole.Member,
            IsActive = true,
            AuthProvider = "local",
        });
        users.GetTenantRoleAsync(UserId, OrgA).Returns(OrgRole.ReadOnly);
        users.GetTenantRoleAsync(UserId, OrgB).Returns(OrgRole.ReadOnly);
        users.GetUserTenantAccessAsync(UserId).Returns(new List<UserTenantAccess>
        {
            new() { UserId = UserId, TenantId = OrgA, Role = OrgRole.ReadOnly },
            new() { UserId = UserId, TenantId = OrgB, Role = OrgRole.ReadOnly },
        });
        users.GetFeatureEntitlementsAsync(UserId, OrgA).Returns(new FeatureEntitlements(true, false));
        users.GetFeatureEntitlementsAsync(UserId, OrgB).Returns(new FeatureEntitlements(false, true));

        var orgs = Substitute.For<IOrganizationRepository>();
        orgs.GetFeatureEntitlementsAsync(OrgA).Returns(new FeatureEntitlements(true, false));
        orgs.GetFeatureEntitlementsAsync(OrgB).Returns(new FeatureEntitlements(false, true));

        return new EntitlementMiddlewarePipelineHost(users, orgs, UserId, systemAdmin: false);
    }
}
