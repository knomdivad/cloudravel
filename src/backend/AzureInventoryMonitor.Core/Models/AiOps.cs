namespace AzureInventoryMonitor.Core.Models;

// ============================================================================
// AIOps domain: anomalies, incidents, and gated auto-remediation.
//
// Pipeline:
//   detectors → Anomaly → (correlation) → Incident
//                       → (playbook match) → RemediationAction
//   RemediationAction: proposed → pending_approval → approved → executing
//                      → succeeded | failed        (or rejected/expired)
//
// Approval gating is a per-tenant policy (AutoRemediationMode). Low-risk
// playbooks may auto-approve when the tenant opts in; high-risk actions are
// ALWAYS gated regardless of tenant policy.
// ============================================================================

/// <summary>
/// A statistically or heuristically detected deviation from the tenant's baseline.
/// Fingerprint dedupes repeated detections of the same underlying condition.
/// </summary>
public sealed class Anomaly
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Stable hash of (kind, resource/metric scope) used for dedup across scans.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public AnomalyKind Kind { get; set; }
    public AnomalySeverity Severity { get; set; }
    public AnomalyStatus Status { get; set; } = AnomalyStatus.Open;
    public CloudProvider Provider { get; set; } = CloudProvider.Azure;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ResourceId { get; set; }

    /// <summary>Metric that tripped the detector, e.g. "changes.security.hourly".</summary>
    public string? MetricName { get; set; }
    public double? ObservedValue { get; set; }
    public double? BaselineMean { get; set; }
    public double? BaselineStdDev { get; set; }
    /// <summary>Z-score (or detector-specific score) of the observation vs baseline.</summary>
    public double? Score { get; set; }

    public DateTime DetectedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? DetailsJson { get; set; }
    public long? IncidentId { get; set; }
}

public enum AnomalyKind
{
    /// <summary>Change volume significantly above the rolling baseline.</summary>
    ChangeVelocitySpike,
    /// <summary>New critical/high security findings or a security posture regression.</summary>
    SecurityPostureRegression,
    /// <summary>Cost-impacting drift: SKU/capacity growth, new cost recommendations spike.</summary>
    CostAnomaly,
    /// <summary>Security-relevant configuration drift on a specific resource.</summary>
    ConfigurationDrift,
    /// <summary>Changes made by an actor never seen in the baseline window.</summary>
    UnusualActorActivity,
    /// <summary>Telemetry pipeline health: snapshots or change polling gone stale.</summary>
    StaleTelemetry,
    /// <summary>Sudden growth in resource counts (sprawl / runaway automation).</summary>
    ResourceSprawl
}

public enum AnomalySeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}

public enum AnomalyStatus
{
    Open,
    Acknowledged,
    Resolved,
    Suppressed,
    FalsePositive
}

/// <summary>
/// Persisted rolling baseline for a tenant metric, refreshed by the anomaly scan.
/// Keeping baselines in SQL lets detectors run cheaply and makes the math auditable.
/// </summary>
public sealed class MetricBaseline
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string MetricKey { get; set; } = string.Empty;
    public int WindowHours { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public int SampleCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ============================================================================
// Incidents
// ============================================================================

/// <summary>
/// An operational incident — the unit of work an MSP would normally track in
/// a ticketing system. Created automatically from high/critical anomalies or
/// manually by an operator, with a full timeline of events.
/// </summary>
public sealed class Incident
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public AnomalySeverity Severity { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    /// <summary>anomaly | manual | ai</summary>
    public string Source { get; set; } = "anomaly";
    public string? SummaryMarkdown { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? MitigatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    /// <summary>SLA clock: acknowledge/resolve by, derived from severity policy.</summary>
    public DateTime? SlaDueAt { get; set; }
    public int AnomalyCount { get; set; }
    public int RemediationCount { get; set; }
}

public enum IncidentStatus
{
    Open,
    Acknowledged,
    Mitigated,
    Resolved,
    Closed
}

/// <summary>Append-only timeline entry on an incident.</summary>
public sealed class IncidentEvent
{
    public long Id { get; set; }
    public long IncidentId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime OccurredAt { get; set; }
    /// <summary>created | status_change | note | anomaly_linked | remediation_linked | ai_summary</summary>
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ActorName { get; set; }
    public string? DetailsJson { get; set; }
}

// ============================================================================
// Remediation
// ============================================================================

/// <summary>
/// A catalog entry describing a safe, allow-listed remediation the platform can
/// perform. Free-form actions are never executed — every remediation maps to a
/// playbook with a typed executor per cloud provider.
/// </summary>
public sealed class RemediationPlaybook
{
    public string PlaybookKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public CloudProvider Provider { get; set; }
    /// <summary>security | cost | operational | governance</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>Executor key, e.g. "azure.vm.deallocate", "aws.ec2.stop_instance".</summary>
    public string ActionType { get; set; } = string.Empty;
    public RemediationRisk RiskLevel { get; set; } = RemediationRisk.Medium;
    /// <summary>When true the playbook can never auto-approve, regardless of tenant policy.</summary>
    public bool AlwaysRequiresApproval { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>JSON schema describing required parameters (documentation + validation).</summary>
    public string? ParametersSchemaJson { get; set; }
}

public enum RemediationRisk
{
    Low,
    Medium,
    High
}

/// <summary>
/// A concrete remediation proposed against a tenant resource, subject to the
/// approval gate. Execution is idempotent per action row; results and errors
/// are captured for the audit trail.
/// </summary>
public sealed class RemediationAction
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string PlaybookKey { get; set; } = string.Empty;
    public CloudProvider Provider { get; set; }
    public string? ResourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Why this action was proposed (detector evidence or AI rationale).</summary>
    public string Reason { get; set; } = string.Empty;
    public string? ParametersJson { get; set; }
    public RemediationStatus Status { get; set; } = RemediationStatus.Proposed;
    public RemediationRisk RiskLevel { get; set; } = RemediationRisk.Medium;
    /// <summary>system:anomaly | ai:query | user:{objectId}</summary>
    public string RequestedBy { get; set; } = string.Empty;
    public long? AnomalyId { get; set; }
    public long? IncidentId { get; set; }
    /// <summary>auto | gated — how approval was (or must be) obtained.</summary>
    public string ApprovalMode { get; set; } = "gated";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Pending approvals expire so stale actions never fire against a drifted environment.</summary>
    public DateTime? ExpiresAt { get; set; }
}

public enum RemediationStatus
{
    Proposed,
    PendingApproval,
    Approved,
    Rejected,
    Executing,
    Succeeded,
    Failed,
    Cancelled,
    Expired
}

/// <summary>
/// Tenant-level auto-remediation policy.
///   Disabled — platform only proposes; nothing is ever queued for execution.
///   Gated    — every action requires explicit human approval (default).
///   Auto     — low-risk actions auto-approve; medium/high remain gated.
/// </summary>
public enum AutoRemediationMode
{
    Disabled,
    Gated,
    Auto
}
