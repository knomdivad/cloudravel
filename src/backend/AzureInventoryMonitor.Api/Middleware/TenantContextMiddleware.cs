using System.Security.Claims;
using AzureInventoryMonitor.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Middleware;

/// <summary>
/// Extracts and validates the tenant context from every incoming API request.
///
/// Enforcement chain:
///   1. UseAuthentication()/UseAuthorization() (Program.cs) + [Authorize] on each
///      function already reject requests without a valid Entra or Local JWT.
///   2. Reads X-Tenant-Id header
///   3. Checks the authenticated user actually has access to that tenant
///      (global "admin" role bypasses the per-tenant check, same as before)
///   4. Sets TenantContext in function context items for downstream use
///
/// This is the first tenant-scoping enforcement layer. RLS at the database
/// level is the second.
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
        // Skip tenant validation for non-HTTP functions (timers, service bus, etc.)
        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null)
        {
            await next(context);
            return;
        }

        // CORS preflight never carries X-Tenant-Id — let CorsFunctions handle it.
        if (httpRequestData.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Skip for health check and the login endpoint itself
        var path = httpRequestData.Url.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/health") || path.Contains("/auth/login"))
        {
            await next(context);
            return;
        }

        // Extract tenant ID from header
        if (!httpRequestData.Headers.TryGetValues("X-Tenant-Id", out var tenantIdValues))
        {
            // For tenant/organization listing endpoints, tenant header is optional
            if ((path.Contains("/tenants") || path.Contains("/organizations"))
                && httpRequestData.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var response = httpRequestData.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { code = "MISSING_TENANT", message = "X-Tenant-Id header is required." });
            context.GetInvocationResult().Value = response;
            return;
        }

        var tenantIdStr = tenantIdValues.First();
        if (!Guid.TryParse(tenantIdStr, out var tenantId))
        {
            var response = httpRequestData.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { code = "INVALID_TENANT", message = "X-Tenant-Id must be a valid GUID." });
            context.GetInvocationResult().Value = response;
            return;
        }

        var userId = context.GetUserId();
        if (userId.HasValue && !await _userRepo.HasTenantAccessAsync(userId.Value, tenantId))
        {
            var response = httpRequestData.CreateResponse(System.Net.HttpStatusCode.Forbidden);
            await response.WriteAsJsonAsync(new { code = "TENANT_FORBIDDEN", message = "You do not have access to this tenant." });
            context.GetInvocationResult().Value = response;
            return;
        }

        // Store tenant context for use by API functions
        context.Items["TenantId"] = tenantId;

        _logger.LogDebug("Request scoped to tenant {TenantId}", tenantId);
        await next(context);
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
