using System.Text.Json;
using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based repository for linked multi-cloud accounts (AWS accounts, GCP projects).
/// </summary>
public sealed class CloudAccountRepository : ICloudAccountRepository
{
    private const string SelectColumns = @"
        SELECT account_id AS AccountId, tenant_id AS TenantId, org_id AS OrgId, provider AS Provider,
               external_id AS ExternalId, display_name AS DisplayName, status AS Status,
               credential_secret_name AS CredentialSecretName, regions_json AS RegionsJson,
               last_inventory_at AS LastInventoryAt, last_error AS LastError,
               created_at AS CreatedAt, created_by AS CreatedBy
        FROM cloud_accounts";

    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<CloudAccountRepository> _logger;

    public CloudAccountRepository(ITenantDbConnectionFactory connectionFactory, ILogger<CloudAccountRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<CloudAccount> CreateAsync(CloudAccount account)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();

        account.AccountId = account.AccountId == Guid.Empty ? Guid.NewGuid() : account.AccountId;

        const string sql = @"
            INSERT INTO cloud_accounts
                (account_id, tenant_id, org_id, provider, external_id, display_name, status,
                 credential_secret_name, regions_json, created_by)
            VALUES
                (@AccountId, @TenantId, @OrgId, @Provider, @ExternalId, @DisplayName, @Status,
                 @CredentialSecretName, @RegionsJson, @CreatedBy)";

        await conn.ExecuteAsync(sql, new
        {
            account.AccountId,
            account.TenantId,
            account.OrgId,
            Provider = account.Provider.ToString(),
            account.ExternalId,
            account.DisplayName,
            Status = account.Status.ToString(),
            account.CredentialSecretName,
            RegionsJson = account.Regions != null ? JsonSerializer.Serialize(account.Regions) : null,
            account.CreatedBy
        });

        _logger.LogInformation("Linked {Provider} account {ExternalId} to tenant {TenantId}",
            account.Provider, account.ExternalId, account.TenantId);
        return account;
    }

    public async Task<CloudAccount?> GetByIdAsync(Guid tenantId, Guid accountId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId AND account_id = @AccountId";
        var row = await conn.QuerySingleOrDefaultAsync<CloudAccountRow>(sql, new { TenantId = tenantId, AccountId = accountId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<CloudAccount>> GetByTenantAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId ORDER BY provider, display_name";
        var rows = await conn.QueryAsync<CloudAccountRow>(sql, new { TenantId = tenantId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<CloudAccount>> GetByOrgAsync(Guid tenantId, Guid orgId)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = SelectColumns + " WHERE tenant_id = @TenantId AND org_id = @OrgId ORDER BY display_name";
        var rows = await conn.QueryAsync<CloudAccountRow>(sql, new { TenantId = tenantId, OrgId = orgId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<CloudAccount>> GetAllActiveAsync()
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var sql = SelectColumns + " WHERE status IN ('Connected', 'Degraded') ORDER BY tenant_id, provider";
        var rows = await conn.QueryAsync<CloudAccountRow>(sql);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpdateStatusAsync(Guid accountId, CloudAccountStatus status, string? lastError)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE cloud_accounts SET status = @Status, last_error = @LastError
            WHERE account_id = @AccountId",
            new { AccountId = accountId, Status = status.ToString(), LastError = lastError });
    }

    public async Task TouchInventoryAsync(Guid accountId, DateTime inventoryAt)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE cloud_accounts SET last_inventory_at = @At, last_error = NULL WHERE account_id = @AccountId",
            new { AccountId = accountId, At = inventoryAt });
    }

    /// <summary>Row type: regions stored as a JSON array string.</summary>
    private sealed class CloudAccountRow
    {
        public Guid AccountId { get; set; }
        public Guid TenantId { get; set; }
        public Guid OrgId { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? CredentialSecretName { get; set; }
        public string? RegionsJson { get; set; }
        public DateTime? LastInventoryAt { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;

        public CloudAccount ToModel()
        {
            var account = new CloudAccount
            {
                AccountId = AccountId,
                TenantId = TenantId,
                OrgId = OrgId,
                ExternalId = ExternalId,
                DisplayName = DisplayName,
                CredentialSecretName = CredentialSecretName,
                LastInventoryAt = LastInventoryAt,
                LastError = LastError,
                CreatedAt = CreatedAt,
                CreatedBy = CreatedBy
            };
            if (Enum.TryParse<CloudProvider>(Provider, true, out var p)) account.Provider = p;
            if (Enum.TryParse<CloudAccountStatus>(Status, true, out var s)) account.Status = s;
            if (!string.IsNullOrEmpty(RegionsJson))
            {
                try { account.Regions = JsonSerializer.Deserialize<List<string>>(RegionsJson); }
                catch { /* malformed JSON — leave null */ }
            }
            return account;
        }
    }
}
