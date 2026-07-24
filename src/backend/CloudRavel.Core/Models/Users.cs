namespace CloudRavel.Core.Models;

/// <summary>
/// Platform user (MSP or customer).
/// </summary>
public sealed class User
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GlobalRole { get; set; } = "member";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>"entra" (user_id is the Entra object ID) or "local" (username/password).</summary>
    public string AuthProvider { get; set; } = "entra";
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
}

/// <summary>Result of a successful local login: a signed JWT plus the user it belongs to.</summary>
public sealed class LocalAuthResult
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public User User { get; set; } = new();
}

/// <summary>
/// Maps a user to a tenant with a specific role.
/// </summary>
public sealed class UserTenantAccess
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Role { get; set; } = "read_only";
    public DateTime GrantedAt { get; set; }
    public Guid GrantedBy { get; set; }
}

/// <summary>
/// Immutable audit event.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}
