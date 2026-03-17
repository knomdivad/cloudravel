using System.Text.Json;
using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based audit trail repository.
/// Writes use admin connections (audit events span tenants).
/// Reads use admin connections for cross-tenant audit queries.
/// </summary>
public sealed class AuditRepository : IAuditRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<AuditRepository> _logger;

    public AuditRepository(ITenantDbConnectionFactory connectionFactory, ILogger<AuditRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task LogAsync(AuditEvent auditEvent)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO audit_events (tenant_id, user_id, action, entity_type, entity_id, details_json, client_ip, user_agent)
            VALUES (@TenantId, @UserId, @Action, @EntityType, @EntityId, @DetailsJson, @ClientIp, @UserAgent)";

        await conn.ExecuteAsync(sql, new
        {
            auditEvent.TenantId,
            auditEvent.UserId,
            auditEvent.Action,
            auditEvent.EntityType,
            auditEvent.EntityId,
            auditEvent.DetailsJson,
            auditEvent.ClientIp,
            auditEvent.UserAgent
        });

        _logger.LogDebug("Audit: {Action} by {UserId} on {EntityType}/{EntityId}",
            auditEvent.Action, auditEvent.UserId, auditEvent.EntityType, auditEvent.EntityId);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetByTenantAsync(Guid tenantId, int offset = 0, int limit = 50)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT id AS Id, tenant_id AS TenantId, user_id AS UserId, action AS Action,
                   entity_type AS EntityType, entity_id AS EntityId, details_json AS DetailsJson,
                   client_ip AS ClientIp, user_agent AS UserAgent, created_at AS CreatedAt
            FROM audit_events
            WHERE tenant_id = @TenantId
            ORDER BY created_at DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var results = await conn.QueryAsync<AuditEvent>(sql,
            new { TenantId = tenantId, Offset = offset, Limit = limit });
        return results.ToList();
    }

    public async Task<IReadOnlyList<AuditEvent>> GetByUserAsync(Guid userId, int offset = 0, int limit = 50)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT id AS Id, tenant_id AS TenantId, user_id AS UserId, action AS Action,
                   entity_type AS EntityType, entity_id AS EntityId, details_json AS DetailsJson,
                   client_ip AS ClientIp, user_agent AS UserAgent, created_at AS CreatedAt
            FROM audit_events
            WHERE user_id = @UserId
            ORDER BY created_at DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var results = await conn.QueryAsync<AuditEvent>(sql,
            new { UserId = userId, Offset = offset, Limit = limit });
        return results.ToList();
    }
}
