namespace AzureInventoryMonitor.Core.DTOs;

// ============================================================================
// AIOps API DTOs: anomalies, incidents, remediation, cloud accounts, ops summary
// ============================================================================

// --- Anomalies ---

public sealed class AnomaliesResponse
{
    public IReadOnlyList<AnomalyDto> Anomalies { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class AnomalyDto
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ResourceId { get; set; }
    public string? MetricName { get; set; }
    public double? ObservedValue { get; set; }
    public double? BaselineMean { get; set; }
    public double? Score { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public long? IncidentId { get; set; }
}

public sealed class UpdateAnomalyStatusRequest
{
    public string Status { get; set; } = string.Empty;
}

// --- Incidents ---

public sealed class IncidentsResponse
{
    public IReadOnlyList<IncidentDto> Incidents { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class IncidentDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? SummaryMarkdown { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? SlaDueAt { get; set; }
    public bool SlaBreached { get; set; }
    public int AnomalyCount { get; set; }
    public int RemediationCount { get; set; }
    public IReadOnlyList<IncidentEventDto>? Events { get; set; }
}

public sealed class IncidentEventDto
{
    public DateTime OccurredAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ActorName { get; set; }
}

public sealed class UpdateIncidentRequest
{
    public string? Status { get; set; }
    public string? AssignedTo { get; set; }
    public string? Note { get; set; }
}

// --- Remediation ---

public sealed class RemediationActionsResponse
{
    public IReadOnlyList<RemediationActionDto> Actions { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class RemediationActionDto
{
    public long Id { get; set; }
    public string PlaybookKey { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? ParametersJson { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public long? AnomalyId { get; set; }
    public long? IncidentId { get; set; }
    public string ApprovalMode { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public sealed class ProposeRemediationRequest
{
    public string PlaybookKey { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? Title { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ParametersJson { get; set; }
}

public sealed class RejectRemediationRequest
{
    public string? Reason { get; set; }
}

public sealed class PlaybooksResponse
{
    public IReadOnlyList<PlaybookDto> Playbooks { get; set; } = [];
}

public sealed class PlaybookDto
{
    public string PlaybookKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public bool AlwaysRequiresApproval { get; set; }
    public string? ParametersSchemaJson { get; set; }
}

// --- Cloud orgs (top-level provider-agnostic grouping) ---

public sealed class CloudOrgsResponse
{
    public IReadOnlyList<CloudOrgDto> Orgs { get; set; } = [];
}

public sealed class CloudOrgDto
{
    public Guid OrgId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>Accounts/projects belonging to this org.</summary>
    public IReadOnlyList<CloudAccountDto> Accounts { get; set; } = [];
}

public sealed class CreateCloudOrgRequest
{
    /// <summary>azure | aws | gcp</summary>
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional: AWS Organization ID / GCP Organization ID / Azure tenant GUID.</summary>
    public string? ExternalId { get; set; }
}

// --- Cloud accounts (children of an org) ---

public sealed class CloudAccountsResponse
{
    public IReadOnlyList<CloudAccountDto> Accounts { get; set; } = [];
}

public sealed class CloudAccountDto
{
    public Guid AccountId { get; set; }
    public Guid OrgId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string>? Regions { get; set; }
    public DateTime? LastInventoryAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LinkCloudAccountRequest
{
    /// <summary>The org this account/project belongs to (required).</summary>
    public Guid OrgId { get; set; }
    /// <summary>aws | gcp</summary>
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Credential payload stored in the secret store; never persisted in SQL.</summary>
    public string? CredentialJson { get; set; }
    public List<string>? Regions { get; set; }
}

// --- Operations summary (AIOps dashboard) ---

public sealed class OpsSummaryResponse
{
    public Guid TenantId { get; set; }
    public int OpenAnomalies { get; set; }
    public int CriticalAnomalies { get; set; }
    public int OpenIncidents { get; set; }
    public int SlaBreachedIncidents { get; set; }
    public int PendingApprovals { get; set; }
    public int RemediationsLast7d { get; set; }
    public int AutoRemediationsLast7d { get; set; }
    public double? MeanTimeToResolveHours { get; set; }
    public string AutoRemediationMode { get; set; } = string.Empty;
    public bool MonitoringEnabled { get; set; }
    public IReadOnlyList<AnomalyDto> RecentAnomalies { get; set; } = [];
    public IReadOnlyList<IncidentDto> RecentIncidents { get; set; } = [];
    public IReadOnlyList<RemediationActionDto> RecentRemediations { get; set; } = [];
    public IReadOnlyList<CloudAccountDto> CloudAccounts { get; set; } = [];
}
