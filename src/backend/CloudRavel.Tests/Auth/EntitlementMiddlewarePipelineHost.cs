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
    public static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly IUserRepository _users;
    private readonly IOrganizationRepository _orgs;
    private readonly TenantContextMiddleware _tenant;
    private readonly FeatureRouteEntitlementMiddleware _entitlements;

    private readonly bool _systemAdmin;

    public EntitlementMiddlewarePipelineHost(
        FeatureEntitlements memberEntitlements,
        FeatureEntitlements orgEntitlements,
        bool systemAdmin = false)
    {
        _systemAdmin = systemAdmin;
        _users = Substitute.For<IUserRepository>();
        _orgs = Substitute.For<IOrganizationRepository>();
        _users.GetByIdAsync(UserId).Returns(new User
        {
            UserId = UserId,
            DisplayName = "Sweep User",
            Email = "sweep@example.test",
            GlobalRole = systemAdmin ? SystemRole.SystemAdmin : SystemRole.Member,
            IsActive = true,
            AuthProvider = "local",
        });
        _users.GetTenantRoleAsync(UserId, WorkspaceId).Returns(OrgRole.ReadOnly);
        _users.GetFeatureEntitlementsAsync(UserId, WorkspaceId).Returns(memberEntitlements);
        _orgs.GetFeatureEntitlementsAsync(WorkspaceId).Returns(orgEntitlements);
        _users.GetUserTenantAccessAsync(UserId).Returns(new List<UserTenantAccess>
        {
            new() { UserId = UserId, TenantId = WorkspaceId, Role = OrgRole.ReadOnly },
        });

        _tenant = new TenantContextMiddleware(_users, _orgs, NullLogger<TenantContextMiddleware>.Instance);
        _entitlements = new FeatureRouteEntitlementMiddleware();
    }

    public async Task<(HttpStatusCode? Status, bool NextCalled)> InvokeAsync(
        string method,
        string absolutePath,
        bool? sendTenantHeader = null)
    {
        var ctx = new TestFunctionContext();
        ctx.Items["SystemRole"] = _systemAdmin ? SystemRole.SystemAdmin : SystemRole.Member;
        ctx.SetUser(UserId);

        var needsTenant = sendTenantHeader ?? RequiresTenantHeader(absolutePath, method);
        string[]? tenantHeaders = needsTenant ? new[] { WorkspaceId.ToString() } : null;
        var req = new FakeHttpRequestData(ctx, method, tenantHeaders, url: "http://localhost:7071" + absolutePath);
        ctx.SetRequest(req);

        var nextCalled = false;
        await _tenant.Invoke(ctx, async _ =>
        {
            await _entitlements.Invoke(ctx, _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        });

        if (ctx.GetInvocationResult() is HttpResponseData response)
            return (response.StatusCode, nextCalled);

        return (null, nextCalled);
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
        if (normalized.StartsWith("bot-monitor/registry", StringComparison.Ordinal)
            && (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (normalized.StartsWith("bot-monitor/ops/", StringComparison.Ordinal))
            return false;

        return true;
    }
}
