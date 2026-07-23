using Dapper;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for the remediation playbook catalog and actions.
/// The playbook catalog is global (no tenant_id); actions are tenant-scoped.
/// </summary>
public sealed class RemediationRepository : IRemediationRepository
{
    private const string PlaybookColumns = @"
        SELECT playbook_key AS PlaybookKey, display_name AS DisplayName, description AS Description,
               provider AS Provider, category AS Category, action_type AS ActionType,
               risk_level AS RiskLevel, always_requires_approval AS AlwaysRequiresApproval,
               enabled AS Enabled, parameters_schema_json AS ParametersSchemaJson
        FROM remediation_playbooks";

    private const string ActionColumns = @"
        SELECT id AS Id, tenant_id AS TenantId, playbook_key AS PlaybookKey, provider AS Provider,
               resource_id AS ResourceId, title AS Title, reason AS Reason,
               parameters_json AS ParametersJson, status AS Status, risk_level AS RiskLevel,
               requested_by AS RequestedBy, anomaly_id AS AnomalyId, incident_id AS IncidentId,
               approval_mode AS ApprovalMode, approved_by AS ApprovedBy, approved_at AS ApprovedAt,
               rejected_reason AS RejectedReason, executed_at AS ExecutedAt, completed_at AS CompletedAt,
               result_json AS ResultJson, error_message AS ErrorMessage, created_at AS CreatedAt,
               expires_at AS ExpiresAt
        FROM remediation_actions";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<RemediationRepository> _logger;

    public RemediationRepository(ITenantDbConnectionFactory connectionFactory, ILogger<RemediationRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // ---- Playbook catalog ----

    public async Task<IReadOnlyList<RemediationPlaybook>> GetPlaybooksAsync(CloudProvider? provider = null, bool enabledOnly = true)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = PlaybookColumns + " WHERE 1 = 1";
        if (provider.HasValue) sql += " AND provider = @Provider";
        if (enabledOnly) sql += " AND enabled = 1";
        sql += " ORDER BY provider, category, playbook_key";

        var rows = await conn.QueryAsync<RemediationPlaybook>(sql, new { Provider = provider?.ToString() });
        return rows.ToList();
    }

    public async Task<RemediationPlaybook?> GetPlaybookAsync(string playbookKey)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = PlaybookColumns + " WHERE playbook_key = @PlaybookKey";
        return await conn.QuerySingleOrDefaultAsync<RemediationPlaybook>(sql, new { PlaybookKey = playbookKey });
    }

    // ---- Actions ----

    public async Task<long> CreateActionAsync(RemediationAction action)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO remediation_actions
                (tenant_id, playbook_key, provider, resource_id, title, reason, parameters_json,
                 status, risk_level, requested_by, anomaly_id, incident_id, approval_mode,
                 approved_by, approved_at, expires_at)
            OUTPUT inserted.id
            VALUES
                (@TenantId, @PlaybookKey, @Provider, @ResourceId, @Title, @Reason, @ParametersJson,
                 @Status, @RiskLevel, @RequestedBy, @AnomalyId, @IncidentId, @ApprovalMode,
                 @ApprovedBy, @ApprovedAt, @ExpiresAt)";

        var id = await conn.ExecuteScalarAsync<long>(sql, new
        {
            action.TenantId,
            action.PlaybookKey,
            Provider = action.Provider.ToString(),
            action.ResourceId,
            action.Title,
            action.Reason,
            action.ParametersJson,
            Status = action.Status.ToString(),
            RiskLevel = action.RiskLevel.ToString(),
            action.RequestedBy,
            action.AnomalyId,
            action.IncidentId,
            action.ApprovalMode,
            action.ApprovedBy,
            action.ApprovedAt,
            action.ExpiresAt
        });

        action.Id = id;
        _logger.LogInformation("Created remediation action {ActionId} ({Playbook}, {Status}) for tenant {TenantId}",
            id, action.PlaybookKey, action.Status, action.TenantId);
        return id;
    }

    public async Task<RemediationAction?> GetActionByIdAsync(Guid tenantId, long actionId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = ActionColumns + " WHERE tenant_id = @TenantId AND id = @Id";
        return await conn.QuerySingleOrDefaultAsync<RemediationAction>(sql, new { TenantId = tenantId, Id = actionId });
    }

    public async Task<IReadOnlyList<RemediationAction>> GetActionsAsync(
        Guid tenantId, RemediationStatus? status = null, int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = ActionColumns + " WHERE tenant_id = @TenantId";
        if (status.HasValue) sql += " AND status = @Status";
        sql += " ORDER BY created_at DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<RemediationAction>(sql, new
        {
            TenantId = tenantId,
            Status = status?.ToString(),
            Offset = offset,
            Limit = limit
        });
        return rows.ToList();
    }

    public async Task<int> GetPendingApprovalCountAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM remediation_actions WHERE tenant_id = @TenantId AND status = 'PendingApproval'",
            new { TenantId = tenantId });
    }

    public async Task<IReadOnlyList<RemediationAction>> GetApprovedPendingExecutionAsync(int limit = 50)
    {
        // Cross-tenant worker query — the execution queue spans all tenants.
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = ActionColumns + @"
            WHERE status = 'Approved'
            ORDER BY approved_at ASC
            OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY";
        var rows = await conn.QueryAsync<RemediationAction>(sql, new { Limit = limit });
        return rows.ToList();
    }

    public async Task<bool> HasOpenActionAsync(Guid tenantId, string playbookKey, string? resourceId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            SELECT COUNT(*) FROM remediation_actions
            WHERE tenant_id = @TenantId AND playbook_key = @PlaybookKey
              AND (@ResourceId IS NULL AND resource_id IS NULL OR resource_id = @ResourceId)
              AND status IN ('Proposed', 'PendingApproval', 'Approved', 'Executing')";
        var count = await conn.ExecuteScalarAsync<int>(sql, new { TenantId = tenantId, PlaybookKey = playbookKey, ResourceId = resourceId });
        return count > 0;
    }

    public async Task UpdateStatusAsync(Guid tenantId, long actionId, RemediationStatus status,
        string? actor = null, string? rejectedReason = null)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE remediation_actions SET
                status = @Status,
                approved_by = CASE WHEN @Status = 'Approved' THEN @Actor ELSE approved_by END,
                approved_at = CASE WHEN @Status = 'Approved' THEN SYSUTCDATETIME() ELSE approved_at END,
                rejected_reason = CASE WHEN @Status = 'Rejected' THEN @RejectedReason ELSE rejected_reason END
            WHERE tenant_id = @TenantId AND id = @Id";

        var affected = await conn.ExecuteAsync(sql, new
        {
            TenantId = tenantId,
            Id = actionId,
            Status = status.ToString(),
            Actor = actor,
            RejectedReason = rejectedReason
        });
        if (affected == 0)
            throw new KeyNotFoundException($"Remediation action {actionId} not found.");

        _logger.LogInformation("Remediation action {ActionId} status → {Status} by {Actor}", actionId, status, actor ?? "system");
    }

    public async Task MarkExecutionStartedAsync(long actionId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        // Guard on status = 'Approved' makes concurrent executors safe: only one
        // wins the transition to Executing.
        var affected = await conn.ExecuteAsync(@"
            UPDATE remediation_actions
            SET status = 'Executing', executed_at = SYSUTCDATETIME()
            WHERE id = @Id AND status = 'Approved'", new { Id = actionId });

        if (affected == 0)
            throw new InvalidOperationException($"Action {actionId} is not in Approved state.");
    }

    public async Task MarkExecutionCompletedAsync(long actionId, bool succeeded, string? resultJson, string? errorMessage)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE remediation_actions SET
                status = @Status,
                completed_at = SYSUTCDATETIME(),
                result_json = @ResultJson,
                error_message = @ErrorMessage
            WHERE id = @Id", new
        {
            Id = actionId,
            Status = succeeded ? RemediationStatus.Succeeded.ToString() : RemediationStatus.Failed.ToString(),
            ResultJson = resultJson,
            ErrorMessage = errorMessage
        });
    }

    public async Task ExpireStaleActionsAsync(DateTime cutoffUtc)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var affected = await conn.ExecuteAsync(@"
            UPDATE remediation_actions SET status = 'Expired'
            WHERE status IN ('Proposed', 'PendingApproval')
              AND expires_at IS NOT NULL AND expires_at < @Cutoff", new { Cutoff = cutoffUtc });

        if (affected > 0)
            _logger.LogInformation("Expired {Count} stale remediation actions", affected);
    }
}
