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
    private readonly IUserRepository _userRepo;
    private readonly ILogger<AuthFunctions> _logger;

    public AuthFunctions(ILocalAuthService localAuth, IUserRepository userRepo, ILogger<AuthFunctions> logger)
    {
        _localAuth = localAuth;
        _userRepo = userRepo;
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

    /// <summary>
    /// GET /api/auth/me — the authenticated caller's identity and system role.
    /// Works for BOTH local and Entra sessions, giving the frontend a single,
    /// uniform role source (Entra tokens carry no role claim of their own).
    /// </summary>
    [Function("Me")]
    public async Task<HttpResponseData> Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequestData req,
        FunctionContext context)
    {
        var userId = context.GetUserId();
        if (userId == null)
        {
            var unauth = req.CreateCorsResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteAsJsonAsync(new ErrorResponse { Code = "UNAUTHENTICATED", Message = "Not signed in." });
            return unauth;
        }

        var user = await _userRepo.GetByIdAsync(userId.Value);
        var httpUser = context.GetHttpContext()?.User;

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new MeDto
        {
            UserId = userId.Value,
            DisplayName = user?.DisplayName ?? httpUser?.FindFirst("name")?.Value ?? "User",
            Email = user?.Email ?? httpUser?.FindFirst("email")?.Value ?? string.Empty,
            // A user record may not exist yet for an Entra caller (no JIT provisioning
            // here) — default to the least-privileged system role in that case.
            SystemRole = user?.GlobalRole ?? "member"
        });
        return response;
    }
}
