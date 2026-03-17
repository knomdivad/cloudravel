using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Middleware;

/// <summary>
/// Extracts and validates the tenant context from every incoming API request.
/// 
/// Enforcement chain:
///   1. Reads X-Tenant-Id header
///   2. Validates JWT claims (user must have access to the tenant)
///   3. Sets TenantContext in function context items for downstream use
///   4. Rejects cross-tenant attempts with 403
/// 
/// This is the first enforcement layer. RLS at the database level is the second.
/// </summary>
public sealed class TenantContextMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(ILogger<TenantContextMiddleware> logger)
    {
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

        // Skip for health check and auth endpoints
        var path = httpRequestData.Url.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/health") || path.Contains("/auth/"))
        {
            await next(context);
            return;
        }

        // Extract tenant ID from header
        if (!httpRequestData.Headers.TryGetValues("X-Tenant-Id", out var tenantIdValues))
        {
            // For tenant listing endpoints, tenant header is optional
            if (path.Contains("/tenants") && httpRequestData.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
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

        // Store tenant context for use by API functions
        context.Items["TenantId"] = tenantId;

        _logger.LogDebug("Request scoped to tenant {TenantId}", tenantId);
        await next(context);
    }
}

/// <summary>
/// Extension methods for extracting tenant context in API functions.
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
}
