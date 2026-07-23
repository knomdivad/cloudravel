using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AzureInventoryMonitor.Api.Middleware;

/// <summary>
/// Role-based authorization helpers over the roles TenantContextMiddleware stashes
/// into <see cref="FunctionContext.Items"/> ("SystemRole" = users.global_role,
/// "OrgRole" = the caller's user_tenant_access.role for the request's workspace,
/// with system_admin acting as org_admin everywhere).
///
/// Individual isolated-worker functions have no [Authorize] pipeline, so each
/// mutating endpoint gates itself with a one-line early return, e.g.:
///
///     var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
///     if (forbid != null) return forbid;
/// </summary>
public static class AuthorizationExtensions
{
    public static string GetSystemRole(this FunctionContext context) =>
        context.Items.TryGetValue("SystemRole", out var r) && r is string s && s.Length > 0 ? s : "member";

    public static string GetOrgRole(this FunctionContext context) =>
        context.Items.TryGetValue("OrgRole", out var r) && r is string s ? s : string.Empty;

    public static bool IsSystemAdmin(this FunctionContext context) =>
        context.GetSystemRole() == SystemRole.SystemAdmin;

    /// <summary>Org roles ranked low→high; -1 for an unknown/absent role.</summary>
    private static int Rank(string role) => role switch
    {
        OrgRole.ReadOnly => 0,
        OrgRole.CloudAdmin => 1,
        OrgRole.OrgAdmin => 2,
        _ => -1
    };

    public static bool HasOrgRole(this FunctionContext context, string minRole) =>
        Rank(context.GetOrgRole()) >= Rank(minRole);

    /// <summary>Returns a 403 response if the caller is not a system admin, else null.</summary>
    public static async Task<HttpResponseData?> RequireSystemAdminAsync(this FunctionContext context, HttpRequestData req)
    {
        if (context.IsSystemAdmin()) return null;
        return await ForbidAsync(req, "Only system administrators may perform this action.");
    }

    /// <summary>
    /// Returns a 403 response if the caller lacks at least <paramref name="minRole"/>
    /// in the request's workspace, else null. system_admin always passes.
    /// </summary>
    public static async Task<HttpResponseData?> RequireOrgRoleAsync(this FunctionContext context, HttpRequestData req, string minRole)
    {
        if (context.IsSystemAdmin() || context.HasOrgRole(minRole)) return null;
        return await ForbidAsync(req, $"This action requires the '{minRole}' role in this organization.");
    }

    private static async Task<HttpResponseData> ForbidAsync(HttpRequestData req, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { code = "FORBIDDEN_ROLE", message });
        return response;
    }
}

/// <summary>System-tier role values (users.global_role).</summary>
public static class SystemRole
{
    public const string SystemAdmin = "system_admin";
    public const string Member = "member";
}

/// <summary>Organization-tier role values (user_tenant_access.role).</summary>
public static class OrgRole
{
    public const string OrgAdmin = "org_admin";
    public const string CloudAdmin = "cloud_admin";
    public const string ReadOnly = "read_only";
}
