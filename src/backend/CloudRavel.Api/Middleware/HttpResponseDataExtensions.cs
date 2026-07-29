using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Creates a response with CORS headers already attached. Isolated-worker
/// responses that write a body start streaming immediately — headers must be
/// present at CreateResponse() time.
///
/// Origins are allow-listed via <see cref="Configure"/> (from Cors:AllowedOrigins).
/// When unset, local-dev defaults to http://localhost:3000.
/// </summary>
public static class HttpResponseDataExtensions
{
    public const string ConfigKey = "Cors:AllowedOrigins";

    private static string[] _allowedOrigins = { "http://localhost:3000" };

    /// <summary>Call once at host startup with the configured allow-list.</summary>
    public static void Configure(IEnumerable<string>? origins)
    {
        var list = origins?
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .ToArray();
        _allowedOrigins = list is { Length: > 0 } ? list : new[] { "http://localhost:3000" };
    }

    public static void ConfigureFromRaw(string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated))
        {
            Configure(null);
            return;
        }
        Configure(commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static HttpResponseData CreateCorsResponse(this HttpRequestData req, HttpStatusCode statusCode)
    {
        var response = req.CreateResponse(statusCode);
        ApplyCorsHeaders(req, response);
        return response;
    }

    public static void ApplyCorsHeaders(HttpRequestData req, HttpResponseData response)
    {
        var requestOrigin = req.Headers.TryGetValues("Origin", out var origins)
            ? origins.FirstOrDefault()
            : null;

        string? allowOrigin = null;
        if (_allowedOrigins.Contains("*", StringComparer.Ordinal))
        {
            allowOrigin = "*";
        }
        else if (!string.IsNullOrEmpty(requestOrigin)
                 && _allowedOrigins.Any(a => string.Equals(a, requestOrigin, StringComparison.OrdinalIgnoreCase)))
        {
            allowOrigin = requestOrigin;
        }
        else if (_allowedOrigins.Length == 1)
        {
            allowOrigin = _allowedOrigins[0];
        }

        if (allowOrigin != null)
            response.Headers.Add("Access-Control-Allow-Origin", allowOrigin);

        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PATCH, PUT, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers",
            "Content-Type, Authorization, X-Tenant-Id");
        response.Headers.Add("Vary", "Origin");
    }
}
