namespace CloudRavel.Core.DTOs;

// --- System settings (system-admin only) ---

public sealed class SystemSettingsDto
{
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiModel { get; set; }
    /// <summary>True when an API key is stored — the key value itself is never returned.</summary>
    public bool ApiKeyConfigured { get; set; }
}

public sealed class UpdateSystemSettingsRequest
{
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiModel { get; set; }
    /// <summary>When present and non-empty, the new API key (written to the secret store, never echoed back).</summary>
    public string? OpenAiApiKey { get; set; }
}

// --- Users ---

public sealed class AdminUserDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GlobalRole { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string AuthProvider { get; set; } = string.Empty;
    public string? Username { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>The user's org role — only populated in the per-organization listing.</summary>
    public string? OrgRole { get; set; }
}

public sealed class AdminUsersResponse
{
    public IReadOnlyList<AdminUserDto> Users { get; set; } = [];
}

public sealed class CreateUserRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>system_admin | member (system-admin endpoint only; defaults to member).</summary>
    public string? GlobalRole { get; set; }
}

public sealed class UpdateUserRequest
{
    public string? GlobalRole { get; set; }
    public bool? IsActive { get; set; }
    public string? Password { get; set; }
}

// --- Org membership (org-admin only) ---

public sealed class AddOrgUserRequest
{
    /// <summary>Attach an existing user by username or email...</summary>
    public string? Username { get; set; }
    public string? Email { get; set; }
    /// <summary>...or, if no existing user matches, create a new local user with these + Password.</summary>
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    /// <summary>org_admin | cloud_admin | read_only.</summary>
    public string Role { get; set; } = "read_only";
}

public sealed class UpdateOrgUserRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

// --- Per-org SSO settings (org-admin only; stored, not yet enforced) ---

public sealed class OrgSsoDto
{
    public string Provider { get; set; } = "none";
    public string? IdpTenantId { get; set; }
    public string? IdpClientId { get; set; }
    public string? Domain { get; set; }
    public bool Enabled { get; set; }
    public bool ClientSecretConfigured { get; set; }
    /// <summary>
    /// Per-org token federation is stored but not enforced yet.
    /// Always "not_implemented" until multi-issuer validation ships.
    /// </summary>
    public string EnforcementStatus { get; set; } = "not_implemented";
}

public sealed class UpdateOrgSsoRequest
{
    public string Provider { get; set; } = "none";
    public string? IdpTenantId { get; set; }
    public string? IdpClientId { get; set; }
    public string? Domain { get; set; }
    public bool Enabled { get; set; }
    /// <summary>When present and non-empty, written to the secret store; never echoed back.</summary>
    public string? ClientSecret { get; set; }
}

// --- /api/auth/me ---

public sealed class MeDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string SystemRole { get; set; } = "member";
}
