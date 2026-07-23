using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for cloud organizations — the top-level, provider-
/// agnostic grouping (Azure tenant / AWS Organization / GCP Organization).
/// tenant_id is the workspace / RLS boundary, not an Azure dependency.
/// </summary>
public sealed class CloudOrgRepository : ICloudOrgRepository
{
    private const string SelectColumns = @"
        SELECT org_id AS OrgId, tenant_id AS TenantId, provider AS Provider, name AS Name,
               external_id AS ExternalId, status AS Status, created_at AS CreatedAt, created_by AS CreatedBy
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
            INSERT INTO cloud_orgs (org_id, tenant_id, provider, name, external_id, status, created_by)
            VALUES (@OrgId, @TenantId, @Provider, @Name, @ExternalId, @Status, @CreatedBy)";

        await conn.ExecuteAsync(sql, new
        {
            org.OrgId,
            org.TenantId,
            Provider = org.Provider.ToString(),
            org.Name,
            org.ExternalId,
            Status = org.Status.ToString(),
            org.CreatedBy
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

        public CloudOrg ToModel()
        {
            var org = new CloudOrg
            {
                OrgId = OrgId,
                TenantId = TenantId,
                Name = Name,
                ExternalId = ExternalId,
                CreatedAt = CreatedAt,
                CreatedBy = CreatedBy
            };
            if (Enum.TryParse<CloudProvider>(Provider, true, out var p)) org.Provider = p;
            if (Enum.TryParse<CloudAccountStatus>(Status, true, out var s)) org.Status = s;
            return org;
        }
    }
}
