using System.Net;
using System.Text.Json;
using CloudRavel.Api.Auth;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

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
    private readonly LoginRateLimiter _rateLimiter;
    private readonly ILogger<AuthFunctions> _logger;

    public AuthFunctions(
        ILocalAuthService localAuth,
        IUserRepository userRepo,
        LoginRateLimiter rateLimiter,
        ILogger<AuthFunctions> logger)
    {
        _localAuth = localAuth;
        _userRepo = userRepo;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/login
    /// Anonymous — this is the login endpoint itself, so it can't require auth.
    /// </summary>
    private static readonly JsonSerializerOptions LoginJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Function("LocalLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        LoginRequestDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<LoginRequestDto>(req.Body, LoginJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse login request body");
            body = null;
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                new ErrorResponse { Code = "INVALID_REQUEST", Message = "Username and password are required." });
        }

        var username = body.Username.Trim();
        var clientIp = req.Headers.TryGetValues("X-Forwarded-For", out var xff)
            ? xff.FirstOrDefault()?.Split(',')[0].Trim()
            : null;
        if (string.IsNullOrEmpty(clientIp))
            clientIp = req.Headers.TryGetValues("X-Real-IP", out var xri) ? xri.FirstOrDefault() : "unknown";
        var rateKey = $"{clientIp}|{username.ToLowerInvariant()}";

        if (!_rateLimiter.IsAllowed(rateKey))
        {
            _logger.LogWarning("Login rate limit exceeded for {Key}", rateKey);
            return await WriteJsonAsync(req, (HttpStatusCode)429,
                new ErrorResponse { Code = "RATE_LIMITED", Message = "Too many login attempts. Try again in a minute." });
        }

        LocalAuthResult? result;
        try
        {
            result = await _localAuth.LoginAsync(username, body.Password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local login threw for username '{Username}'", username);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError,
                new ErrorResponse { Code = "LOGIN_FAILED", Message = "Login failed due to a server error. Check API logs." });
        }

        if (result == null)
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized,
                new ErrorResponse { Code = "INVALID_CREDENTIALS", Message = "Invalid username or password." });
        }

        _rateLimiter.Reset(rateKey);

        return await WriteJsonAsync(req, HttpStatusCode.OK, new LoginResponseDto
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
    }

    private static async Task<HttpResponseData> WriteJsonAsync<T>(HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateCorsResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, LoginJson));
        return response;
    }

    /// <summary>
    /// GET /api/auth/me — the authenticated caller's identity and system role.
    /// Works for BOTH local and Entra sessions (Entra callers are JIT-provisioned
    /// in TenantContextMiddleware).
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
            SystemRole = user?.GlobalRole ?? "member"
        });
        return response;
    }
}
