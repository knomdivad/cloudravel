using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for Organizations — the in-app workspace registry.
/// Uses admin connections since it defines the RLS boundary (org_id = tenant_id)
/// and management needs cross-workspace visibility, exactly like TenantRepository.
/// </summary>
public sealed class OrganizationRepository : IOrganizationRepository
{
    private const string SelectColumns = @"
        SELECT org_id AS OrgId, name AS Name, environment AS Environment,
               status AS Status, created_at AS CreatedAt, created_by AS CreatedBy,
               entitlement_bot_monitor AS EntitlementBotMonitor,
               entitlement_cloud_monitor AS EntitlementCloudMonitor
        FROM organizations";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<OrganizationRepository> _logger;

    public OrganizationRepository(ITenantDbConnectionFactory connectionFactory, ILogger<OrganizationRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Organization>> GetAllAsync()
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var rows = await conn.QueryAsync<Organization>(
            SelectColumns + " WHERE status = 'active' ORDER BY name");
        return rows.ToList();
    }

    public async Task<Organization?> GetByIdAsync(Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<Organization>(
            SelectColumns + " WHERE org_id = @OrgId", new { OrgId = orgId });
    }

    public async Task<FeatureEntitlements> GetFeatureEntitlementsAsync(Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync(
            @"SELECT entitlement_bot_monitor AS BotMonitor, entitlement_cloud_monitor AS CloudMonitor
              FROM organizations WHERE org_id = @OrgId",
            new { OrgId = orgId });
        if (row is null)
            return FeatureEntitlements.None;
        return new FeatureEntitlements((bool)row.BotMonitor, (bool)row.CloudMonitor);
    }

    public async Task UpdateFeatureEntitlementsAsync(Guid orgId, FeatureEntitlements entitlements)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var affected = await conn.ExecuteAsync(
            @"UPDATE organizations
              SET entitlement_bot_monitor = @Bot, entitlement_cloud_monitor = @Cloud
              WHERE org_id = @OrgId",
            new { OrgId = orgId, Bot = entitlements.BotMonitoring, Cloud = entitlements.CloudMonitoring });
        if (affected == 0)
            throw new KeyNotFoundException($"Organization {orgId} not found.");
    }

    public async Task<Organization> CreateAsync(Organization org)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        org.OrgId = org.OrgId == Guid.Empty ? Guid.NewGuid() : org.OrgId;

        const string sql = @"
            INSERT INTO organizations (org_id, name, environment, status, created_by,
                                       entitlement_bot_monitor, entitlement_cloud_monitor)
            VALUES (@OrgId, @Name, @Environment, @Status, @CreatedBy,
                    @EntitlementBotMonitor, @EntitlementCloudMonitor)";

        await conn.ExecuteAsync(sql, new
        {
            org.OrgId,
            org.Name,
            org.Environment,
            org.Status,
            org.CreatedBy,
            org.EntitlementBotMonitor,
            org.EntitlementCloudMonitor,
        });

        _logger.LogInformation("Created organization '{Name}' ({OrgId}) [{Environment}]",
            org.Name, org.OrgId, org.Environment);

        return (await GetByIdAsync(org.OrgId))!;
    }

    public async Task SoftDeleteAsync(Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        // organizations.status CHECK allows active | suspended — soft-delete = suspended.
        var affected = await conn.ExecuteAsync(
            "UPDATE organizations SET status = 'suspended' WHERE org_id = @OrgId AND status = 'active'",
            new { OrgId = orgId });

        if (affected == 0)
            throw new KeyNotFoundException($"Organization {orgId} not found or already deleted.");

        _logger.LogInformation("Soft-deleted organization {OrgId} (status → suspended)", orgId);
    }
}
