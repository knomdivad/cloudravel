using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudRavel.Tests.Auth;

/// <summary>Runs TenantContextMiddleware then FeatureRouteEntitlementMiddleware in production order.</summary>
internal sealed class EntitlementMiddlewarePipelineHost
{
    public static readonly Guid WorkspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid OrgBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly IUserRepository _users;
    private readonly IOrganizationRepository _orgs;
    private readonly TenantContextMiddleware _tenant;
    private readonly FeatureRouteEntitlementMiddleware _entitlements;
    private readonly bool _systemAdmin;
    private readonly Guid _userId;

    public EntitlementMiddlewarePipelineHost(
        FeatureEntitlements memberEntitlements,
        FeatureEntitlements orgEntitlements,
        bool systemAdmin = false)
        : this(BuildDefaultUsers(memberEntitlements, orgEntitlements, systemAdmin),
            BuildDefaultOrgs(orgEntitlements),
            UserId,
            WorkspaceId,
            systemAdmin)
    {
    }

    public EntitlementMiddlewarePipelineHost(
        IUserRepository users,
        IOrganizationRepository orgs,
        Guid userId,
        bool systemAdmin)
        : this(users, orgs, userId, WorkspaceId, systemAdmin)
    {
    }

    public EntitlementMiddlewarePipelineHost(
        IUserRepository users,
        IOrganizationRepository orgs,
        Guid userId,
        Guid defaultWorkspaceId,
        bool systemAdmin)
    {
        _users = users;
        _orgs = orgs;
        _userId = userId;
        _systemAdmin = systemAdmin;

        _tenant = new TenantContextMiddleware(_users, _orgs, NullLogger<TenantContextMiddleware>.Instance);
        _entitlements = new FeatureRouteEntitlementMiddleware();
    }

    public IUserRepository Users => _users;
    public IOrganizationRepository Orgs => _orgs;

    public async Task<(HttpStatusCode? Status, bool NextCalled, FunctionContext Context)> InvokeAsync(
        string method,
        string absolutePath,
        Guid? workspaceId = null,
        bool? sendTenantHeader = null,
        Func<FunctionContext, Task<HttpResponseData?>>? handler = null)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = _systemAdmin ? SystemRole.SystemAdmin : SystemRole.Member;
        ctx.SetUser(_userId);

        var tenant = workspaceId ?? WorkspaceId;
        var needsTenant = sendTenantHeader ?? RequiresTenantHeader(absolutePath, method);
        string[]? tenantHeaders = needsTenant ? new[] { tenant.ToString() } : null;
        var req = new FakeHttpRequestData(ctx, method, tenantHeaders, url: "http://localhost:7071" + absolutePath);
        ctx.SetRequest(req);

        var nextCalled = false;
        await _tenant.Invoke(ctx, async _ =>
        {
            await _entitlements.Invoke(ctx, async _ =>
            {
                nextCalled = true;
                if (handler != null)
                {
                    var response = await handler(ctx);
                    if (response != null)
                        ctx.SetInvocationResult(response);
                }
            });
        });

        if (ctx.GetInvocationResult() is HttpResponseData response)
            return (response.StatusCode, nextCalled, ctx);

        return (null, nextCalled, ctx);
    }

    private static IUserRepository BuildDefaultUsers(
        FeatureEntitlements memberEntitlements,
        FeatureEntitlements orgEntitlements,
        bool systemAdmin)
    {
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(UserId).Returns(new User
        {
            UserId = UserId,
            DisplayName = "Sweep User",
            Email = "sweep@example.test",
            GlobalRole = systemAdmin ? SystemRole.SystemAdmin : SystemRole.Member,
            IsActive = true,
            AuthProvider = "local",
        });
        users.GetTenantRoleAsync(UserId, WorkspaceId).Returns(OrgRole.ReadOnly);
        users.GetFeatureEntitlementsAsync(UserId, WorkspaceId).Returns(memberEntitlements);
        users.GetUserTenantAccessAsync(UserId).Returns(new List<UserTenantAccess>
        {
            new() { UserId = UserId, TenantId = WorkspaceId, Role = OrgRole.ReadOnly },
        });
        return users;
    }

    private static IOrganizationRepository BuildDefaultOrgs(FeatureEntitlements orgEntitlements)
    {
        var orgs = Substitute.For<IOrganizationRepository>();
        orgs.GetFeatureEntitlementsAsync(WorkspaceId).Returns(orgEntitlements);
        return orgs;
    }

    private static bool RequiresTenantHeader(string absolutePath, string method)
    {
        if (AnonymousApiPaths.IsAnonymousBypass(absolutePath))
            return false;

        var normalized = RouteEntitlementCatalog.NormalizePath(absolutePath);
        if (normalized is "organizations" or "auth/me")
            return false;
        if (normalized == "tenants")
            return true;
        if (normalized == "organizations" && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.StartsWith("admin/", StringComparison.Ordinal)
            || normalized.StartsWith("system/", StringComparison.Ordinal))
            return false;
        if (normalized.StartsWith("bot-monitor/", StringComparison.Ordinal))
            return true;

        return true;
    }
}
