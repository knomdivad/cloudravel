using System.Text.Json;
using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for Advisor recommendations, Policy compliance, and Defender findings.
/// Read queries use tenant-scoped connections (RLS enforced).
/// Upsert/write operations use admin connections.
/// </summary>
public sealed class RecommendationRepository : IRecommendationRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<RecommendationRepository> _logger;

    public RecommendationRepository(ITenantDbConnectionFactory connectionFactory, ILogger<RecommendationRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    // ========================================================================
    // Advisor Recommendations
    // ========================================================================

    public async Task UpsertAdvisorRecommendationsAsync(IEnumerable<AdvisorRecommendation> recommendations)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE advisor_recommendations AS target
            USING (SELECT @TenantId AS tenant_id, @RecommendationId AS recommendation_id) AS source
            ON target.tenant_id = source.tenant_id AND target.recommendation_id = source.recommendation_id
            WHEN MATCHED THEN UPDATE SET
                resource_id = @ResourceId,
                category = @Category,
                impact = @Impact,
                title = @Title,
                description = @Description,
                remediation_action = @RemediationAction,
                estimated_savings = @EstimatedSavings,
                currency = @Currency,
                last_seen_at = SYSUTCDATETIME(),
                lifecycle_status = CASE 
                    WHEN target.lifecycle_status IN ('dismissed', 'snoozed') THEN target.lifecycle_status
                    ELSE 'active'
                END
            WHEN NOT MATCHED THEN INSERT
                (tenant_id, recommendation_id, resource_id, category, impact, title, description,
                 remediation_action, estimated_savings, currency, lifecycle_status)
            VALUES
                (@TenantId, @RecommendationId, @ResourceId, @Category, @Impact, @Title, @Description,
                 @RemediationAction, @EstimatedSavings, @Currency, 'active');";

        var batch = recommendations.Select(r => new
        {
            r.TenantId,
            r.RecommendationId,
            r.ResourceId,
            r.Category,
            r.Impact,
            r.Title,
            r.Description,
            r.RemediationAction,
            r.EstimatedSavings,
            r.Currency
        });

        var count = await conn.ExecuteAsync(sql, batch);
        _logger.LogDebug("Upserted {Count} Advisor recommendations", count);
    }

    public async Task<IReadOnlyList<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(
        Guid tenantId, string? category = null, RecommendationLifecycle? status = null,
        int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = @"
            SELECT id AS Id, tenant_id AS TenantId, recommendation_id AS RecommendationId,
                   resource_id AS ResourceId, category AS Category, impact AS Impact,
                   title AS Title, description AS Description, remediation_action AS RemediationAction,
                   estimated_savings AS EstimatedSavings, currency AS Currency,
                   first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt, resolved_at AS ResolvedAt,
                   lifecycle_status AS LifecycleStatus, acknowledged_by AS AcknowledgedBy
            FROM advisor_recommendations
            WHERE tenant_id = @TenantId";

        if (!string.IsNullOrEmpty(category))
            sql += " AND category = @Category";
        if (status.HasValue)
            sql += " AND lifecycle_status = @Status";

        sql += " ORDER BY CASE impact WHEN 'High' THEN 1 WHEN 'Medium' THEN 2 WHEN 'Low' THEN 3 ELSE 4 END, last_seen_at DESC";
        sql += " OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var results = await conn.QueryAsync<AdvisorRecommendation>(sql, new
        {
            TenantId = tenantId,
            Category = category,
            Status = status?.ToString().ToLowerInvariant(),
            Offset = offset,
            Limit = limit
        });

        return results.ToList();
    }

    public async Task UpdateRecommendationLifecycleAsync(
        Guid tenantId, string recommendationId, RecommendationLifecycle status, Guid? acknowledgedBy)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        const string sql = @"
            UPDATE advisor_recommendations 
            SET lifecycle_status = @Status,
                acknowledged_by = COALESCE(@AcknowledgedBy, acknowledged_by),
                resolved_at = CASE WHEN @Status = 'resolved' THEN SYSUTCDATETIME() ELSE resolved_at END
            WHERE tenant_id = @TenantId AND recommendation_id = @RecommendationId";

        var affected = await conn.ExecuteAsync(sql, new
        {
            TenantId = tenantId,
            RecommendationId = recommendationId,
            Status = status.ToString().ToLowerInvariant(),
            AcknowledgedBy = acknowledgedBy
        });

        if (affected == 0)
            throw new KeyNotFoundException($"Recommendation '{recommendationId}' not found for tenant {tenantId}.");

        _logger.LogInformation("Updated recommendation {RecommendationId} lifecycle to {Status} for tenant {TenantId}",
            recommendationId, status, tenantId);
    }

    // ========================================================================
    // Policy Compliance
    // ========================================================================

    public async Task UpsertPolicyComplianceAsync(IEnumerable<PolicyComplianceRecord> records)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE policy_compliance AS target
            USING (SELECT @TenantId AS tenant_id, @PolicyAssignmentId AS policy_assignment_id, @ResourceId AS resource_id) AS source
            ON target.tenant_id = source.tenant_id 
               AND target.policy_assignment_id = source.policy_assignment_id 
               AND target.resource_id = source.resource_id
            WHEN MATCHED THEN UPDATE SET
                compliance_state = @ComplianceState,
                last_evaluated_at = @LastEvaluatedAt,
                resolved_at = CASE WHEN @ComplianceState = 'Compliant' AND target.compliance_state = 'NonCompliant' 
                              THEN SYSUTCDATETIME() ELSE target.resolved_at END
            WHEN NOT MATCHED THEN INSERT
                (tenant_id, policy_assignment_id, policy_definition_id, policy_name,
                 resource_id, compliance_state, category, last_evaluated_at)
            VALUES
                (@TenantId, @PolicyAssignmentId, @PolicyDefinitionId, @PolicyName,
                 @ResourceId, @ComplianceState, @Category, @LastEvaluatedAt);";

        var batch = records.Select(r => new
        {
            r.TenantId,
            r.PolicyAssignmentId,
            r.PolicyDefinitionId,
            r.PolicyName,
            r.ResourceId,
            r.ComplianceState,
            r.Category,
            r.LastEvaluatedAt
        });

        var count = await conn.ExecuteAsync(sql, batch);
        _logger.LogDebug("Upserted {Count} Policy compliance records", count);
    }

    public async Task<IReadOnlyList<PolicyComplianceRecord>> GetPolicyComplianceAsync(
        Guid tenantId, string? complianceState = null, int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = @"
            SELECT id AS Id, tenant_id AS TenantId, policy_assignment_id AS PolicyAssignmentId,
                   policy_definition_id AS PolicyDefinitionId, policy_name AS PolicyName,
                   resource_id AS ResourceId, compliance_state AS ComplianceState,
                   category AS Category, first_seen_at AS FirstSeenAt,
                   last_evaluated_at AS LastEvaluatedAt, resolved_at AS ResolvedAt
            FROM policy_compliance
            WHERE tenant_id = @TenantId";

        if (!string.IsNullOrEmpty(complianceState))
            sql += " AND compliance_state = @ComplianceState";

        sql += " ORDER BY last_evaluated_at DESC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var results = await conn.QueryAsync<PolicyComplianceRecord>(sql, new
        {
            TenantId = tenantId,
            ComplianceState = complianceState,
            Offset = offset,
            Limit = limit
        });

        return results.ToList();
    }

    // ========================================================================
    // Defender Findings
    // ========================================================================

    public async Task UpsertDefenderFindingsAsync(IEnumerable<DefenderFinding> findings)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE defender_findings AS target
            USING (SELECT @TenantId AS tenant_id, @FindingId AS finding_id) AS source
            ON target.tenant_id = source.tenant_id AND target.finding_id = source.finding_id
            WHEN MATCHED THEN UPDATE SET
                resource_id = @ResourceId,
                assessment_name = @AssessmentName,
                severity = @Severity,
                status = @Status,
                description = @Description,
                remediation_steps = @RemediationSteps,
                categories = @CategoriesJson,
                last_seen_at = SYSUTCDATETIME(),
                resolved_at = CASE WHEN @Status IN ('Healthy', 'NotApplicable') AND target.status = 'Unhealthy' 
                              THEN SYSUTCDATETIME() ELSE target.resolved_at END
            WHEN NOT MATCHED THEN INSERT
                (tenant_id, finding_id, resource_id, assessment_name, severity, status,
                 description, remediation_steps, categories)
            VALUES
                (@TenantId, @FindingId, @ResourceId, @AssessmentName, @Severity, @Status,
                 @Description, @RemediationSteps, @CategoriesJson);";

        var batch = findings.Select(f => new
        {
            f.TenantId,
            f.FindingId,
            f.ResourceId,
            f.AssessmentName,
            f.Severity,
            f.Status,
            f.Description,
            f.RemediationSteps,
            CategoriesJson = f.Categories != null ? JsonSerializer.Serialize(f.Categories) : null
        });

        var count = await conn.ExecuteAsync(sql, batch);
        _logger.LogDebug("Upserted {Count} Defender findings", count);
    }

    public async Task<IReadOnlyList<DefenderFinding>> GetDefenderFindingsAsync(
        Guid tenantId, string? severity = null, string? status = null,
        int offset = 0, int limit = 100)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);

        var sql = @"
            SELECT id AS Id, tenant_id AS TenantId, finding_id AS FindingId,
                   resource_id AS ResourceId, assessment_name AS AssessmentName,
                   severity AS Severity, status AS Status, description AS Description,
                   remediation_steps AS RemediationSteps, categories AS CategoriesJson,
                   first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt, resolved_at AS ResolvedAt
            FROM defender_findings
            WHERE tenant_id = @TenantId";

        if (!string.IsNullOrEmpty(severity))
            sql += " AND severity = @Severity";
        if (!string.IsNullOrEmpty(status))
            sql += " AND status = @Status";

        sql += " ORDER BY CASE severity WHEN 'Critical' THEN 1 WHEN 'High' THEN 2 WHEN 'Medium' THEN 3 WHEN 'Low' THEN 4 ELSE 5 END, last_seen_at DESC";
        sql += " OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        var rows = await conn.QueryAsync<DefenderFindingRow>(sql, new
        {
            TenantId = tenantId,
            Severity = severity,
            Status = status,
            Offset = offset,
            Limit = limit
        });

        return rows.Select(r =>
        {
            var finding = new DefenderFinding
            {
                Id = r.Id,
                TenantId = r.TenantId,
                FindingId = r.FindingId,
                ResourceId = r.ResourceId,
                AssessmentName = r.AssessmentName,
                Severity = r.Severity,
                Status = r.Status,
                Description = r.Description,
                RemediationSteps = r.RemediationSteps,
                FirstSeenAt = r.FirstSeenAt,
                LastSeenAt = r.LastSeenAt,
                ResolvedAt = r.ResolvedAt
            };

            if (!string.IsNullOrEmpty(r.CategoriesJson))
            {
                try
                {
                    finding.Categories = JsonSerializer.Deserialize<List<string>>(r.CategoriesJson);
                }
                catch { /* malformed JSON */ }
            }

            return finding;
        }).ToList();
    }

    /// <summary>
    /// Internal row type for Dapper mapping (Defender findings have JSON columns).
    /// </summary>
    private sealed class DefenderFindingRow
    {
        public long Id { get; set; }
        public Guid TenantId { get; set; }
        public string FindingId { get; set; } = string.Empty;
        public string? ResourceId { get; set; }
        public string AssessmentName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? RemediationSteps { get; set; }
        public string? CategoriesJson { get; set; }
        public DateTime FirstSeenAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
