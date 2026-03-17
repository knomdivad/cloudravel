using System.Text.Json;
using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based change repository.
/// Read queries use tenant-scoped connections (RLS enforced).
/// Write queries use admin connections for cross-tenant ingestion.
/// </summary>
public sealed class ChangeRepository : IChangeRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<ChangeRepository> _logger;

    public ChangeRepository(ITenantDbConnectionFactory connectionFactory, ILogger<ChangeRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task UpsertChangesAsync(IEnumerable<ResourceChange> changes)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE resource_changes AS target
            USING (SELECT @TenantId AS tenant_id, @ChangeId AS change_id) AS source
            ON target.tenant_id = source.tenant_id AND target.change_id = source.change_id
            WHEN NOT MATCHED THEN INSERT 
                (tenant_id, change_id, resource_id, resource_type, change_type, detected_at,
                 changed_properties, actor_type, actor_id, actor_name, client_type,
                 classification, severity)
            VALUES 
                (@TenantId, @ChangeId, @ResourceId, @ResourceType, @ChangeType, @DetectedAt,
                 @ChangedPropertiesJson, @ActorType, @ActorId, @ActorName, @ClientType,
                 @Classification, @Severity);";

        var batch = changes.Select(c => new
        {
            c.TenantId,
            c.ChangeId,
            c.ResourceId,
            c.ResourceType,
            ChangeType = c.ChangeType.ToString(),
            c.DetectedAt,
            ChangedPropertiesJson = c.ChangedProperties != null
                ? JsonSerializer.Serialize(c.ChangedProperties)
                : null,
            c.ActorType,
            c.ActorId,
            c.ActorName,
            c.ClientType,
            Classification = c.Classification.ToString().ToLowerInvariant(),
            Severity = c.Severity?.ToString().ToLowerInvariant()
        });

        var count = await conn.ExecuteAsync(sql, batch);
        _logger.LogDebug("Upserted {Count} changes", count);
    }

    public async Task<IReadOnlyList<ResourceChange>> GetChangesAsync(
        Guid tenantId, DateTime? from = null, DateTime? to = null,
        string? resourceId = null, ChangeClassification? classification = null,
        int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = @"
            SELECT id AS Id, tenant_id AS TenantId, change_id AS ChangeId, resource_id AS ResourceId,
                   resource_type AS ResourceType, change_type AS ChangeType, detected_at AS DetectedAt,
                   ingested_at AS IngestedAt, changed_properties AS ChangedPropertiesJson,
                   actor_type AS ActorType, actor_id AS ActorId, actor_name AS ActorName,
                   client_type AS ClientType, classification AS Classification, severity AS Severity
            FROM resource_changes
            WHERE tenant_id = @TenantId";

        if (from.HasValue)
            sql += " AND detected_at >= @From";
        if (to.HasValue)
            sql += " AND detected_at <= @To";
        if (!string.IsNullOrEmpty(resourceId))
            sql += " AND resource_id = @ResourceId";
        if (classification.HasValue)
            sql += " AND classification = @Classification";

        sql += " ORDER BY detected_at DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<ChangeRow>(sql, new
        {
            TenantId = tenantId,
            From = from,
            To = to,
            ResourceId = resourceId,
            Classification = classification?.ToString().ToLowerInvariant(),
            Offset = offset,
            Limit = limit
        });

        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<ResourceChange>> GetRecentChangesAsync(Guid tenantId, int hours = 24, int limit = 100)
    {
        var from = DateTime.UtcNow.AddHours(-hours);
        return await GetChangesAsync(tenantId, from, limit: limit);
    }

    public async Task<int> GetChangeCountAsync(Guid tenantId, DateTime? from = null, DateTime? to = null)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = "SELECT COUNT(*) FROM resource_changes WHERE tenant_id = @TenantId";
        if (from.HasValue)
            sql += " AND detected_at >= @From";
        if (to.HasValue)
            sql += " AND detected_at <= @To";

        return await conn.ExecuteScalarAsync<int>(sql, new { TenantId = tenantId, From = from, To = to });
    }

    public async Task<IReadOnlyList<ChangeTimeline>> GetChangeTimelineAsync(Guid tenantId, DateTime from, DateTime to)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        // Auto-determine bucket size based on range
        var range = to - from;
        var bucketSql = range.TotalDays switch
        {
            <= 1 => "DATEADD(HOUR, DATEDIFF(HOUR, 0, detected_at), 0)",     // Hourly buckets
            <= 14 => "DATEADD(DAY, DATEDIFF(DAY, 0, detected_at), 0)",       // Daily buckets
            _ => "DATEADD(WEEK, DATEDIFF(WEEK, 0, detected_at), 0)"          // Weekly buckets
        };

        var sql = $@"
            SELECT {bucketSql} AS BucketStart,
                   COUNT(*) AS TotalChanges,
                   SUM(CASE WHEN classification = 'security' THEN 1 ELSE 0 END) AS SecurityChanges,
                   SUM(CASE WHEN classification = 'governance' THEN 1 ELSE 0 END) AS GovernanceChanges,
                   SUM(CASE WHEN classification = 'cost' THEN 1 ELSE 0 END) AS CostChanges,
                   SUM(CASE WHEN classification = 'operational' THEN 1 ELSE 0 END) AS OperationalChanges
            FROM resource_changes
            WHERE tenant_id = @TenantId AND detected_at >= @From AND detected_at <= @To
            GROUP BY {bucketSql}
            ORDER BY BucketStart";

        var results = await conn.QueryAsync<ChangeTimeline>(sql, new { TenantId = tenantId, From = from, To = to });
        return results.ToList();
    }

    private static ResourceChange MapRow(ChangeRow row)
    {
        var change = new ResourceChange
        {
            Id = row.Id,
            TenantId = row.TenantId,
            ChangeId = row.ChangeId,
            ResourceId = row.ResourceId,
            ResourceType = row.ResourceType,
            DetectedAt = row.DetectedAt,
            IngestedAt = row.IngestedAt,
            ActorType = row.ActorType,
            ActorId = row.ActorId,
            ActorName = row.ActorName,
            ClientType = row.ClientType
        };

        // Parse enums
        if (Enum.TryParse<ChangeType>(row.ChangeType, true, out var ct))
            change.ChangeType = ct;
        if (Enum.TryParse<ChangeClassification>(row.Classification, true, out var cc))
            change.Classification = cc;
        if (!string.IsNullOrEmpty(row.Severity) && Enum.TryParse<ChangeSeverity>(row.Severity, true, out var cs))
            change.Severity = cs;

        // Parse JSON properties
        if (!string.IsNullOrEmpty(row.ChangedPropertiesJson))
        {
            try
            {
                change.ChangedProperties = JsonSerializer.Deserialize<List<PropertyChange>>(
                    row.ChangedPropertiesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Malformed JSON — leave as null
            }
        }

        return change;
    }

    /// <summary>
    /// Internal row type for Dapper mapping from SQL columns.
    /// </summary>
    private sealed class ChangeRow
    {
        public long Id { get; set; }
        public Guid TenantId { get; set; }
        public string ChangeId { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public DateTime DetectedAt { get; set; }
        public DateTime IngestedAt { get; set; }
        public string? ChangedPropertiesJson { get; set; }
        public string? ActorType { get; set; }
        public string? ActorId { get; set; }
        public string? ActorName { get; set; }
        public string? ClientType { get; set; }
        public string Classification { get; set; } = string.Empty;
        public string? Severity { get; set; }
    }
}
