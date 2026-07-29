using System.Net;
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
/// Organization-admin endpoints: manage the org's members (grant/change/revoke
/// roles, create local users) and its SSO settings. Every endpoint requires the
/// org_admin role in the request's workspace (system_admin passes too). The
/// path {orgId} must match the X-Tenant-Id the role check is evaluated against.
/// </summary>
public sealed class OrgAdminFunctions
{
    private static readonly string[] ValidOrgRoles = { OrgRole.OrgAdmin, OrgRole.CloudAdmin, OrgRole.ReadOnly };

    private readonly IUserRepository _userRepo;
    private readonly IOrgSsoRepository _ssoRepo;
    private readonly IOrganizationRepository _orgRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<OrgAdminFunctions> _logger;

    public OrgAdminFunctions(
        IUserRepository userRepo,
        IOrgSsoRepository ssoRepo,
        IOrganizationRepository orgRepo,
        ITenantRepository tenantRepo,
        ILogger<OrgAdminFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _userRepo = userRepo;
        _ssoRepo = ssoRepo;
        _orgRepo = orgRepo;
        _tenantRepo = tenantRepo;
        _secretStore = secretStore;
        _logger = logger;
    }

    // ========================================================================
    // Members
    // ========================================================================

    [Function("ListOrgUsers")]
    public async Task<HttpResponseData> ListOrgUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "organizations/{orgId:guid}/users")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        var members = await _userRepo.ListByTenantAsync(orgId);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new AdminUsersResponse
        {
            Users = members.Select(m =>
            {
                var dto = AdminFunctions.ToDto(m.User);
                dto.OrgRole = m.Role;
                return dto;
            }).ToList()
        });
        return response;
    }

    [Function("AddOrgUser")]
    public async Task<HttpResponseData> AddOrgUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "organizations/{orgId:guid}/users")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        var body = await req.ReadFromJsonAsync<AddOrgUserRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Role))
            return await BadRequest(req, "INVALID_REQUEST", "role is required.");
        if (!ValidOrgRoles.Contains(body.Role))
            return await BadRequest(req, "INVALID_ROLE", $"role must be one of: {string.Join(", ", ValidOrgRoles)}");

        // Password present ⇒ create a new local user. Otherwise attach an existing one by email.
        var isCreate = !string.IsNullOrWhiteSpace(body.Password);
        User? user;

        if (isCreate)
        {
            if (string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.Email))
                return await BadRequest(req, "INVALID_REQUEST",
                    "displayName, email, and password are required to create a new local user.");

            var email = body.Email.Trim().ToLowerInvariant();
            if (!email.Contains('@', StringComparison.Ordinal))
                return await BadRequest(req, "INVALID_EMAIL",
                    "email must be a valid email address (also used as login username).");

            if (await _userRepo.GetByEmailAsync(email) != null
                || await _userRepo.GetByUsernameAsync(email) != null)
                return await Conflict(req, "EMAIL_TAKEN", "That email is already in use.");

            try
            {
                user = await _userRepo.CreateLocalUserAsync(new User
                {
                    UserId = Guid.NewGuid(),
                    DisplayName = body.DisplayName.Trim(),
                    Email = email,
                    Username = email,
                    GlobalRole = SystemRole.Member
                }, PasswordHasher.Hash(body.Password!));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create local user '{Email}'", email);
                return await BadRequest(req, "USER_CREATE_FAILED",
                    $"Could not create user. Detail: {ex.Message}");
            }
        }
        else
        {
            // Attach existing user by email (login identity). Username field is treated as email alias.
            var identity = !string.IsNullOrWhiteSpace(body.Email)
                ? body.Email.Trim()
                : body.Username?.Trim();
            if (string.IsNullOrWhiteSpace(identity))
                return await BadRequest(req, "INVALID_REQUEST", "email is required to attach an existing user.");

            user = await _userRepo.GetByEmailAsync(identity)
                ?? await _userRepo.GetByUsernameAsync(identity);
            if (user == null)
                return await BadRequest(req, "USER_NOT_FOUND",
                    "No existing user matched that email. To create a new local user, include a password.");
        }

        var grantedBy = context.GetUserId() ?? Guid.Empty;
        try
        {
            // user_tenant_access FK → tenants; materialize shell if missing.
            await EnsureWorkspaceTenantShellAsync(orgId, grantedBy);
            await _userRepo.GrantTenantAccessAsync(user.UserId, orgId, body.Role, grantedBy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to grant {Role} on org {OrgId} to user {UserId}", body.Role, orgId, user.UserId);
            return await BadRequest(req, "GRANT_FAILED",
                "Could not grant org access. Run database/repair-org-workspace-shell.sql if this org " +
                $"is missing a tenants workspace row. Detail: {ex.Message}");
        }

        var dto = AdminFunctions.ToDto(user);
        dto.OrgRole = body.Role;
        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(dto);
        return response;
    }

    [Function("UpdateOrgUserRole")]
    public async Task<HttpResponseData> UpdateOrgUserRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "organizations/{orgId:guid}/users/{userId:guid}")] HttpRequestData req,
        Guid orgId,
        Guid userId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        var body = await req.ReadFromJsonAsync<UpdateOrgUserRoleRequest>();
        if (body == null || !ValidOrgRoles.Contains(body.Role))
            return await BadRequest(req, "INVALID_ROLE", $"role must be one of: {string.Join(", ", ValidOrgRoles)}");

        var grantedBy = context.GetUserId() ?? Guid.Empty;
        await _userRepo.GrantTenantAccessAsync(userId, orgId, body.Role, grantedBy);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { userId, orgId, role = body.Role });
        return response;
    }

    [Function("RemoveOrgUser")]
    public async Task<HttpResponseData> RemoveOrgUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "organizations/{orgId:guid}/users/{userId:guid}")] HttpRequestData req,
        Guid orgId,
        Guid userId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        await _userRepo.RevokeTenantAccessAsync(userId, orgId);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { userId, orgId, removed = true });
        return response;
    }

    // ========================================================================
    // SSO settings (stored; enforcement is a follow-up)
    // ========================================================================

    [Function("GetOrgSso")]
    public async Task<HttpResponseData> GetOrgSso(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "organizations/{orgId:guid}/sso")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        var s = await _ssoRepo.GetAsync(orgId);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new OrgSsoDto
        {
            Provider = s?.Provider ?? "none",
            IdpTenantId = s?.IdpTenantId,
            IdpClientId = s?.IdpClientId,
            Domain = s?.Domain,
            Enabled = s?.Enabled ?? false,
            ClientSecretConfigured = !string.IsNullOrWhiteSpace(s?.ClientSecretName),
            EnforcementStatus = "not_implemented"
        });
        return response;
    }

    [Function("UpdateOrgSso")]
    public async Task<HttpResponseData> UpdateOrgSso(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "organizations/{orgId:guid}/sso")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var gate = await Gate(req, context, orgId);
        if (gate != null) return gate;

        var body = await req.ReadFromJsonAsync<UpdateOrgSsoRequest>();
        if (body == null) return await BadRequest(req, "INVALID_REQUEST", "An SSO settings body is required.");
        var provider = body.Provider?.ToLowerInvariant() ?? "none";
        if (provider is not ("none" or "entra" or "oidc"))
            return await BadRequest(req, "INVALID_PROVIDER", "provider must be none, entra, or oidc.");

        var existing = await _ssoRepo.GetAsync(orgId);
        var secretName = existing?.ClientSecretName;
        if (!string.IsNullOrWhiteSpace(body.ClientSecret))
        {
            if (_secretStore == null)
                return await BadRequest(req, "SECRETSTORE_UNAVAILABLE",
                    "A secret store (OpenBao) is not configured, so the SSO client secret cannot be stored securely.");
            secretName = $"org/{orgId}/sso-secret";
            await _secretStore.SetSecretAsync(secretName, body.ClientSecret.Trim());
        }

        var actor = context.GetUserId()?.ToString() ?? "system";
        await _ssoRepo.UpsertAsync(new OrgSsoSettings
        {
            OrgId = orgId,
            Provider = provider,
            IdpTenantId = Trim(body.IdpTenantId),
            IdpClientId = Trim(body.IdpClientId),
            Domain = Trim(body.Domain),
            ClientSecretName = secretName,
            Enabled = body.Enabled
        }, actor);

        return await GetOrgSso(req, orgId, context);
    }

    // ---- helpers ----

    /// <summary>
    /// org_admin gate + path/header consistency: the {orgId} in the route must
    /// equal the X-Tenant-Id the role was evaluated against, so the role check
    /// can't be bypassed by pointing the path at a different org.
    /// </summary>
    private static async Task<HttpResponseData?> Gate(HttpRequestData req, FunctionContext context, Guid orgId)
    {
        if (context.TryGetTenantId() != orgId)
            return await BadRequest(req, "TENANT_MISMATCH", "The X-Tenant-Id header must match the organization in the path.");
        return await context.RequireOrgRoleAsync(req, OrgRole.OrgAdmin);
    }

    /// <summary>
    /// Ensures a <c>tenants</c> row exists for this org_id so membership grants
    /// (FK to tenants) succeed even if the org was created before shell rows.
    /// </summary>
    private async Task EnsureWorkspaceTenantShellAsync(Guid orgId, Guid createdBy)
    {
        if (await _tenantRepo.GetByIdAsync(orgId) != null) return;

        var org = await _orgRepo.GetByIdAsync(orgId);
        var name = org?.Name ?? orgId.ToString();
        await _tenantRepo.CreateAsync(new Tenant
        {
            TenantId = orgId,
            DisplayName = name,
            AzureTenantId = WorkspaceTenantPlaceholders.AzureTenantId,
            OnboardingMethod = OnboardingMethod.Lighthouse,
            Status = TenantStatus.Active,
        }, createdBy == Guid.Empty ? Guid.Empty : createdBy);
        _logger.LogInformation("Materialized workspace tenants shell for org {OrgId}", orgId);
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Conflict);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }
}
