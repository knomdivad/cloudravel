using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based incident + timeline repository.
/// Incident creation happens from the anomaly scan worker (admin connection)
/// and from operators via the API (tenant-scoped connection).
/// </summary>
public sealed class IncidentRepository : IIncidentRepository
{
    private const string SelectColumns = @"
        SELECT i.id AS Id, i.tenant_id AS TenantId, i.title AS Title, i.severity AS Severity,
               i.status AS Status, i.source AS Source, i.summary_markdown AS SummaryMarkdown,
               i.assigned_to AS AssignedTo, i.created_at AS CreatedAt,
               i.acknowledged_at AS AcknowledgedAt, i.mitigated_at AS MitigatedAt,
               i.resolved_at AS ResolvedAt, i.closed_at AS ClosedAt, i.sla_due_at AS SlaDueAt,
               (SELECT COUNT(*) FROM anomalies a WHERE a.incident_id = i.id) AS AnomalyCount,
               (SELECT COUNT(*) FROM remediation_actions r WHERE r.incident_id = i.id) AS RemediationCount
        FROM incidents i";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<IncidentRepository> _logger;

    public IncidentRepository(ITenantDbConnectionFactory connectionFactory, ILogger<IncidentRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<long> CreateAsync(Incident incident)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO incidents (tenant_id, title, severity, status, source, summary_markdown,
                                   assigned_to, sla_due_at)
            OUTPUT inserted.id
            VALUES (@TenantId, @Title, @Severity, @Status, @Source, @SummaryMarkdown,
                    @AssignedTo, @SlaDueAt)";

        var id = await conn.ExecuteScalarAsync<long>(sql, new
        {
            incident.TenantId,
            incident.Title,
            Severity = incident.Severity.ToString(),
            Status = incident.Status.ToString(),
            incident.Source,
            incident.SummaryMarkdown,
            incident.AssignedTo,
            incident.SlaDueAt
        });

        incident.Id = id;
        _logger.LogInformation("Created incident {IncidentId} ({Severity}) for tenant {TenantId}: {Title}",
            id, incident.Severity, incident.TenantId, incident.Title);
        return id;
    }

    public async Task<Incident?> GetByIdAsync(Guid tenantId, long incidentId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE i.tenant_id = @TenantId AND i.id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Incident>(sql, new { TenantId = tenantId, Id = incidentId });
    }

    public async Task<IReadOnlyList<Incident>> GetIncidentsAsync(
        Guid tenantId, IncidentStatus? status = null, AnomalySeverity? severity = null,
        int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = SelectColumns + " WHERE i.tenant_id = @TenantId";
        if (status.HasValue) sql += " AND i.status = @Status";
        if (severity.HasValue) sql += " AND i.severity = @Severity";
        sql += " ORDER BY i.created_at DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<Incident>(sql, new
        {
            TenantId = tenantId,
            Status = status?.ToString(),
            Severity = severity?.ToString(),
            Offset = offset,
            Limit = limit
        });
        return rows.ToList();
    }

    public async Task<int> GetOpenCountAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM incidents WHERE tenant_id = @TenantId AND status IN ('Open', 'Acknowledged', 'Mitigated')",
            new { TenantId = tenantId });
    }

    public async Task UpdateStatusAsync(Guid tenantId, long incidentId, IncidentStatus status, string? actor)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            UPDATE incidents SET
                status = @Status,
                acknowledged_at = CASE WHEN @Status = 'Acknowledged' AND acknowledged_at IS NULL THEN SYSUTCDATETIME() ELSE acknowledged_at END,
                mitigated_at    = CASE WHEN @Status = 'Mitigated'    AND mitigated_at    IS NULL THEN SYSUTCDATETIME() ELSE mitigated_at END,
                resolved_at     = CASE WHEN @Status = 'Resolved'     AND resolved_at     IS NULL THEN SYSUTCDATETIME() ELSE resolved_at END,
                closed_at       = CASE WHEN @Status = 'Closed'       AND closed_at       IS NULL THEN SYSUTCDATETIME() ELSE closed_at END
            WHERE tenant_id = @TenantId AND id = @Id";

        var affected = await conn.ExecuteAsync(sql, new { TenantId = tenantId, Id = incidentId, Status = status.ToString() });
        if (affected == 0)
            throw new KeyNotFoundException($"Incident {incidentId} not found.");

        _logger.LogInformation("Incident {IncidentId} status → {Status} by {Actor}", incidentId, status, actor ?? "system");
    }

    public async Task UpdateAssignmentAsync(Guid tenantId, long incidentId, string? assignedTo)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var affected = await conn.ExecuteAsync(
            "UPDATE incidents SET assigned_to = @AssignedTo WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = incidentId, AssignedTo = assignedTo });
        if (affected == 0)
            throw new KeyNotFoundException($"Incident {incidentId} not found.");
    }

    public async Task<Incident?> FindOpenIncidentForFingerprintAsync(Guid tenantId, string fingerprint)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = SelectColumns + @"
            WHERE i.tenant_id = @TenantId
              AND i.status IN ('Open', 'Acknowledged', 'Mitigated')
              AND EXISTS (SELECT 1 FROM anomalies a
                          WHERE a.incident_id = i.id AND a.fingerprint = @Fingerprint)
            ORDER BY i.created_at DESC
            OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY";
        return await conn.QuerySingleOrDefaultAsync<Incident>(sql, new { TenantId = tenantId, Fingerprint = fingerprint });
    }

    public async Task AddEventAsync(IncidentEvent evt)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO incident_events (incident_id, tenant_id, occurred_at, event_type, message, actor_name, details_json)
            VALUES (@IncidentId, @TenantId, @OccurredAt, @EventType, @Message, @ActorName, @DetailsJson)";

        await conn.ExecuteAsync(sql, new
        {
            evt.IncidentId,
            evt.TenantId,
            OccurredAt = evt.OccurredAt == default ? DateTime.UtcNow : evt.OccurredAt,
            evt.EventType,
            evt.Message,
            evt.ActorName,
            evt.DetailsJson
        });
    }

    public async Task<IReadOnlyList<IncidentEvent>> GetEventsAsync(Guid tenantId, long incidentId, int limit = 200)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            SELECT id AS Id, incident_id AS IncidentId, tenant_id AS TenantId, occurred_at AS OccurredAt,
                   event_type AS EventType, message AS Message, actor_name AS ActorName, details_json AS DetailsJson
            FROM incident_events
            WHERE tenant_id = @TenantId AND incident_id = @IncidentId
            ORDER BY occurred_at ASC
            OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<IncidentEvent>(sql, new { TenantId = tenantId, IncidentId = incidentId, Limit = limit });
        return rows.ToList();
    }
}
