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
    // NOTE: Use /api/system/* — not /api/admin/*. Azure Functions reserves /admin
    // for host management, and /api/admin/* routes 404 in the container host.

    /// <summary>
    /// GET  /api/system/settings — current AI settings (never returns the key value).
    /// PUT  /api/system/settings — update AI settings; the key goes to the secret store.
    /// </summary>
    [Function("SystemSettings")]
    public async Task<HttpResponseData> SystemSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", Route = "system/settings")] HttpRequestData req,
        FunctionContext context)
    {
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return await GetSystemSettingsCoreAsync(req, context);
        return await UpdateSystemSettingsCoreAsync(req, context);
    }

    private async Task<HttpResponseData> GetSystemSettingsCoreAsync(HttpRequestData req, FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var all = await _settings.GetAllAsync();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new SystemSettingsDto
        {
            OpenAiBaseUrl = all.GetValueOrDefault(SystemSettingKeys.OpenAiBaseUrl),
            OpenAiModel = all.GetValueOrDefault(SystemSettingKeys.OpenAiModel),
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(all.GetValueOrDefault(SystemSettingKeys.OpenAiApiKeySecretName))
        });
    }

    private async Task<HttpResponseData> UpdateSystemSettingsCoreAsync(HttpRequestData req, FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        UpdateSystemSettingsRequest? body;
        try
        {
            using var reader = new StreamReader(req.Body);
            var raw = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<UpdateSystemSettingsRequest>(raw, AdminJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse UpdateSystemSettings body");
            body = null;
        }

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

        return await GetSystemSettingsCoreAsync(req, context);
    }

    // ========================================================================
    // Global user management
    // ========================================================================

    private static readonly JsonSerializerOptions AdminJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// GET  /api/admin/users — list all users.
    /// POST /api/admin/users — create a local user (email = login identity).
    /// Single function, multi-method: reliable on Azure Functions isolated worker.
    /// </summary>
    [Function("AdminUsers")]
    public async Task<HttpResponseData> AdminUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "admin/users")] HttpRequestData req,
        FunctionContext context)
    {
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return await ListAllUsersCoreAsync(req, context);
        return await CreateUserCoreAsync(req, context);
    }

    /// <summary>POST /api/admin/users/create — same as POST /api/admin/users (explicit path).</summary>
    [Function("CreateUser")]
    public async Task<HttpResponseData> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/users/create")] HttpRequestData req,
        FunctionContext context) =>
        await CreateUserCoreAsync(req, context);

    /// <summary>
    /// GET  /api/system/users — list all users.
    /// POST /api/system/users — create user.
    /// Dedicated path used by the SPA (avoids shared-route GET/POST issues).
    /// </summary>
    [Function("SystemUsers")]
    public async Task<HttpResponseData> SystemUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "system/users")] HttpRequestData req,
        FunctionContext context)
    {
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return await ListAllUsersCoreAsync(req, context);
        return await CreateUserCoreAsync(req, context);
    }

    private async Task<HttpResponseData> ListAllUsersCoreAsync(HttpRequestData req, FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var users = await _userRepo.ListAllAsync();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new AdminUsersResponse { Users = users.Select(ToDto).ToList() });
    }

    private async Task<HttpResponseData> CreateUserCoreAsync(HttpRequestData req, FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        CreateUserRequest? body;
        try
        {
            // Buffer body — some hosts leave the stream unreadable after middleware.
            using var reader = new StreamReader(req.Body);
            var raw = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<CreateUserRequest>(raw, AdminJson);
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

    /// <summary>PATCH /api/system/users/{id} — set global role / active / reset password.</summary>
    [Function("UpdateUser")]
    public async Task<HttpResponseData> UpdateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "system/users/{userId:guid}")] HttpRequestData req,
        Guid userId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var user = await _userRepo.GetByIdAsync(userId);
        if (user == null) return await NotFound(req, $"User {userId} not found.");

        UpdateUserRequest? body;
        try
        {
            using var reader = new StreamReader(req.Body);
            var raw = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<UpdateUserRequest>(raw, AdminJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse UpdateUser body");
            body = null;
        }

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
        return await WriteJsonAsync(req, HttpStatusCode.OK, ToDto(updated!));
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
