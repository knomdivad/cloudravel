namespace CloudRavel.Core.Models;

/// <summary>
/// A normalized Azure Advisor recommendation.
/// </summary>
public sealed class AdvisorRecommendation
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string RecommendationId { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RemediationAction { get; set; }
    public decimal? EstimatedSavings { get; set; }
    public string? Currency { get; set; } = "USD";
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public RecommendationLifecycle LifecycleStatus { get; set; } = RecommendationLifecycle.Active;
    public Guid? AcknowledgedBy { get; set; }
}

/// <summary>
/// Azure Policy compliance state for a resource.
/// </summary>
public sealed class PolicyComplianceRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string PolicyAssignmentId { get; set; } = string.Empty;
    public string PolicyDefinitionId { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ComplianceState { get; set; } = string.Empty;
    public string? Category { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastEvaluatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// Microsoft Defender for Cloud finding.
/// </summary>
public sealed class DefenderFinding
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string FindingId { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string AssessmentName { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RemediationSteps { get; set; }
    public List<string>? Categories { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public enum RecommendationLifecycle
{
    Active,
    Acknowledged,
    Snoozed,
    Resolved,
    Dismissed
}
