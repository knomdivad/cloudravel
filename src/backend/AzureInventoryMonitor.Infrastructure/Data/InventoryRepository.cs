using System.Text.Json;
using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based inventory repository.
/// All queries run through tenant-scoped connections (RLS enforced).
/// </summary>
public sealed class InventoryRepository : IInventoryRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<InventoryRepository> _logger;

    public InventoryRepository(ITenantDbConnectionFactory connectionFactory, ILogger<InventoryRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<InventorySnapshot> CreateSnapshotAsync(Guid tenantId, string triggeredBy)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            INSERT INTO inventory_snapshots (tenant_id, started_at, status, triggered_by)
            OUTPUT INSERTED.snapshot_id, INSERTED.tenant_id, INSERTED.started_at, INSERTED.status, INSERTED.triggered_by
            VALUES (@TenantId, SYSUTCDATETIME(), 'running', @TriggeredBy)";

        var snapshot = await conn.QuerySingleAsync<InventorySnapshot>(sql, new { TenantId = tenantId, TriggeredBy = triggeredBy });
        _logger.LogInformation("Created snapshot {SnapshotId} for tenant {TenantId}", snapshot.SnapshotId, tenantId);
        return snapshot;
    }

    public async Task CompleteSnapshotAsync(long snapshotId, int resourceCount, string blobPath)
    {
        // Need admin connection to update without tenant context issues during ingestion
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE inventory_snapshots 
            SET completed_at = SYSUTCDATETIME(), status = 'completed', resource_count = @ResourceCount, blob_path = @BlobPath
            WHERE snapshot_id = @SnapshotId";

        await conn.ExecuteAsync(sql, new { SnapshotId = snapshotId, ResourceCount = resourceCount, BlobPath = blobPath });
    }

    public async Task FailSnapshotAsync(long snapshotId, string errorMessage)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE inventory_snapshots 
            SET completed_at = SYSUTCDATETIME(), status = 'failed', error_message = @ErrorMessage
            WHERE snapshot_id = @SnapshotId";

        await conn.ExecuteAsync(sql, new { SnapshotId = snapshotId, ErrorMessage = errorMessage });
    }

    public async Task BulkInsertResourcesAsync(long snapshotId, IEnumerable<InventoryResource> resources)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO inventory_resources 
            (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name,
             location, sku_name, sku_tier, sku_capacity, tags, identity_type, identity_principal_ids,
             properties_json, networking_json, security_config_json)
            VALUES 
            (@TenantId, @SnapshotId, @ResourceId, @SubscriptionId, @ResourceGroup, @ResourceType, @ResourceName,
             @Location, @SkuName, @SkuTier, @SkuCapacity, @TagsJson, @IdentityType, @IdentityPrincipalIdsJson,
             @PropertiesJson, @NetworkingJson, @SecurityConfigJson)";

        var batch = resources.Select(r => new
        {
            r.TenantId,
            r.SnapshotId,
            r.ResourceId,
            r.SubscriptionId,
            r.ResourceGroup,
            r.ResourceType,
            r.ResourceName,
            r.Location,
            r.SkuName,
            r.SkuTier,
            r.SkuCapacity,
            TagsJson = r.Tags != null ? JsonSerializer.Serialize(r.Tags) : null,
            r.IdentityType,
            IdentityPrincipalIdsJson = r.IdentityPrincipalIds != null ? JsonSerializer.Serialize(r.IdentityPrincipalIds) : null,
            r.PropertiesJson,
            r.NetworkingJson,
            r.SecurityConfigJson
        });

        // Dapper handles batch operations efficiently
        var count = await conn.ExecuteAsync(sql, batch);
        _logger.LogInformation("Inserted {Count} resources for snapshot {SnapshotId}", count, snapshotId);
    }

    public async Task SetLatestSnapshotAsync(Guid tenantId, long snapshotId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE latest_snapshots AS target
            USING (SELECT @TenantId AS tenant_id, @SnapshotId AS snapshot_id) AS source
            ON target.tenant_id = source.tenant_id
            WHEN MATCHED THEN UPDATE SET snapshot_id = source.snapshot_id, updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (tenant_id, snapshot_id) VALUES (source.tenant_id, source.snapshot_id);";

        await conn.ExecuteAsync(sql, new { TenantId = tenantId, SnapshotId = snapshotId });
    }

    public async Task<InventorySnapshot?> GetLatestSnapshotAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            SELECT s.* FROM inventory_snapshots s
            INNER JOIN latest_snapshots ls ON s.snapshot_id = ls.snapshot_id
            WHERE s.tenant_id = @TenantId";

        return await conn.QuerySingleOrDefaultAsync<InventorySnapshot>(sql, new { TenantId = tenantId });
    }

    public async Task<InventorySnapshot?> GetSnapshotByIdAsync(long snapshotId)
    {
        // Uses admin connection since the caller determines tenant context
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = "SELECT * FROM inventory_snapshots WHERE snapshot_id = @SnapshotId";
        return await conn.QuerySingleOrDefaultAsync<InventorySnapshot>(sql, new { SnapshotId = snapshotId });
    }

    public async Task<IReadOnlyList<InventorySnapshot>> GetSnapshotHistoryAsync(Guid tenantId, int limit = 50)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            SELECT TOP(@Limit) * FROM inventory_snapshots 
            WHERE tenant_id = @TenantId 
            ORDER BY started_at DESC";

        var results = await conn.QueryAsync<InventorySnapshot>(sql, new { TenantId = tenantId, Limit = limit });
        return results.ToList();
    }

    public async Task<IReadOnlyList<InventoryResource>> GetResourcesAsync(
        Guid tenantId, long? snapshotId = null, string? resourceType = null,
        string? subscriptionId = null, string? resourceGroup = null,
        int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        // Use latest snapshot if not specified
        var effectiveSnapshotId = snapshotId;
        if (effectiveSnapshotId == null)
        {
            const string latestSql = "SELECT snapshot_id FROM latest_snapshots WHERE tenant_id = @TenantId";
            effectiveSnapshotId = await conn.QuerySingleOrDefaultAsync<long?>(latestSql, new { TenantId = tenantId });
            if (effectiveSnapshotId == null) return [];
        }

        var sql = @"
            SELECT * FROM inventory_resources
            WHERE tenant_id = @TenantId AND snapshot_id = @SnapshotId";

        if (!string.IsNullOrEmpty(resourceType))
            sql += " AND resource_type = @ResourceType";
        if (!string.IsNullOrEmpty(subscriptionId))
            sql += " AND subscription_id = @SubscriptionId";
        if (!string.IsNullOrEmpty(resourceGroup))
            sql += " AND resource_group = @ResourceGroup";

        sql += " ORDER BY resource_type, resource_name OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var results = await conn.QueryAsync<InventoryResource>(sql, new
        {
            TenantId = tenantId,
            SnapshotId = effectiveSnapshotId,
            ResourceType = resourceType,
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            Offset = offset,
            Limit = limit
        });

        return results.ToList();
    }

    public async Task<InventoryResource?> GetResourceByIdAsync(Guid tenantId, string resourceId, long? snapshotId = null)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        string sql;
        if (snapshotId.HasValue)
        {
            sql = @"SELECT * FROM inventory_resources 
                    WHERE tenant_id = @TenantId AND resource_id = @ResourceId AND snapshot_id = @SnapshotId";
        }
        else
        {
            sql = @"SELECT ir.* FROM inventory_resources ir
                    INNER JOIN latest_snapshots ls ON ir.tenant_id = ls.tenant_id AND ir.snapshot_id = ls.snapshot_id
                    WHERE ir.tenant_id = @TenantId AND ir.resource_id = @ResourceId";
        }

        return await conn.QuerySingleOrDefaultAsync<InventoryResource>(sql, new
        {
            TenantId = tenantId,
            ResourceId = resourceId,
            SnapshotId = snapshotId
        });
    }

    public async Task<int> GetResourceCountAsync(Guid tenantId, long? snapshotId = null)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        if (snapshotId.HasValue)
        {
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM inventory_resources WHERE tenant_id = @TenantId AND snapshot_id = @SnapshotId",
                new { TenantId = tenantId, SnapshotId = snapshotId });
        }

        return await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM inventory_resources ir
              INNER JOIN latest_snapshots ls ON ir.tenant_id = ls.tenant_id AND ir.snapshot_id = ls.snapshot_id
              WHERE ir.tenant_id = @TenantId",
            new { TenantId = tenantId });
    }

    public async Task<IReadOnlyList<ResourceTypeSummary>> GetResourceTypeSummaryAsync(Guid tenantId, long? snapshotId = null)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = snapshotId.HasValue
            ? @"SELECT resource_type AS ResourceType, COUNT(*) AS Count 
                FROM inventory_resources WHERE tenant_id = @TenantId AND snapshot_id = @SnapshotId
                GROUP BY resource_type ORDER BY Count DESC"
            : @"SELECT ir.resource_type AS ResourceType, COUNT(*) AS Count 
                FROM inventory_resources ir
                INNER JOIN latest_snapshots ls ON ir.tenant_id = ls.tenant_id AND ir.snapshot_id = ls.snapshot_id
                WHERE ir.tenant_id = @TenantId
                GROUP BY ir.resource_type ORDER BY Count DESC";

        var results = await conn.QueryAsync<ResourceTypeSummary>(sql, new { TenantId = tenantId, SnapshotId = snapshotId });
        return results.ToList();
    }
}
