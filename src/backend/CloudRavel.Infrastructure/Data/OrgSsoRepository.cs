using Dapper;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based per-organization SSO settings store. Admin connections — access
/// is gated at the app layer (org_admin), and the endpoints pass an explicit
/// org id, so RLS scoping is unnecessary here.
/// </summary>
public sealed class OrgSsoRepository : IOrgSsoRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<OrgSsoRepository> _logger;

    public OrgSsoRepository(ITenantDbConnectionFactory connectionFactory, ILogger<OrgSsoRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<OrgSsoSettings?> GetAsync(Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<OrgSsoSettings>(@"
            SELECT org_id AS OrgId, provider AS Provider, idp_tenant_id AS IdpTenantId,
                   idp_client_id AS IdpClientId, domain AS Domain,
                   client_secret_name AS ClientSecretName, enabled AS Enabled
            FROM org_sso_settings WHERE org_id = @OrgId", new { OrgId = orgId });
    }

    public async Task UpsertAsync(OrgSsoSettings s, string updatedBy)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE org_sso_settings AS target
            USING (SELECT @OrgId AS org_id) AS source
            ON target.org_id = source.org_id
            WHEN MATCHED THEN UPDATE SET
                provider = @Provider, idp_tenant_id = @IdpTenantId, idp_client_id = @IdpClientId,
                domain = @Domain, client_secret_name = @ClientSecretName, enabled = @Enabled,
                updated_at = SYSUTCDATETIME(), updated_by = @UpdatedBy
            WHEN NOT MATCHED THEN INSERT
                (org_id, provider, idp_tenant_id, idp_client_id, domain, client_secret_name, enabled, updated_by)
                VALUES (@OrgId, @Provider, @IdpTenantId, @IdpClientId, @Domain, @ClientSecretName, @Enabled, @UpdatedBy);";
        await conn.ExecuteAsync(sql, new
        {
            s.OrgId, s.Provider, s.IdpTenantId, s.IdpClientId, s.Domain, s.ClientSecretName, s.Enabled, UpdatedBy = updatedBy
        });
        _logger.LogInformation("SSO settings updated for org {OrgId} (provider {Provider}) by {Actor}", s.OrgId, s.Provider, updatedBy);
    }
}
