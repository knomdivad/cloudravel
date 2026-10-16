using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Deny-by-default feature entitlement gate keyed on route prefix / template.
/// Runs after <see cref="TenantContextMiddleware"/> so entitlements are loaded for workspace scoped calls.
/// </summary>
public sealed class FeatureRouteEntitlementMiddleware : IFunctionsWorkerMiddleware
{
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

        var path = httpRequestData.Url.AbsolutePath;
        if (path.Contains("/health", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/bot-monitor/heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var requirement = RouteEntitlementCatalog.ResolveRequirement(path);
        if (requirement is null)
        {
            var deny = httpRequestData.CreateCorsResponse(HttpStatusCode.Forbidden);
            await deny.WriteAsJsonAsync(new
            {
                code = "ROUTE_NOT_MAPPED",
                message = "This API route is not registered in the feature entitlement catalog.",
            });
            context.GetInvocationResult().Value = deny;
            return;
        }

        if (context.IsSystemAdmin())
        {
            await next(context);
            return;
        }

        if (string.Equals(requirement, RouteEntitlementCatalog.RequirementAuthenticated, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (string.Equals(requirement, RouteEntitlementCatalog.RequirementCloudMonitoring, StringComparison.Ordinal))
        {
            if (!context.GetFeatureEntitlements().CloudMonitoring)
            {
                context.GetInvocationResult().Value = await EntitlementForbidAsync(
                    httpRequestData, "FEATURE_CLOUD_MONITORING_REQUIRED",
                    "Cloud monitoring is not enabled for your account in this organization.");
                return;
            }

            await next(context);
            return;
        }

        if (string.Equals(requirement, RouteEntitlementCatalog.RequirementBotMonitoring, StringComparison.Ordinal))
        {
            if (!context.GetFeatureEntitlements().BotMonitoring)
            {
                context.GetInvocationResult().Value = await EntitlementForbidAsync(
                    httpRequestData, "FEATURE_BOT_MONITORING_REQUIRED",
                    "Bot monitoring is not enabled for your account in this organization.");
                return;
            }

            await next(context);
            return;
        }

        var unmapped = httpRequestData.CreateCorsResponse(HttpStatusCode.Forbidden);
        await unmapped.WriteAsJsonAsync(new { code = "ROUTE_NOT_MAPPED", message = "Unknown entitlement requirement." });
        context.GetInvocationResult().Value = unmapped;
    }

    private static async Task<HttpResponseData> EntitlementForbidAsync(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { code, message });
        return response;
    }
}
