using CloudRavel.Core.Models;

namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Repository for user and RBAC operations.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid userId);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByUsernameAsync(string username);
    Task<User> UpsertAsync(User user);
    Task UpdateLastLoginAsync(Guid userId);
    Task<IReadOnlyList<UserTenantAccess>> GetUserTenantAccessAsync(Guid userId);
    Task<bool> HasTenantAccessAsync(Guid userId, Guid tenantId);
    Task GrantTenantAccessAsync(Guid userId, Guid tenantId, string role, Guid grantedBy);
    Task RevokeTenantAccessAsync(Guid userId, Guid tenantId);

    // --- RBAC / admin ---

    /// <summary>The caller's org role for a workspace (user_tenant_access.role), or null if none.</summary>
    Task<string?> GetTenantRoleAsync(Guid userId, Guid tenantId);

    /// <summary>All users (system-admin global listing).</summary>
    Task<IReadOnlyList<User>> ListAllAsync();

    /// <summary>Users with access to a workspace, paired with their org role.</summary>
    Task<IReadOnlyList<(User User, string Role)>> ListByTenantAsync(Guid tenantId);

    /// <summary>Create a local username/password user. Returns the created row.</summary>
    Task<User> CreateLocalUserAsync(User user, string passwordHash);

    Task SetPasswordAsync(Guid userId, string passwordHash);
    Task SetGlobalRoleAsync(Guid userId, string globalRole);
    Task SetActiveAsync(Guid userId, bool isActive);
}

/// <summary>
/// Repository for audit trail operations.
/// </summary>
public interface IAuditRepository
{
    Task LogAsync(AuditEvent auditEvent);
    Task<IReadOnlyList<AuditEvent>> GetByTenantAsync(Guid tenantId, int offset = 0, int limit = 50);
    Task<IReadOnlyList<AuditEvent>> GetByUserAsync(Guid userId, int offset = 0, int limit = 50);
}
