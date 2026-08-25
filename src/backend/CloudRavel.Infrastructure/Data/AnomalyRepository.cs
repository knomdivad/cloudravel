using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based anomaly + metric baseline repository.
/// Writes come from the (cross-tenant) anomaly scan worker via admin connections;
/// reads are tenant-scoped (RLS enforced).
/// New AIOps tables store enum values in PascalCase, matching defender_findings.
/// </summary>
public sealed class AnomalyRepository : IAnomalyRepository
{
    private const string SelectColumns = @"
        SELECT id AS Id, tenant_id AS TenantId, fingerprint AS Fingerprint, kind AS Kind,
               severity AS Severity, status AS Status, provider AS Provider, title AS Title,
               description AS Description, resource_id AS ResourceId, metric_name AS MetricName,
               observed_value AS ObservedValue, baseline_mean AS BaselineMean,
               baseline_std_dev AS BaselineStdDev, score AS Score, detected_at AS DetectedAt,
               last_seen_at AS LastSeenAt, resolved_at AS ResolvedAt, details_json AS DetailsJson,
               incident_id AS IncidentId
        FROM anomalies";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<AnomalyRepository> _logger;

    public AnomalyRepository(ITenantDbConnectionFactory connectionFactory, ILogger<AnomalyRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<(long Id, bool Created)> UpsertAnomalyAsync(Anomaly anomaly)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();

        // Refresh an already-open anomaly with the same fingerprint instead of
        // opening a duplicate — repeated scans of a persisting condition should
        // update one row, not spam the queue.
        const string sql = @"
            MERGE anomalies AS target
            USING (SELECT @TenantId AS tenant_id, @Fingerprint AS fingerprint) AS source
            ON target.tenant_id = source.tenant_id
               AND target.fingerprint = source.fingerprint
               AND target.status IN ('Open', 'Acknowledged')
            WHEN MATCHED THEN UPDATE SET
                last_seen_at = @LastSeenAt,
                observed_value = @ObservedValue,
                baseline_mean = @BaselineMean,
                baseline_std_dev = @BaselineStdDev,
                score = @Score,
                severity = @Severity,
                details_json = @DetailsJson,
                -- Refresh provider/title/description so multi-cloud estates don't stay
                -- stuck on a historical Azure default after we improve inference.
                provider = @Provider,
                title = @Title,
                description = @Description,
                resource_id = COALESCE(@ResourceId, target.resource_id)
            WHEN NOT MATCHED THEN INSERT
                (tenant_id, fingerprint, kind, severity, status, provider, title, description,
                 resource_id, metric_name, observed_value, baseline_mean, baseline_std_dev,
                 score, detected_at, last_seen_at, details_json)
            VALUES
                (@TenantId, @Fingerprint, @Kind, @Severity, @Status, @Provider, @Title, @Description,
                 @ResourceId, @MetricName, @ObservedValue, @BaselineMean, @BaselineStdDev,
                 @Score, @DetectedAt, @LastSeenAt, @DetailsJson)
            OUTPUT inserted.id, $action AS MergeAction;";

        var result = await conn.QuerySingleAsync<(long id, string MergeAction)>(sql, new
        {
            anomaly.TenantId,
            anomaly.Fingerprint,
            Kind = anomaly.Kind.ToString(),
            Severity = anomaly.Severity.ToString(),
            Status = anomaly.Status.ToString(),
            Provider = anomaly.Provider.ToString(),
            anomaly.Title,
            anomaly.Description,
            anomaly.ResourceId,
            anomaly.MetricName,
            anomaly.ObservedValue,
            anomaly.BaselineMean,
            anomaly.BaselineStdDev,
            anomaly.Score,
            anomaly.DetectedAt,
            anomaly.LastSeenAt,
            anomaly.DetailsJson
        });

        anomaly.Id = result.id;
        return (result.id, result.MergeAction == "INSERT");
    }

    public async Task<Anomaly?> GetByIdAsync(Guid tenantId, long anomalyId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId AND id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Anomaly>(sql, new { TenantId = tenantId, Id = anomalyId });
    }

    public async Task<IReadOnlyList<Anomaly>> GetAnomaliesAsync(
        Guid tenantId, AnomalyStatus? status = null, AnomalySeverity? severity = null,
        AnomalyKind? kind = null, int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = SelectColumns + " WHERE tenant_id = @TenantId";
        if (status.HasValue) sql += " AND status = @Status";
        if (severity.HasValue) sql += " AND severity = @Severity";
        if (kind.HasValue) sql += " AND kind = @Kind";
        sql += " ORDER BY last_seen_at DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<Anomaly>(sql, new
        {
            TenantId = tenantId,
            Status = status?.ToString(),
            Severity = severity?.ToString(),
            Kind = kind?.ToString(),
            Offset = offset,
            Limit = limit
        });
        return rows.ToList();
    }

    public async Task<int> GetOpenCountAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM anomalies WHERE tenant_id = @TenantId AND status IN ('Open', 'Acknowledged')",
            new { TenantId = tenantId });
    }

    public async Task UpdateStatusAsync(Guid tenantId, long anomalyId, AnomalyStatus status, string? actor)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            UPDATE anomalies SET
                status = @Status,
                resolved_at = CASE WHEN @Status IN ('Resolved', 'Suppressed', 'FalsePositive')
                                   THEN SYSUTCDATETIME() ELSE resolved_at END
            WHERE tenant_id = @TenantId AND id = @Id";

        var affected = await conn.ExecuteAsync(sql, new { TenantId = tenantId, Id = anomalyId, Status = status.ToString() });
        if (affected == 0)
            throw new KeyNotFoundException($"Anomaly {anomalyId} not found.");

        _logger.LogInformation("Anomaly {AnomalyId} status → {Status} by {Actor}", anomalyId, status, actor ?? "system");
    }

    public async Task LinkToIncidentAsync(Guid tenantId, long anomalyId, long incidentId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE anomalies SET incident_id = @IncidentId WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = anomalyId, IncidentId = incidentId });
    }

    public async Task UpdateProviderAsync(Guid tenantId, long anomalyId, CloudProvider provider)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE anomalies SET provider = @Provider WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = anomalyId, Provider = provider.ToString() });
    }

    public async Task ResolveByFingerprintAsync(Guid tenantId, string fingerprint)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE anomalies SET status = 'Resolved', resolved_at = SYSUTCDATETIME()
            WHERE tenant_id = @TenantId AND fingerprint = @Fingerprint AND status IN ('Open', 'Acknowledged')",
            new { TenantId = tenantId, Fingerprint = fingerprint });
    }

    public async Task<MetricBaseline?> GetBaselineAsync(Guid tenantId, string metricKey)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT id AS Id, tenant_id AS TenantId, metric_key AS MetricKey, window_hours AS WindowHours,
                   mean AS Mean, std_dev AS StdDev, sample_count AS SampleCount, updated_at AS UpdatedAt
            FROM metric_baselines
            WHERE tenant_id = @TenantId AND metric_key = @MetricKey";
        return await conn.QuerySingleOrDefaultAsync<MetricBaseline>(sql, new { TenantId = tenantId, MetricKey = metricKey });
    }

    public async Task UpsertBaselineAsync(MetricBaseline baseline)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE metric_baselines AS target
            USING (SELECT @TenantId AS tenant_id, @MetricKey AS metric_key) AS source
            ON target.tenant_id = source.tenant_id AND target.metric_key = source.metric_key
            WHEN MATCHED THEN UPDATE SET
                window_hours = @WindowHours, mean = @Mean, std_dev = @StdDev,
                sample_count = @SampleCount, updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (tenant_id, metric_key, window_hours, mean, std_dev, sample_count)
            VALUES (@TenantId, @MetricKey, @WindowHours, @Mean, @StdDev, @SampleCount);";

        await conn.ExecuteAsync(sql, new
        {
            baseline.TenantId,
            baseline.MetricKey,
            baseline.WindowHours,
            baseline.Mean,
            baseline.StdDev,
            baseline.SampleCount
        });
    }
}
