using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudRavel.Tests.Auth;

internal static class EntitlementSweepResponseRunner
{
    public static async Task<string> GetResponseBodyAsync(
        EntitlementRouteSweepTests.Persona persona,
        string method,
        string routeTemplate)
    {
        var path = HttpRouteDiscovery.MaterializePath(routeTemplate);
        var host = new EntitlementMiddlewarePipelineHost(persona.Member, persona.Org, persona.SystemAdmin);
        var (_, nextCalled, ctx) = await host.InvokeAsync(
            method,
            path,
            handler: c => EntitlementSweepHandlers.InvokeAsync(c, method, routeTemplate, persona));

        var testCtx = (TestFunctionContext)ctx;
        if (!nextCalled)
            return await ReadBodyIfAnyAsync(testCtx);

        if (testCtx.GetInvocationResult() is HttpResponseData response)
            return await response.ReadBodyAsync();

        return string.Empty;
    }

    private static async Task<string> ReadBodyIfAnyAsync(TestFunctionContext ctx)
    {
        if (ctx.GetInvocationResult() is HttpResponseData response)
            return await response.ReadBodyAsync();
        return string.Empty;
    }
}
