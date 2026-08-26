using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for cloud organizations — the top-level, provider-
/// agnostic grouping (Azure tenant connection / AWS Organization / GCP Organization).
/// tenant_id is the workspace / RLS boundary, not an Azure dependency. A workspace
/// can hold multiple Azure connections as peers, exactly like multiple AWS/GCP orgs.
/// </summary>
public sealed class CloudOrgRepository : ICloudOrgRepository
{
    private const string SelectColumns = @"
        SELECT org_id AS OrgId, tenant_id AS TenantId, provider AS Provider, name AS Name,
               external_id AS ExternalId, status AS Status, created_at AS CreatedAt, created_by AS CreatedBy,
               onboarding_method AS OnboardingMethod, credential_secret_name AS CredentialSecretName,
               lighthouse_delegation_id AS LighthouseDelegationId, subscription_scope AS SubscriptionScope
        FROM cloud_orgs";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<CloudOrgRepository> _logger;

    public CloudOrgRepository(ITenantDbConnectionFactory connectionFactory, ILogger<CloudOrgRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<CloudOrg> CreateAsync(CloudOrg org)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        org.OrgId = org.OrgId == Guid.Empty ? Guid.NewGuid() : org.OrgId;

        const string sql = @"
            INSERT INTO cloud_orgs (org_id, tenant_id, provider, name, external_id, status, created_by,
                                     onboarding_method, credential_secret_name, lighthouse_delegation_id, subscription_scope)
            VALUES (@OrgId, @TenantId, @Provider, @Name, @ExternalId, @Status, @CreatedBy,
                    @OnboardingMethod, @CredentialSecretName, @LighthouseDelegationId, @SubscriptionScope)";

        await conn.ExecuteAsync(sql, new
        {
            org.OrgId,
            org.TenantId,
            Provider = org.Provider.ToString(),
            org.Name,
            org.ExternalId,
            Status = org.Status.ToString(),
            org.CreatedBy,
            org.OnboardingMethod,
            org.CredentialSecretName,
            org.LighthouseDelegationId,
            org.SubscriptionScope
        });

        _logger.LogInformation("Created {Provider} org '{Name}' ({OrgId}) in workspace {TenantId}",
            org.Provider, org.Name, org.OrgId, org.TenantId);
        return org;
    }

    public async Task<CloudOrg?> GetByIdAsync(Guid tenantId, Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId AND org_id = @OrgId";
        var row = await conn.QuerySingleOrDefaultAsync<CloudOrgRow>(sql, new { TenantId = tenantId, OrgId = orgId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<CloudOrg>> GetByTenantAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId ORDER BY provider, name";
        var rows = await conn.QueryAsync<CloudOrgRow>(sql, new { TenantId = tenantId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpdateStatusAsync(Guid tenantId, Guid orgId, CloudOrgStatus status)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var affected = await conn.ExecuteAsync(
            "UPDATE cloud_orgs SET status = @Status WHERE tenant_id = @TenantId AND org_id = @OrgId",
            new { TenantId = tenantId, OrgId = orgId, Status = status.ToString() });

        if (affected == 0)
            throw new KeyNotFoundException($"Cloud org {orgId} not found.");

        _logger.LogInformation("Set cloud org {OrgId} status to {Status} in workspace {TenantId}",
            orgId, status, tenantId);
    }

    public async Task DeleteAsync(Guid tenantId, Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();

        // azure_org_subscriptions FK → cloud_orgs without ON DELETE CASCADE
        await conn.ExecuteAsync(
            "DELETE FROM azure_org_subscriptions WHERE tenant_id = @TenantId AND org_id = @OrgId",
            new { TenantId = tenantId, OrgId = orgId });

        var affected = await conn.ExecuteAsync(
            "DELETE FROM cloud_orgs WHERE tenant_id = @TenantId AND org_id = @OrgId",
            new { TenantId = tenantId, OrgId = orgId });

        if (affected == 0)
            throw new KeyNotFoundException($"Cloud org {orgId} not found.");

        _logger.LogInformation("Deleted cloud org {OrgId} from workspace {TenantId}", orgId, tenantId);
    }

    public async Task UpdateCredentialSecretNameAsync(Guid tenantId, Guid orgId, string? credentialSecretName)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var affected = await conn.ExecuteAsync(
            "UPDATE cloud_orgs SET credential_secret_name = @SecretName WHERE tenant_id = @TenantId AND org_id = @OrgId",
            new { TenantId = tenantId, OrgId = orgId, SecretName = credentialSecretName });

        if (affected == 0)
            throw new KeyNotFoundException($"Cloud org {orgId} not found.");
    }

    public async Task AddAzureSubscriptionsAsync(Guid tenantId, Guid orgId, IReadOnlyList<string> subscriptionIds)
    {
        if (subscriptionIds.Count == 0) return;

        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            INSERT INTO azure_org_subscriptions (org_id, tenant_id, subscription_id, subscription_name)
            SELECT @OrgId, @TenantId, @SubscriptionId, @SubscriptionName
            WHERE NOT EXISTS (
                SELECT 1 FROM azure_org_subscriptions
                WHERE org_id = @OrgId AND subscription_id = @SubscriptionId);";

        foreach (var raw in subscriptionIds)
        {
            var subId = raw?.Trim();
            if (string.IsNullOrWhiteSpace(subId)) continue;
            await conn.ExecuteAsync(sql, new
            {
                OrgId = orgId,
                TenantId = tenantId,
                SubscriptionId = subId,
                SubscriptionName = subId
            });
        }

        _logger.LogInformation("Pinned {Count} subscription(s) to Azure org {OrgId}", subscriptionIds.Count, orgId);
    }

    public async Task<IReadOnlyList<AzureOrgSubscription>> GetAzureSubscriptionsAsync(Guid tenantId, Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var rows = await conn.QueryAsync<AzureOrgSubscription>(@"
            SELECT org_id AS OrgId, tenant_id AS TenantId, subscription_id AS SubscriptionId,
                   subscription_name AS SubscriptionName, created_at AS CreatedAt
            FROM azure_org_subscriptions
            WHERE tenant_id = @TenantId AND org_id = @OrgId",
            new { TenantId = tenantId, OrgId = orgId });
        return rows.ToList();
    }

    private sealed class CloudOrgRow
    {
        public Guid OrgId { get; set; }
        public Guid TenantId { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ExternalId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public string? OnboardingMethod { get; set; }
        public string? CredentialSecretName { get; set; }
        public string? LighthouseDelegationId { get; set; }
        public string SubscriptionScope { get; set; } = "all";

        public CloudOrg ToModel()
        {
            var org = new CloudOrg
            {
                OrgId = OrgId,
                TenantId = TenantId,
                Name = Name,
                ExternalId = ExternalId,
                CreatedAt = CreatedAt,
                CreatedBy = CreatedBy,
                OnboardingMethod = OnboardingMethod,
                CredentialSecretName = CredentialSecretName,
                LighthouseDelegationId = LighthouseDelegationId,
                SubscriptionScope = SubscriptionScope
            };
            if (Enum.TryParse<CloudProvider>(Provider, true, out var p)) org.Provider = p;
            if (Enum.TryParse<CloudOrgStatus>(Status, true, out var s)) org.Status = s;
            return org;
        }
    }
}
