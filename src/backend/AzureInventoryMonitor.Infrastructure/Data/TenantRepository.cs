using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Data;

/// <summary>
/// Dapper-based tenant repository.
/// Tenant operations use admin connections since RLS filters by tenant_id,
/// and tenant management requires cross-tenant visibility.
/// </summary>
/// <remarks>
/// The DB stores onboarding_method as snake_case ('app_registration', 'lighthouse')
/// but the C# enum uses PascalCase (AppRegistration, Lighthouse).
/// Dapper ignores TypeHandlers for enums, so we use SQL CASE expressions on reads
/// and ToDbValue() for writes.
/// </remarks>
public sealed class TenantRepository : ITenantRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<TenantRepository> _logger;

    // SQL CASE to convert snake_case DB values to C# enum names for Dapper parsing
    private const string OnboardingMethodCase =
        "CASE onboarding_method WHEN 'app_registration' THEN 'AppRegistration' WHEN 'lighthouse' THEN 'Lighthouse' ELSE onboarding_method END";

    private const string OnboardingMethodCaseT =
        "CASE t.onboarding_method WHEN 'app_registration' THEN 'AppRegistration' WHEN 'lighthouse' THEN 'Lighthouse' ELSE t.onboarding_method END";

    public TenantRepository(ITenantDbConnectionFactory connectionFactory, ILogger<TenantRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private static string ToDbValue(OnboardingMethod method) => method switch
    {
        OnboardingMethod.Lighthouse => "lighthouse",
        OnboardingMethod.AppRegistration => "app_registration",
        _ => method.ToString().ToLowerInvariant()
    };

    public async Task<Tenant?> GetByIdAsync(Guid tenantId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        string sql = $@"
            SELECT tenant_id AS TenantId, display_name AS DisplayName, azure_tenant_id AS AzureTenantId,
                   {OnboardingMethodCase} AS OnboardingMethod, status AS Status,
                   snapshot_frequency_minutes AS SnapshotFrequencyMinutes,
                   change_poll_frequency_minutes AS ChangePollFrequencyMinutes,
                   secret_name AS SecretName,
                   lighthouse_delegation_id AS LighthouseDelegationId,
                   auto_remediation_mode AS AutoRemediationMode,
                   aiops_monitoring_enabled AS AiOpsMonitoringEnabled,
                   created_at AS CreatedAt, updated_at AS UpdatedAt, created_by AS CreatedBy
            FROM tenants WHERE tenant_id = @TenantId";

        return await conn.QuerySingleOrDefaultAsync<Tenant>(sql, new { TenantId = tenantId });
    }

    public async Task<IReadOnlyList<Tenant>> GetAllActiveAsync()
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        string sql = $@"
            SELECT tenant_id AS TenantId, display_name AS DisplayName, azure_tenant_id AS AzureTenantId,
                   {OnboardingMethodCase} AS OnboardingMethod, status AS Status,
                   snapshot_frequency_minutes AS SnapshotFrequencyMinutes,
                   change_poll_frequency_minutes AS ChangePollFrequencyMinutes,
                   secret_name AS SecretName,
                   lighthouse_delegation_id AS LighthouseDelegationId,
                   auto_remediation_mode AS AutoRemediationMode,
                   aiops_monitoring_enabled AS AiOpsMonitoringEnabled,
                   created_at AS CreatedAt, updated_at AS UpdatedAt, created_by AS CreatedBy
            FROM tenants WHERE status IN ('active', 'degraded')
            ORDER BY display_name";

        var results = await conn.QueryAsync<Tenant>(sql);
        return results.ToList();
    }

    public async Task<IReadOnlyList<Tenant>> GetByUserAccessAsync(Guid userId)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        string sql = $@"
            SELECT t.tenant_id AS TenantId, t.display_name AS DisplayName, t.azure_tenant_id AS AzureTenantId,
                   {OnboardingMethodCaseT} AS OnboardingMethod, t.status AS Status,
                   t.snapshot_frequency_minutes AS SnapshotFrequencyMinutes,
                   t.change_poll_frequency_minutes AS ChangePollFrequencyMinutes,
                   t.secret_name AS SecretName,
                   t.lighthouse_delegation_id AS LighthouseDelegationId,
                   t.auto_remediation_mode AS AutoRemediationMode,
                   t.aiops_monitoring_enabled AS AiOpsMonitoringEnabled,
                   t.created_at AS CreatedAt, t.updated_at AS UpdatedAt, t.created_by AS CreatedBy
            FROM tenants t
            INNER JOIN user_tenant_access uta ON t.tenant_id = uta.tenant_id
            WHERE uta.user_id = @UserId AND t.status IN ('active', 'degraded')
            ORDER BY t.display_name";

        var results = await conn.QueryAsync<Tenant>(sql, new { UserId = userId });
        return results.ToList();
    }

    public async Task<Tenant> CreateAsync(Tenant tenant, Guid createdBy)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();

        tenant.TenantId = tenant.TenantId == Guid.Empty ? Guid.NewGuid() : tenant.TenantId;

        const string sql = @"
            INSERT INTO tenants (tenant_id, display_name, azure_tenant_id, onboarding_method, status,
                                 snapshot_frequency_minutes, change_poll_frequency_minutes,
                                 secret_name, lighthouse_delegation_id,
                                 auto_remediation_mode, aiops_monitoring_enabled, created_by)
            VALUES (@TenantId, @DisplayName, @AzureTenantId, @OnboardingMethod, @Status,
                    @SnapshotFrequencyMinutes, @ChangePollFrequencyMinutes,
                    @SecretName, @LighthouseDelegationId,
                    @AutoRemediationMode, @AiOpsMonitoringEnabled, @CreatedBy)";

        await conn.ExecuteAsync(sql, new
        {
            tenant.TenantId,
            tenant.DisplayName,
            tenant.AzureTenantId,
            OnboardingMethod = ToDbValue(tenant.OnboardingMethod),
            Status = tenant.Status.ToString().ToLowerInvariant(),
            tenant.SnapshotFrequencyMinutes,
            tenant.ChangePollFrequencyMinutes,
            tenant.SecretName,
            tenant.LighthouseDelegationId,
            AutoRemediationMode = tenant.AutoRemediationMode.ToString().ToLowerInvariant(),
            tenant.AiOpsMonitoringEnabled,
            CreatedBy = createdBy.ToString()
        });

        _logger.LogInformation("Created tenant {TenantId} ({DisplayName}) via {Method}",
            tenant.TenantId, tenant.DisplayName, tenant.OnboardingMethod);

        return (await GetByIdAsync(tenant.TenantId))!;
    }

    public async Task UpdateStatusAsync(Guid tenantId, TenantStatus status)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE tenants SET status = @Status, updated_at = SYSUTCDATETIME()
            WHERE tenant_id = @TenantId";

        var affected = await conn.ExecuteAsync(sql, new
        {
            TenantId = tenantId,
            Status = status.ToString().ToLowerInvariant()
        });

        if (affected == 0)
            throw new KeyNotFoundException($"Tenant {tenantId} not found.");

        _logger.LogInformation("Updated tenant {TenantId} status to {Status}", tenantId, status);
    }

    public async Task UpdateAsync(Tenant tenant)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE tenants SET 
                display_name = @DisplayName,
                onboarding_method = @OnboardingMethod,
                snapshot_frequency_minutes = @SnapshotFrequencyMinutes,
                change_poll_frequency_minutes = @ChangePollFrequencyMinutes,
                secret_name = @SecretName,
                lighthouse_delegation_id = @LighthouseDelegationId,
                auto_remediation_mode = @AutoRemediationMode,
                aiops_monitoring_enabled = @AiOpsMonitoringEnabled,
                updated_at = SYSUTCDATETIME()
            WHERE tenant_id = @TenantId";

        var affected = await conn.ExecuteAsync(sql, new
        {
            tenant.TenantId,
            tenant.DisplayName,
            OnboardingMethod = ToDbValue(tenant.OnboardingMethod),
            tenant.SnapshotFrequencyMinutes,
            tenant.ChangePollFrequencyMinutes,
            tenant.SecretName,
            tenant.LighthouseDelegationId,
            AutoRemediationMode = tenant.AutoRemediationMode.ToString().ToLowerInvariant(),
            tenant.AiOpsMonitoringEnabled
        });

        if (affected == 0)
            throw new KeyNotFoundException($"Tenant {tenant.TenantId} not found.");

        _logger.LogInformation("Updated tenant {TenantId} ({DisplayName})", tenant.TenantId, tenant.DisplayName);
    }
}
