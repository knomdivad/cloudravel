namespace CloudRavel.Core.Models;

/// <summary>
/// Read-only enforcement for assessment engagements.
///
/// An active assessment workspace must not take actions against the customer's
/// estate: the deliverable is a ranked fix-list, not changes. Defense in depth:
///   1. Starting an assessment forces AutoRemediationMode = Disabled (API layer).
///   2. The remediation engine checks this policy before approving or executing
///      anything (RemediationService), so no code path — inline approval, AI
///      proposal approval, or the queue-drain worker — can act on an
///      assessment workspace even if a tenant row is hand-edited.
/// Proposals stay allowed: they are inert fix-list raw material.
/// </summary>
public static class AssessmentPolicy
{
    /// <summary>True when the workspace must refuse remediation approval/execution.</summary>
    public static bool BlocksActions(Tenant tenant)
    {
        if (tenant.EngagementKind != EngagementKind.Assessment)
            return false;

        // Completed assessments no longer block — the workspace is read-only by
        // convention, but closing the engagement ends the hard gate.
        return tenant.AssessmentCompletedAt is null;
    }

    /// <summary>Standard InvalidOperationException for the blocked case (engine throws, API maps to 409).</summary>
    public static InvalidOperationException BlockedError(Tenant tenant) => new(
        $"Workspace '{tenant.DisplayName}' is in a read-only assessment engagement; " +
        "remediation approval/execution is blocked until the assessment is completed or converted " +
        "to ongoing monitoring.");
}
