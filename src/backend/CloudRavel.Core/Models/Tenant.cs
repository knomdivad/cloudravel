namespace CloudRavel.Core.Models;

/// <summary>
/// Represents an onboarded customer tenant.
/// </summary>
public sealed class Tenant
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public OnboardingMethod OnboardingMethod { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public int SnapshotFrequencyMinutes { get; set; } = 360;
    public int ChangePollFrequencyMinutes { get; set; } = 15;
    public string? SecretName { get; set; }
    public string? LighthouseDelegationId { get; set; }

    /// <summary>Governs whether proposed remediations require human approval. Default: gated.</summary>
    public AutoRemediationMode AutoRemediationMode { get; set; } = AutoRemediationMode.Gated;

    /// <summary>Master switch for proactive anomaly scanning on this tenant.</summary>
    public bool AiOpsMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// Engagement lifecycle: Standard (ongoing monitoring) or Assessment (a
    /// time-boxed, read-only fixed-fee assessment). Assessments force the
    /// approval gate closed — see <see cref="AssessmentPolicy"/>.
    /// </summary>
    public EngagementKind EngagementKind { get; set; } = EngagementKind.Standard;

    /// <summary>When the assessment watch window opened (null unless an assessment).</summary>
    public DateTime? AssessmentStartedAt { get; set; }

    /// <summary>When the assessment watch window closes: started + configured days.</summary>
    public DateTime? AssessmentEndsAt { get; set; }

    /// <summary>When the assessment was closed out (fix-list delivered).</summary>
    public DateTime? AssessmentCompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public enum OnboardingMethod
{
    Lighthouse,
    AppRegistration
}

public enum TenantStatus
{
    Active,
    Degraded,
    Suspended,
    Offboarded
}

/// <summary>
/// What kind of commercial engagement this workspace delivers.
///   Standard   — ongoing AIOps monitoring (the default platform mode).
///   Assessment — a time-boxed, read-only fixed-fee assessment: snapshot the
///                estate once, watch changes for N days, produce the ranked
///                fix-list. Remediation approval/execution is blocked while
///                active; converting back to Standard restores action.
/// </summary>
public enum EngagementKind
{
    Standard,
    Assessment
}
