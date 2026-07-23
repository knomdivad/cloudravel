using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based user and RBAC repository.
/// Uses admin connections since user data is cross-tenant.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(ITenantDbConnectionFactory connectionFactory, ILogger<UserRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private const string SelectColumns = @"
            SELECT user_id AS UserId, display_name AS DisplayName, email AS Email,
                   global_role AS GlobalRole, is_active AS IsActive,
                   created_at AS CreatedAt, last_login_at AS LastLoginAt,
                   auth_provider AS AuthProvider, username AS Username, password_hash AS PasswordHash
            FROM users";

    public async Task<User?> GetByIdAsync(Guid userId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = $"{SelectColumns} WHERE user_id = @UserId";

        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { UserId = userId });
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = $"{SelectColumns} WHERE email = @Email";

        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { Email = email });
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = $"{SelectColumns} WHERE username = @Username AND auth_provider = 'local'";

        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });
    }

    public async Task<User> UpsertAsync(User user)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE users AS target
            USING (SELECT @UserId AS user_id) AS source
            ON target.user_id = source.user_id
            WHEN MATCHED THEN UPDATE SET
                display_name = @DisplayName,
                email = @Email,
                last_login_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (user_id, display_name, email, global_role)
            VALUES
                (@UserId, @DisplayName, @Email, @GlobalRole);";

        await conn.ExecuteAsync(sql, new
        {
            user.UserId,
            user.DisplayName,
            user.Email,
            user.GlobalRole
        });

        _logger.LogDebug("Upserted user {UserId} ({Email})", user.UserId, user.Email);
        return (await GetByIdAsync(user.UserId))!;
    }

    public async Task UpdateLastLoginAsync(Guid userId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET last_login_at = SYSUTCDATETIME() WHERE user_id = @UserId",
            new { UserId = userId });
    }

    public async Task<IReadOnlyList<UserTenantAccess>> GetUserTenantAccessAsync(Guid userId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT uta.id AS Id, uta.user_id AS UserId, uta.tenant_id AS TenantId,
                   uta.role AS Role, uta.granted_at AS GrantedAt, uta.granted_by AS GrantedBy
            FROM user_tenant_access uta
            WHERE uta.user_id = @UserId
            ORDER BY uta.granted_at DESC";

        var results = await conn.QueryAsync<UserTenantAccess>(sql, new { UserId = userId });
        return results.ToList();
    }

    public async Task<bool> HasTenantAccessAsync(Guid userId, Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();

        // Admins have access to all tenants
        var user = await GetByIdAsync(userId);
        if (user?.GlobalRole == "admin") return true;

        const string sql = @"
            SELECT COUNT(*) FROM user_tenant_access 
            WHERE user_id = @UserId AND tenant_id = @TenantId";

        var count = await conn.ExecuteScalarAsync<int>(sql, new { UserId = userId, TenantId = tenantId });
        return count > 0;
    }

    public async Task GrantTenantAccessAsync(Guid userId, Guid tenantId, string role, Guid grantedBy)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE user_tenant_access AS target
            USING (SELECT @UserId AS user_id, @TenantId AS tenant_id) AS source
            ON target.user_id = source.user_id AND target.tenant_id = source.tenant_id
            WHEN MATCHED THEN UPDATE SET role = @Role, granted_by = @GrantedBy, granted_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (user_id, tenant_id, role, granted_by) VALUES (@UserId, @TenantId, @Role, @GrantedBy);";

        await conn.ExecuteAsync(sql, new { UserId = userId, TenantId = tenantId, Role = role, GrantedBy = grantedBy });
        _logger.LogInformation("Granted {Role} access to user {UserId} for tenant {TenantId}", role, userId, tenantId);
    }

    public async Task RevokeTenantAccessAsync(Guid userId, Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM user_tenant_access WHERE user_id = @UserId AND tenant_id = @TenantId",
            new { UserId = userId, TenantId = tenantId });
        _logger.LogInformation("Revoked access for user {UserId} to tenant {TenantId}", userId, tenantId);
    }
}
