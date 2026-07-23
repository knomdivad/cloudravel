using System.Net;
using AzureInventoryMonitor.Api.Middleware;
using AzureInventoryMonitor.Core.DTOs;
using AzureInventoryMonitor.Core.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// Local username/password login — the non-Entra authentication path. Entra ID
/// SSO continues to work independently via MSAL on the frontend; this endpoint
/// exists so the platform can run without an Entra tenant at all (local dev,
/// or self-hosting on any cloud).
/// </summary>
public sealed class AuthFunctions
{
    private readonly ILocalAuthService _localAuth;
    private readonly ILogger<AuthFunctions> _logger;

    public AuthFunctions(ILocalAuthService localAuth, ILogger<AuthFunctions> logger)
    {
        _localAuth = localAuth;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/login
    /// Anonymous — this is the login endpoint itself, so it can't require auth.
    /// </summary>
    [Function("LocalLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<LoginRequestDto>();
        if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_REQUEST", Message = "Username and password are required." });
            return badReq;
        }

        var result = await _localAuth.LoginAsync(body.Username.Trim(), body.Password);
        if (result == null)
        {
            var unauthorized = req.CreateCorsResponse(HttpStatusCode.Unauthorized);
            await unauthorized.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_CREDENTIALS", Message = "Invalid username or password." });
            return unauthorized;
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new LoginResponseDto
        {
            Token = result.Token,
            ExpiresAt = result.ExpiresAt,
            User = new AuthUserDto
            {
                UserId = result.User.UserId,
                DisplayName = result.User.DisplayName,
                Email = result.User.Email,
                GlobalRole = result.User.GlobalRole,
            }
        });
        return response;
    }
}
