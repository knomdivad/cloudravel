namespace AzureInventoryMonitor.Core.Models;

/// <summary>
/// Represents a point-in-time ARI inventory snapshot for a tenant.
/// </summary>
public sealed class InventorySnapshot
{
    public long SnapshotId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public SnapshotStatus Status { get; set; } = SnapshotStatus.Running;
    public int? ResourceCount { get; set; }
    public string? BlobPath { get; set; }
    public string? ErrorMessage { get; set; }
    public string TriggeredBy { get; set; } = "schedule";
}

public enum SnapshotStatus
{
    Running,
    Completed,
    Failed,
    Partial
}

/// <summary>
/// A normalized resource from an ARI snapshot.
/// </summary>
public sealed class InventoryResource
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public long SnapshotId { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? SkuName { get; set; }
    public string? SkuTier { get; set; }
    public int? SkuCapacity { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public string? IdentityType { get; set; }
    public List<string>? IdentityPrincipalIds { get; set; }
    public string? PropertiesJson { get; set; }
    public string? NetworkingJson { get; set; }
    public string? SecurityConfigJson { get; set; }
}
