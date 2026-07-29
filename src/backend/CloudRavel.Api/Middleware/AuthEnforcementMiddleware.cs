using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Enforces authentication on every HTTP-triggered function except health
/// checks and the login endpoint itself.
///
/// Isolated-worker Azure Functions don't get ASP.NET Core's automatic
/// UseAuthentication()/UseAuthorization() pipeline or [Authorize] attribute
/// enforcement, even with the AspNetCore integration package — that package
/// gives you a real, DI-wired HttpContext, but nothing evaluates auth
/// metadata against it automatically. This middleware does that check
/// manually: it authenticates against the "EntraOrLocal" policy scheme
/// (Program.cs), which forwards to whichever of the "EntraId"/"Local"
/// JwtBearer schemes actually issued the token.
/// </summary>
public sealed class AuthEnforcementMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<AuthEnforcementMiddleware> _logger;

    public AuthEnforcementMiddleware(ILogger<AuthEnforcementMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null)
        {
            // Non-HTTP triggers (timers) — nothing to authenticate.
            await next(context);
            return;
        }

        // CORS preflight never carries auth headers — let CorsFunctions handle it.
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

        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            _logger.LogError("No HttpContext available for {Path} — AspNetCore integration misconfigured?", path);
            var errorResponse = httpRequestData.CreateCorsResponse(HttpStatusCode.InternalServerError);
            context.GetInvocationResult().Value = errorResponse;
            return;
        }

        var result = await httpContext.AuthenticateAsync();
        if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
        {
            var response = httpRequestData.CreateCorsResponse(HttpStatusCode.Unauthorized);
            await response.WriteAsJsonAsync(new { code = "UNAUTHENTICATED", message = "A valid Bearer token (Entra ID or local login) is required." });
            context.GetInvocationResult().Value = response;
            return;
        }

        httpContext.User = result.Principal;
        await next(context);
    }
}
