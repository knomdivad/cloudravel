using System.Net;
using AzureInventoryMonitor.Api.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// Handles CORS preflight (OPTIONS) requests for every route. Isolated-worker
/// routing 404s an HTTP method a function doesn't declare — since none of the
/// GET/POST/etc. functions declare "options", a wildcard catch-all function
/// is needed so OPTIONS has somewhere to match at all. The actual CORS
/// headers are added uniformly by CorsMiddleware, which this now flows
/// through same as any other function.
/// </summary>
public sealed class CorsFunctions
{
    [Function("CorsPreflight")]
    public HttpResponseData HandlePreflight(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "{*route}")] HttpRequestData req)
    {
        return req.CreateCorsResponse(HttpStatusCode.NoContent);
    }
}
