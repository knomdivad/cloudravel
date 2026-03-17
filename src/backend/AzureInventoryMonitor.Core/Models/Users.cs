namespace AzureInventoryMonitor.Core.Models;

/// <summary>
/// Platform user (MSP or customer).
/// </summary>
public sealed class User
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GlobalRole { get; set; } = "operator";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Maps a user to a tenant with a specific role.
/// </summary>
public sealed class UserTenantAccess
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Role { get; set; } = "operator";
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
