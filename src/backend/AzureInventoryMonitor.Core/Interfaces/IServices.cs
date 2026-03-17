namespace AzureInventoryMonitor.Core.Interfaces;

/// <summary>
/// Abstraction for tenant-scoped connections to Azure APIs.
/// Handles Lighthouse vs App Registration credential resolution.
/// </summary>
public interface IAzureCredentialFactory
{
    /// <summary>
    /// Returns a TokenCredential scoped to the given customer tenant.
    /// For Lighthouse: Uses the MSP managed identity (Lighthouse handles cross-tenant).
    /// For App Reg: Retrieves client secret/cert from Key Vault and builds a ClientSecretCredential.
    /// </summary>
    Task<Azure.Core.TokenCredential> GetCredentialForTenantAsync(Guid tenantId);
}

/// <summary>
/// SQL connection factory that sets RLS session context.
/// </summary>
public interface ITenantDbConnectionFactory
{
    /// <summary>
    /// Creates a SQL connection with the tenant_id session context set.
    /// Every query through this connection is automatically filtered by RLS.
    /// </summary>
    Task<System.Data.Common.DbConnection> CreateConnectionAsync(Guid tenantId);

    /// <summary>
    /// Creates a SQL connection without tenant context (for admin/system operations only).
    /// Requires explicit bypass_rls session context flag.
    /// </summary>
    Task<System.Data.Common.DbConnection> CreateAdminConnectionAsync();
}

/// <summary>
/// Service for collecting inventory from Azure Resource Graph.
/// </summary>
public interface IInventoryCollectionService
{
    /// <summary>
    /// Collects all resources from the tenant via Azure Resource Graph and stores them as a snapshot.
    /// </summary>
    Task<(long SnapshotId, int ResourceCount)> CollectInventoryAsync(Guid tenantId, string triggeredBy);
}

/// <summary>
/// Service for ingesting raw ARI output and normalizing it into the inventory schema.
/// </summary>
public interface IAriIngestionService
{
    Task<long> IngestSnapshotAsync(Guid tenantId, string blobPath, string triggeredBy);
}

/// <summary>
/// Service for polling Azure Resource Graph change history.
/// </summary>
public interface IChangePollingService
{
    Task PollChangesAsync(Guid tenantId, DateTime? fromOverride = null);
}

/// <summary>
/// Service for syncing recommendations from Advisor, Policy, and Defender.
/// </summary>
public interface IRecommendationSyncService
{
    Task SyncAdvisorAsync(Guid tenantId);
    Task SyncPolicyComplianceAsync(Guid tenantId);
    Task SyncDefenderFindingsAsync(Guid tenantId);
}
