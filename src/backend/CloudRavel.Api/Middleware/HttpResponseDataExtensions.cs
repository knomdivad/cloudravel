using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Creates a response with CORS headers already attached. Isolated-worker
/// responses that write a body (WriteAsJsonAsync/WriteStringAsync) start
/// streaming immediately — headers added afterward, even from outer
/// middleware, are silently dropped once that's happened. So headers must be
/// present at CreateResponse() time, before any write call; there's no
/// reliable way to add them generically after the fact.
/// </summary>
public static class HttpResponseDataExtensions
{
    public static HttpResponseData CreateCorsResponse(this HttpRequestData req, HttpStatusCode statusCode)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PATCH, PUT, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Tenant-Id");
        return response;
    }
}
