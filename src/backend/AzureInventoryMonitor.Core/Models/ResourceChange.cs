namespace AzureInventoryMonitor.Core.Models;

/// <summary>
/// A resource configuration change detected via Resource Graph Change History.
/// </summary>
public sealed class ResourceChange
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string ChangeId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public ChangeType ChangeType { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime IngestedAt { get; set; }
    public List<PropertyChange>? ChangedProperties { get; set; }
    public string? ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ClientType { get; set; }
    public ChangeClassification Classification { get; set; } = ChangeClassification.Operational;
    public ChangeSeverity? Severity { get; set; }
}

public sealed class PropertyChange
{
    public string Path { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
}

public enum ChangeType
{
    Create,
    Update,
    Delete
}

public enum ChangeClassification
{
    Security,
    Governance,
    Cost,
    Operational
}

public enum ChangeSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}
