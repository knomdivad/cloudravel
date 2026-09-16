namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// Structured, tenant-scoped grounding context handed to the customer chat model.
/// Every value is read server-side from the caller's own workspace; the user's
/// chat text never influences these queries.
/// </summary>
public sealed record ChatWorkspaceContext
{
    public Guid TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public int TotalResources { get; init; }
    public DateTime? LastSnapshotAt { get; init; }
    public int ChangesLast7d { get; init; }
    public int OpenAdvisorRecommendations { get; init; }
    public int EstimatedAnnualSavingsUsd { get; init; }
    public int OpenDefenderFindings { get; init; }
    public int CriticalDefenderFindings { get; init; }
    public int NonCompliantPolicies { get; init; }
    public IReadOnlyList<ChatResourceTypeCount> ResourceTypes { get; init; } = [];
    public IReadOnlyList<ChatTopFinding> TopFindings { get; init; } = [];
    public IReadOnlyList<ChatRecentChange> RecentChanges { get; init; } = [];
    public IReadOnlyList<ChatCloudConnection> CloudConnections { get; init; } = [];
}

public sealed record ChatResourceTypeCount(string ResourceType, int Count);

public sealed record ChatTopFinding(string Severity, string Title, string ResourceId, DateTime LastSeenAt);

public sealed record ChatRecentChange(string ResourceId, string ChangeType, string Classification, string? ActorName, DateTime DetectedAt);

public sealed record ChatCloudConnection(string Provider, string Name, string Status);
