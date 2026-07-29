using System.Security.Claims;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Extracts and validates the tenant context from every incoming API request.
///
/// Enforcement chain:
///   1. AuthEnforcementMiddleware rejects requests without a valid JWT.
///   2. This middleware resolves the user, rejects inactive accounts, JIT-provisions
///      Entra callers, then validates X-Tenant-Id against RBAC.
///   3. Sets TenantContext / roles in function context items for downstream use.
///
/// RLS at the database level is the second isolation layer for tenant-scoped tables.
/// </summary>
public sealed class TenantContextMiddleware : IFunctionsWorkerMiddleware
{
    private readonly IUserRepository _userRepo;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(IUserRepository userRepo, ILogger<TenantContextMiddleware> logger)
    {
        _userRepo = userRepo;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null)
        {
            await next(context);
            return;
        }

        if (httpRequestData.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var path = httpRequestData.Url.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/health") || path.Contains("/auth/login"))
        {
            await next(context);
            return;
        }

        // Resolve identity from JWT (already authenticated by AuthEnforcementMiddleware).
        var userId = context.GetUserId();
        if (!userId.HasValue)
        {
            var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.Unauthorized);
            await response.WriteAsJsonAsync(new
            {
                code = "UNAUTHENTICATED",
                message = "A valid Bearer token with a resolvable user identity is required."
            });
            context.GetInvocationResult().Value = response;
            return;
        }

        var user = await _userRepo.GetByIdAsync(userId.Value);
        var httpUser = context.GetHttpContext()?.User;
        var isEntra = IsEntraPrincipal(httpUser);

        // JIT-provision Entra callers so roles / org grants can be attached later.
        if (user == null && isEntra)
        {
            user = await ProvisionEntraUserAsync(userId.Value, httpUser);
        }

        if (user == null)
        {
            // Local JWT for a deleted user, or Entra provision failure.
            var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.Unauthorized);
            await response.WriteAsJsonAsync(new
            {
                code = "USER_NOT_FOUND",
                message = "No user record exists for this identity."
            });
            context.GetInvocationResult().Value = response;
            return;
        }

        if (!user.IsActive)
        {
            var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.Forbidden);
            await response.WriteAsJsonAsync(new
            {
                code = "USER_DISABLED",
                message = "This account has been disabled."
            });
            context.GetInvocationResult().Value = response;
            return;
        }

        var isSystemAdmin = user.GlobalRole == "system_admin";
        context.Items["SystemRole"] = user.GlobalRole ?? "member";

        // Tenant header optional for listing endpoints and /auth/me.
        if (!httpRequestData.Headers.TryGetValues("X-Tenant-Id", out var tenantIdValues))
        {
            if ((path.Contains("/tenants") || path.Contains("/organizations") || path.Contains("/auth/me")
                 || path.Contains("/admin/"))
                && (httpRequestData.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/auth/me")
                    || path.Contains("/admin/")))
            {
                await next(context);
                return;
            }

            // POST /organizations (create) and some admin mutations don't need a workspace header.
            if (path.Contains("/organizations")
                && httpRequestData.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/azure") && !path.Contains("/users") && !path.Contains("/sso"))
            {
                await next(context);
                return;
            }

            if (path.Contains("/admin/")
                && (httpRequestData.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                    || httpRequestData.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    || httpRequestData.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { code = "MISSING_TENANT", message = "X-Tenant-Id header is required." });
            context.GetInvocationResult().Value = response;
            return;
        }

        var tenantIdStr = tenantIdValues.First();
        if (!Guid.TryParse(tenantIdStr, out var tenantId))
        {
            var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { code = "INVALID_TENANT", message = "X-Tenant-Id must be a valid GUID." });
            context.GetInvocationResult().Value = response;
            return;
        }

        if (tenantId == Guid.Empty)
        {
            // Global / registry scope (NO_WORKSPACE sentinel).
            context.Items["OrgRole"] = string.Empty;
        }
        else
        {
            var orgRole = isSystemAdmin ? "org_admin" : await _userRepo.GetTenantRoleAsync(userId.Value, tenantId);
            if (!isSystemAdmin && orgRole == null)
            {
                var response = httpRequestData.CreateCorsResponse(System.Net.HttpStatusCode.Forbidden);
                await response.WriteAsJsonAsync(new { code = "TENANT_FORBIDDEN", message = "You do not have access to this tenant." });
                context.GetInvocationResult().Value = response;
                return;
            }
            context.Items["OrgRole"] = orgRole ?? string.Empty;
        }

        context.Items["TenantId"] = tenantId;

        _logger.LogDebug("Request scoped to tenant {TenantId} for user {UserId}", tenantId, userId);
        await next(context);
    }

    private async Task<User> ProvisionEntraUserAsync(Guid userId, ClaimsPrincipal? principal)
    {
        var displayName = principal?.FindFirst("name")?.Value
            ?? principal?.FindFirst("preferred_username")?.Value
            ?? "Entra User";
        var email = principal?.FindFirst("email")?.Value
            ?? principal?.FindFirst(ClaimTypes.Email)?.Value
            ?? principal?.FindFirst("preferred_username")?.Value
            ?? string.Empty;

        var user = new User
        {
            UserId = userId,
            DisplayName = displayName,
            Email = email,
            GlobalRole = "member",
            IsActive = true,
            AuthProvider = "entra",
        };

        var created = await _userRepo.UpsertAsync(user);
        _logger.LogInformation("JIT-provisioned Entra user {UserId} ({Email})", userId, email);
        return created;
    }

    private static bool IsEntraPrincipal(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        // Local tokens use issuer cloudravel-local-auth; Entra tokens carry oid.
        if (user.FindFirst("oid") != null) return true;
        var iss = user.FindFirst("iss")?.Value ?? string.Empty;
        return iss.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Extension methods for extracting tenant/user context in API functions.
/// </summary>
public static class TenantContextExtensions
{
    public static Guid GetTenantId(this FunctionContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var tenantIdObj) && tenantIdObj is Guid tenantId)
        {
            return tenantId;
        }
        throw new InvalidOperationException("Tenant context not available. Ensure TenantContextMiddleware is registered.");
    }

    public static Guid? TryGetTenantId(this FunctionContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var tenantIdObj) && tenantIdObj is Guid tenantId)
        {
            return tenantId;
        }
        return null;
    }

    /// <summary>
    /// The authenticated user's ID: the Entra object ID (`oid` claim) for SSO
    /// requests, or the local user's GUID (`sub` claim) for local-auth requests.
    /// Null for anonymous endpoints (health, login).
    /// </summary>
    public static Guid? GetUserId(this FunctionContext context)
    {
        var httpContext = context.GetHttpContext();
        var user = httpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        var raw = user.FindFirst("oid")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value;

        return Guid.TryParse(raw, out var userId) ? userId : null;
    }
}
