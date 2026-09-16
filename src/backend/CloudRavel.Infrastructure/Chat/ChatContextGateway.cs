using CloudRavel.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// Retrieval gateway for the customer-facing chat: gathers a read-only,
/// tenant-scoped grounding snapshot of the caller's workspace.
///
/// Isolation model (defense in depth):
///   1. The caller's tenantId arrives from TenantContextMiddleware, which has
///      already asserted user→tenant access (user_tenant_access role) before
///      the handler runs. It is NOT client-controlled at this point.
///   2. Every connection comes from ITenantDbConnectionFactory with that
///      tenantId — SESSION_CONTEXT('tenant_id') is set read-only, and the RLS
///      security policy (fn_tenant_security_predicate) filters/blocks every
///      tenant-scoped table.
///   3. Every statement also filters WHERE tenant_id = @TenantId explicitly.
/// There is deliberately no overload that accepts a tenant id other than the
/// middleware-verified one, and no admin/RLS-bypass connection is used here.
/// </summary>
public sealed class ChatContextGateway
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ITenantRepository _tenantRepo;
    private readonly ILogger<ChatContextGateway> _logger;

    public ChatContextGateway(
        ITenantDbConnectionFactory connectionFactory,
        ITenantRepository tenantRepo,
        ILogger<ChatContextGateway> logger)
    {
        _connectionFactory = connectionFactory;
        _tenantRepo = tenantRepo;
        _logger = logger;
    }

    public async Task<ChatWorkspaceContext> GetContextAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);

        var summary = await QuerySingleAsync(
            tenantId,
            """
            SELECT
                (SELECT COUNT(*) FROM inventory_resources WHERE tenant_id = @TenantId
                     AND snapshot_id = (SELECT MAX(snapshot_id) FROM inventory_snapshots WHERE tenant_id = @TenantId)) AS TotalResources,
                (SELECT MAX(completed_at) FROM inventory_snapshots WHERE tenant_id = @TenantId AND status = 'completed') AS LastSnapshotAt,
                (SELECT COUNT(*) FROM resource_changes WHERE tenant_id = @TenantId AND detected_at >= DATEADD(day, -7, SYSUTCDATETIME())) AS ChangesLast7d,
                (SELECT COUNT(*) FROM advisor_recommendations WHERE tenant_id = @TenantId AND lifecycle_status = 'Active') AS OpenAdvisorRecommendations,
                (SELECT ISNULL(SUM(estimated_savings), 0) FROM advisor_recommendations WHERE tenant_id = @TenantId AND category = 'Cost' AND lifecycle_status = 'Active') AS EstimatedAnnualSavingsUsd,
                (SELECT COUNT(*) FROM defender_findings WHERE tenant_id = @TenantId AND status = 'Unhealthy') AS OpenDefenderFindings,
                (SELECT COUNT(*) FROM defender_findings WHERE tenant_id = @TenantId AND status = 'Unhealthy' AND severity = 'Critical') AS CriticalDefenderFindings,
                (SELECT COUNT(*) FROM policy_compliance WHERE tenant_id = @TenantId AND compliance_state = 'NonCompliant') AS NonCompliantPolicies
            """,
            cancellationToken);

        var resourceTypes = (await QueryAsync<ChatResourceTypeCount>(
            tenantId,
            """
            SELECT TOP (10) resource_type AS ResourceType, COUNT(*) AS Count
            FROM inventory_resources
            WHERE tenant_id = @TenantId
                AND snapshot_id = (SELECT MAX(snapshot_id) FROM inventory_snapshots WHERE tenant_id = @TenantId)
            GROUP BY resource_type
            ORDER BY COUNT(*) DESC
            """,
            cancellationToken)).ToList();

        var topFindings = (await QueryAsync<ChatTopFinding>(
            tenantId,
            """
            SELECT TOP (5) severity AS Severity, assessment_name AS Title, resource_id AS ResourceId, last_seen_at AS LastSeenAt
            FROM defender_findings
            WHERE tenant_id = @TenantId AND status = 'Unhealthy'
            ORDER BY CASE severity WHEN 'Critical' THEN 0 WHEN 'High' THEN 1 WHEN 'Medium' THEN 2 ELSE 3 END, last_seen_at DESC
            """,
            cancellationToken)).ToList();

        var recentChanges = (await QueryAsync<ChatRecentChange>(
            tenantId,
            """
            SELECT TOP (10) resource_id AS ResourceId, change_type AS ChangeType, classification AS Classification,
                   actor_name AS ActorName, detected_at AS DetectedAt
            FROM resource_changes
            WHERE tenant_id = @TenantId AND detected_at >= DATEADD(day, -7, SYSUTCDATETIME())
            ORDER BY detected_at DESC
            """,
            cancellationToken)).ToList();

        var cloudConnections = (await QueryAsync<ChatCloudConnection>(
            tenantId,
            """
            SELECT provider AS Provider, name AS Name, status AS Status
            FROM cloud_orgs
            WHERE tenant_id = @TenantId
            ORDER BY created_at
            """,
            cancellationToken)).ToList();

        var context = new ChatWorkspaceContext
        {
            TenantId = tenantId,
            TenantName = tenant?.DisplayName ?? "your workspace",
            TotalResources = summary?.TotalResources ?? 0,
            LastSnapshotAt = summary?.LastSnapshotAt,
            ChangesLast7d = summary?.ChangesLast7d ?? 0,
            OpenAdvisorRecommendations = summary?.OpenAdvisorRecommendations ?? 0,
            EstimatedAnnualSavingsUsd = (int)Math.Round(summary?.EstimatedAnnualSavingsUsd ?? 0),
            OpenDefenderFindings = summary?.OpenDefenderFindings ?? 0,
            CriticalDefenderFindings = summary?.CriticalDefenderFindings ?? 0,
            NonCompliantPolicies = summary?.NonCompliantPolicies ?? 0,
            ResourceTypes = resourceTypes,
            TopFindings = topFindings,
            RecentChanges = recentChanges,
            CloudConnections = cloudConnections,
        };

        _logger.LogInformation(
            "Chat grounding gathered for tenant {TenantId}: {Resources} resources, {Findings} open findings, {Changes} changes/7d",
            tenantId, context.TotalResources, context.OpenDefenderFindings, context.ChangesLast7d);
        return context;
    }

    private async Task<ChatSummaryRow?> QuerySingleAsync(Guid tenantId, string sql, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        return await conn.QuerySingleAsync<ChatSummaryRow?>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: ct));
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(Guid tenantId, string sql, CancellationToken ct) where T : class
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        return await conn.QueryAsync<T>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: ct));
    }

    private sealed class ChatSummaryRow
    {
        public int TotalResources { get; set; }
        public DateTime? LastSnapshotAt { get; set; }
        public int ChangesLast7d { get; set; }
        public int OpenAdvisorRecommendations { get; set; }
        public decimal EstimatedAnnualSavingsUsd { get; set; }
        public int OpenDefenderFindings { get; set; }
        public int CriticalDefenderFindings { get; set; }
        public int NonCompliantPolicies { get; set; }
    }
}
