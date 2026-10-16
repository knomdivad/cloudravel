using CloudRavel.Api.Middleware;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudRavel.Tests.Auth;

internal static class EntitlementSweepResponseRunner
{
    public sealed record SweepResponse(string Body, string HandlerGate);

    public static Task<string> GetResponseBodyAsync(
        EntitlementRouteSweepTests.Persona persona,
        string method,
        string routeTemplate) =>
        GetResponseAsync(persona, method, routeTemplate).ContinueWith(t => t.Result.Body);

    public static async Task<SweepResponse> GetResponseAsync(
        EntitlementRouteSweepTests.Persona persona,
        string method,
        string routeTemplate)
    {
        var path = HttpRouteDiscovery.MaterializePath(routeTemplate);
        var gate = AnonymousApiPaths.IsAnonymousBypass(path) ? "anonymous" : "pending";
        var host = new EntitlementMiddlewarePipelineHost(persona.Member, persona.Org, persona.SystemAdmin);
        var (_, nextCalled, ctx) = await host.InvokeAsync(
            method,
            path,
            handler: c => EntitlementSweepHandlers.InvokeAsync(c, method, routeTemplate, persona));

        var testCtx = (TestFunctionContext)ctx;
        if (!nextCalled)
        {
            gate = gate == "anonymous" ? "anonymous" : "middleware";
            return new SweepResponse(await ReadBodyIfAnyAsync(testCtx), gate);
        }

        gate = "handler";
        if (testCtx.GetInvocationResult() is HttpResponseData response)
            return new SweepResponse(await response.ReadBodyAsync(), gate);

        return new SweepResponse(string.Empty, gate);
    }

    private static async Task<string> ReadBodyIfAnyAsync(TestFunctionContext ctx)
    {
        if (ctx.GetInvocationResult() is HttpResponseData response)
            return await response.ReadBodyAsync();
        return string.Empty;
    }
}
