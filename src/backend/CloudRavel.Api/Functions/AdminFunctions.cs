using System.Net;
using System.Text.Json;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// System-administrator endpoints: instance-wide settings (e.g. the OpenAI
/// endpoint/key/model) and global user management. Every endpoint here requires
/// the system_admin system role.
/// </summary>
public sealed class AdminFunctions
{
    private readonly ISystemSettingsRepository _settings;
    private readonly IUserRepository _userRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<AdminFunctions> _logger;

    public AdminFunctions(
        ISystemSettingsRepository settings,
        IUserRepository userRepo,
        ILogger<AdminFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _settings = settings;
        _userRepo = userRepo;
        _secretStore = secretStore;
        _logger = logger;
    }

    // ========================================================================
    // System settings
    // ========================================================================

    /// <summary>GET /api/admin/settings — current AI settings (never returns the key value).</summary>
    [Function("GetSystemSettings")]
    public async Task<HttpResponseData> GetSystemSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/settings")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var all = await _settings.GetAllAsync();
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new SystemSettingsDto
        {
            OpenAiBaseUrl = all.GetValueOrDefault(SystemSettingKeys.OpenAiBaseUrl),
            OpenAiModel = all.GetValueOrDefault(SystemSettingKeys.OpenAiModel),
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(all.GetValueOrDefault(SystemSettingKeys.OpenAiApiKeySecretName))
        });
        return response;
    }

    /// <summary>PUT /api/admin/settings — update AI settings; the key goes to the secret store.</summary>
    [Function("UpdateSystemSettings")]
    public async Task<HttpResponseData> UpdateSystemSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "admin/settings")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var body = await req.ReadFromJsonAsync<UpdateSystemSettingsRequest>();
        if (body == null) return await BadRequest(req, "INVALID_REQUEST", "A settings body is required.");

        var actor = context.GetUserId()?.ToString() ?? "system";

        // base_url and model are plain values; null/empty clears them (falls back to env var).
        await _settings.SetAsync(SystemSettingKeys.OpenAiBaseUrl, Trim(body.OpenAiBaseUrl), actor);
        await _settings.SetAsync(SystemSettingKeys.OpenAiModel, Trim(body.OpenAiModel), actor);

        if (!string.IsNullOrWhiteSpace(body.OpenAiApiKey))
        {
            if (_secretStore == null)
                return await BadRequest(req, "SECRET_STORE_REQUIRED",
                    "A secret store is not configured, so the API key cannot be stored securely.");
            await _secretStore.SetSecretAsync(SystemSettingKeys.OpenAiApiKeySecretPath, body.OpenAiApiKey.Trim());
            await _settings.SetAsync(SystemSettingKeys.OpenAiApiKeySecretName, SystemSettingKeys.OpenAiApiKeySecretPath, actor);
            _logger.LogInformation("OpenAI API key updated by {Actor}", actor);
        }

        return await GetSystemSettings(req, context);
    }

    // ========================================================================
    // Global user management
    // ========================================================================

    /// <summary>GET /api/admin/users — all users.</summary>
    [Function("ListAllUsers")]
    public async Task<HttpResponseData> ListAllUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/users")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var users = await _userRepo.ListAllAsync();
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new AdminUsersResponse { Users = users.Select(ToDto).ToList() });
        return response;
    }

    private static readonly JsonSerializerOptions AdminJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// POST /api/admin/users and POST /api/admin/users/create —
    /// create a local user (optionally a system admin). Dual routes: some hosts
    /// only bind one verb on shared route templates with GET list.
    /// </summary>
    [Function("CreateUser")]
    public Task<HttpResponseData> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/users")] HttpRequestData req,
        FunctionContext context) =>
        CreateUserCoreAsync(req, context);

    [Function("CreateUserExplicit")]
    public Task<HttpResponseData> CreateUserExplicit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/users/create")] HttpRequestData req,
        FunctionContext context) =>
        CreateUserCoreAsync(req, context);

    private async Task<HttpResponseData> CreateUserCoreAsync(HttpRequestData req, FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        CreateUserRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CreateUserRequest>(req.Body, AdminJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse CreateUser body");
            body = null;
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Password)
            || string.IsNullOrWhiteSpace(body.DisplayName)
            || string.IsNullOrWhiteSpace(body.Email))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                new ErrorResponse { Code = "INVALID_REQUEST", Message = "displayName, email, and password are required." });

        var email = body.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@', StringComparison.Ordinal))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                new ErrorResponse { Code = "INVALID_EMAIL", Message = "email must be a valid email address (also used as login username)." });

        // Email is the unique login identity; username is set equal to email.
        if (await _userRepo.GetByEmailAsync(email) != null
            || await _userRepo.GetByUsernameAsync(email) != null)
            return await WriteJsonAsync(req, HttpStatusCode.Conflict,
                new ErrorResponse { Code = "EMAIL_TAKEN", Message = "That email is already in use." });

        var globalRole = string.Equals(body.GlobalRole, SystemRole.SystemAdmin, StringComparison.OrdinalIgnoreCase)
            ? SystemRole.SystemAdmin : SystemRole.Member;

        User created;
        try
        {
            created = await _userRepo.CreateLocalUserAsync(new User
            {
                UserId = Guid.NewGuid(),
                DisplayName = body.DisplayName.Trim(),
                Email = email,
                Username = email,
                GlobalRole = globalRole
            }, PasswordHasher.Hash(body.Password));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create user '{Email}'", email);
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                new ErrorResponse { Code = "USER_CREATE_FAILED", Message = $"Could not create user. Detail: {ex.Message}" });
        }

        _logger.LogInformation("Created user {UserId} ({Email}) role={Role}", created.UserId, created.Email, created.GlobalRole);
        return await WriteJsonAsync(req, HttpStatusCode.Created, ToDto(created));
    }

    private static async Task<HttpResponseData> WriteJsonAsync<T>(HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateCorsResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, AdminJson));
        return response;
    }

    /// <summary>PATCH /api/admin/users/{id} — set global role / active / reset password.</summary>
    [Function("UpdateUser")]
    public async Task<HttpResponseData> UpdateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "admin/users/{userId:guid}")] HttpRequestData req,
        Guid userId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var user = await _userRepo.GetByIdAsync(userId);
        if (user == null) return await NotFound(req, $"User {userId} not found.");

        var body = await req.ReadFromJsonAsync<UpdateUserRequest>();
        if (body == null) return await BadRequest(req, "INVALID_REQUEST", "An update body is required.");

        if (!string.IsNullOrWhiteSpace(body.GlobalRole))
        {
            var role = string.Equals(body.GlobalRole, SystemRole.SystemAdmin, StringComparison.OrdinalIgnoreCase)
                ? SystemRole.SystemAdmin : SystemRole.Member;
            await _userRepo.SetGlobalRoleAsync(userId, role);
        }
        if (body.IsActive.HasValue)
            await _userRepo.SetActiveAsync(userId, body.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(body.Password))
            await _userRepo.SetPasswordAsync(userId, PasswordHasher.Hash(body.Password));

        var updated = await _userRepo.GetByIdAsync(userId);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(updated!));
        return response;
    }

    // ---- helpers ----

    internal static AdminUserDto ToDto(User u) => new()
    {
        UserId = u.UserId,
        DisplayName = u.DisplayName,
        Email = u.Email,
        GlobalRole = u.GlobalRole,
        IsActive = u.IsActive,
        AuthProvider = u.AuthProvider,
        Username = u.Username,
        LastLoginAt = u.LastLoginAt
    };

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = "NOT_FOUND", Message = message });
        return response;
    }

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Conflict);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }
}
