using AzureInventoryMonitor.Core.Models;

namespace AzureInventoryMonitor.Core.Interfaces;

/// <summary>
/// Repository for tenant management operations.
/// </summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid tenantId);
    Task<IReadOnlyList<Tenant>> GetAllActiveAsync();
    Task<IReadOnlyList<Tenant>> GetByUserAccessAsync(Guid userId);
    Task<Tenant> CreateAsync(Tenant tenant, Guid createdBy);
    Task UpdateStatusAsync(Guid tenantId, TenantStatus status);
    Task UpdateAsync(Tenant tenant);
    /// <summary>
    /// Register the Azure subscriptions to monitor for a tenant (the "specific
    /// subscriptions" choice). Idempotent per (tenant, subscription). An empty
    /// list means "all subscriptions" — nothing is pinned and discovery covers all.
    /// </summary>
    Task AddSubscriptionsAsync(Guid tenantId, IReadOnlyList<string> subscriptionIds);
}

/// <summary>
/// Repository for inventory data.
/// </summary>
public interface IInventoryRepository
{
    Task<InventorySnapshot> CreateSnapshotAsync(Guid tenantId, string triggeredBy);
    Task CompleteSnapshotAsync(long snapshotId, int resourceCount, string blobPath);
    Task FailSnapshotAsync(long snapshotId, string errorMessage);
    Task BulkInsertResourcesAsync(long snapshotId, IEnumerable<InventoryResource> resources);
    /// <summary>Removes a provider's rows from a snapshot so a multi-cloud re-sync can replace them.</summary>
    Task DeleteResourcesByProviderAsync(long snapshotId, string provider);
    Task SetLatestSnapshotAsync(Guid tenantId, long snapshotId);
    Task<InventorySnapshot?> GetLatestSnapshotAsync(Guid tenantId);
    Task<InventorySnapshot?> GetSnapshotByIdAsync(long snapshotId);
    Task<IReadOnlyList<InventorySnapshot>> GetSnapshotHistoryAsync(Guid tenantId, int limit = 50);
    Task<IReadOnlyList<InventoryResource>> GetResourcesAsync(
        Guid tenantId,
        long? snapshotId = null,
        string? resourceType = null,
        string? subscriptionId = null,
        string? resourceGroup = null,
        int offset = 0,
        int limit = 100);
    Task<InventoryResource?> GetResourceByIdAsync(Guid tenantId, string resourceId, long? snapshotId = null);
    Task<int> GetResourceCountAsync(Guid tenantId, long? snapshotId = null);
    Task<IReadOnlyList<ResourceTypeSummary>> GetResourceTypeSummaryAsync(Guid tenantId, long? snapshotId = null);
    /// <summary>
    /// Latest-snapshot resource counts for a provider, keyed by subscription_id
    /// (which for AWS/GCP is the account/project external id). Powers the per-org
    /// and per-account rollups on the Clouds page.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetResourceCountsBySubscriptionAsync(Guid tenantId, string provider);
}

/// <summary>
/// Repository for change intelligence data.
/// </summary>
public interface IChangeRepository
{
    Task UpsertChangesAsync(IEnumerable<ResourceChange> changes);
    Task<IReadOnlyList<ResourceChange>> GetChangesAsync(
        Guid tenantId,
        DateTime? from = null,
        DateTime? to = null,
        string? resourceId = null,
        ChangeClassification? classification = null,
        int offset = 0,
        int limit = 100);
    Task<IReadOnlyList<ResourceChange>> GetRecentChangesAsync(Guid tenantId, int hours = 24, int limit = 100);
    Task<int> GetChangeCountAsync(Guid tenantId, DateTime? from = null, DateTime? to = null);
    Task<IReadOnlyList<ChangeTimeline>> GetChangeTimelineAsync(Guid tenantId, DateTime from, DateTime to);
}

/// <summary>
/// Repository for recommendations, policy, and defender data.
/// </summary>
public interface IRecommendationRepository
{
    Task UpsertAdvisorRecommendationsAsync(IEnumerable<AdvisorRecommendation> recommendations);
    Task<IReadOnlyList<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(
        Guid tenantId,
        string? category = null,
        RecommendationLifecycle? status = null,
        int offset = 0,
        int limit = 100);
    Task UpdateRecommendationLifecycleAsync(Guid tenantId, string recommendationId, RecommendationLifecycle status, Guid? acknowledgedBy);

    Task UpsertPolicyComplianceAsync(IEnumerable<PolicyComplianceRecord> records);
    Task<IReadOnlyList<PolicyComplianceRecord>> GetPolicyComplianceAsync(
        Guid tenantId,
        string? complianceState = null,
        int offset = 0,
        int limit = 100);

    Task UpsertDefenderFindingsAsync(IEnumerable<DefenderFinding> findings);
    Task<IReadOnlyList<DefenderFinding>> GetDefenderFindingsAsync(
        Guid tenantId,
        string? severity = null,
        string? status = null,
        int offset = 0,
        int limit = 100);
}

/// <summary>
/// Aggregated resource type summary.
/// </summary>
public sealed class ResourceTypeSummary
{
    public string ResourceType { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Change count per time bucket for timeline charts.
/// </summary>
public sealed class ChangeTimeline
{
    public DateTime BucketStart { get; set; }
    public int TotalChanges { get; set; }
    public int SecurityChanges { get; set; }
    public int GovernanceChanges { get; set; }
    public int CostChanges { get; set; }
    public int OperationalChanges { get; set; }
}
