using Dapper;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

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
        // FirstOrDefault: email is not unique in the schema; Single throws (500) when
        // multiple rows share an address (common after blank-email creates + retries).
        var sql = $"{SelectColumns} WHERE email = @Email ORDER BY created_at";

        return await conn.QueryFirstOrDefaultAsync<User>(sql, new { Email = email });
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
        // Never elevates global_role on MATCH — only seeds it on insert (Entra JIT → member).
        const string sql = @"
            MERGE users AS target
            USING (SELECT @UserId AS user_id) AS source
            ON target.user_id = source.user_id
            WHEN MATCHED THEN UPDATE SET
                display_name = @DisplayName,
                email = @Email,
                last_login_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (user_id, display_name, email, global_role, is_active, auth_provider)
            VALUES
                (@UserId, @DisplayName, @Email, @GlobalRole, 1, @AuthProvider);";

        await conn.ExecuteAsync(sql, new
        {
            user.UserId,
            user.DisplayName,
            user.Email,
            user.GlobalRole,
            AuthProvider = string.IsNullOrWhiteSpace(user.AuthProvider) ? "entra" : user.AuthProvider
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

        // System admins have access to all workspaces.
        var user = await GetByIdAsync(userId);
        if (user?.GlobalRole == "system_admin") return true;

        const string sql = @"
            SELECT COUNT(*) FROM user_tenant_access
            WHERE user_id = @UserId AND tenant_id = @TenantId";

        var count = await conn.ExecuteScalarAsync<int>(sql, new { UserId = userId, TenantId = tenantId });
        return count > 0;
    }

    public async Task<string?> GetTenantRoleAsync(Guid userId, Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT role FROM user_tenant_access WHERE user_id = @UserId AND tenant_id = @TenantId",
            new { UserId = userId, TenantId = tenantId });
    }

    public async Task<IReadOnlyList<User>> ListAllAsync()
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var results = await conn.QueryAsync<User>($"{SelectColumns} ORDER BY display_name");
        return results.ToList();
    }

    public async Task<IReadOnlyList<(User User, string Role)>> ListByTenantAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT u.user_id AS UserId, u.display_name AS DisplayName, u.email AS Email,
                   u.global_role AS GlobalRole, u.is_active AS IsActive,
                   u.created_at AS CreatedAt, u.last_login_at AS LastLoginAt,
                   u.auth_provider AS AuthProvider, u.username AS Username, u.password_hash AS PasswordHash,
                   uta.role AS Role
            FROM user_tenant_access uta
            INNER JOIN users u ON u.user_id = uta.user_id
            WHERE uta.tenant_id = @TenantId
            ORDER BY u.display_name";

        var rows = await conn.QueryAsync<User, string, (User, string)>(
            sql, (u, role) => (u, role), new { TenantId = tenantId }, splitOn: "Role");
        return rows.ToList();
    }

    public async Task<User> CreateLocalUserAsync(User user, string passwordHash)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        user.UserId = user.UserId == Guid.Empty ? Guid.NewGuid() : user.UserId;
        const string sql = @"
            INSERT INTO users (user_id, display_name, email, global_role, is_active,
                               auth_provider, username, password_hash)
            VALUES (@UserId, @DisplayName, @Email, @GlobalRole, 1, 'local', @Username, @PasswordHash)";
        await conn.ExecuteAsync(sql, new
        {
            user.UserId,
            user.DisplayName,
            user.Email,
            user.GlobalRole,
            user.Username,
            PasswordHash = passwordHash
        });
        _logger.LogInformation("Created local user {UserId} ({Username})", user.UserId, user.Username);
        return (await GetByIdAsync(user.UserId))!;
    }

    public async Task SetPasswordAsync(Guid userId, string passwordHash)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET password_hash = @PasswordHash WHERE user_id = @UserId AND auth_provider = 'local'",
            new { UserId = userId, PasswordHash = passwordHash });
    }

    public async Task SetGlobalRoleAsync(Guid userId, string globalRole)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET global_role = @GlobalRole WHERE user_id = @UserId",
            new { UserId = userId, GlobalRole = globalRole });
    }

    public async Task SetActiveAsync(Guid userId, bool isActive)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET is_active = @IsActive WHERE user_id = @UserId",
            new { UserId = userId, IsActive = isActive });
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
