using AzureInventoryMonitor.Core.Models;

namespace AzureInventoryMonitor.Core.Interfaces;

/// <summary>
/// Repository for user and RBAC operations.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid userId);
    Task<User?> GetByEmailAsync(string email);
    Task<User> UpsertAsync(User user);
    Task UpdateLastLoginAsync(Guid userId);
    Task<IReadOnlyList<UserTenantAccess>> GetUserTenantAccessAsync(Guid userId);
    Task<bool> HasTenantAccessAsync(Guid userId, Guid tenantId);
    Task GrantTenantAccessAsync(Guid userId, Guid tenantId, string role, Guid grantedBy);
    Task RevokeTenantAccessAsync(Guid userId, Guid tenantId);
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
