using System.Net;
using CloudRavel.Core.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudRavel.Api.Middleware;

public static class FeatureEntitlementExtensions
{
    public static FeatureEntitlements GetFeatureEntitlements(this FunctionContext context)
    {
        var bot = context.Items.TryGetValue(FeatureEntitlementKeys.BotMonitoring, out var b) && b is true;
        var cloud = context.Items.TryGetValue(FeatureEntitlementKeys.CloudMonitoring, out var c) && c is true;
        return new FeatureEntitlements(bot, cloud);
    }

    public static void SetFeatureEntitlements(this FunctionContext context, FeatureEntitlements entitlements)
    {
        context.Items[FeatureEntitlementKeys.BotMonitoring] = entitlements.BotMonitoring;
        context.Items[FeatureEntitlementKeys.CloudMonitoring] = entitlements.CloudMonitoring;
    }

    public static async Task<HttpResponseData?> RequireBotMonitoringEntitlementAsync(
        this FunctionContext context, HttpRequestData req)
    {
        if (context.IsSystemAdmin() || context.GetFeatureEntitlements().BotMonitoring) return null;
        return await EntitlementForbidAsync(req, "FEATURE_BOT_MONITORING_REQUIRED",
            "Bot monitoring is not enabled for your account in this organization.");
    }

    public static async Task<HttpResponseData?> RequireCloudMonitoringEntitlementAsync(
        this FunctionContext context, HttpRequestData req)
    {
        if (context.IsSystemAdmin() || context.GetFeatureEntitlements().CloudMonitoring) return null;
        return await EntitlementForbidAsync(req, "FEATURE_CLOUD_MONITORING_REQUIRED",
            "Cloud monitoring is not enabled for your account in this organization.");
    }

    private static async Task<HttpResponseData> EntitlementForbidAsync(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { code, message });
        return response;
    }
}
