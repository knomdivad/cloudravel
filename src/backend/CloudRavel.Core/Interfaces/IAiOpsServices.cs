using CloudRavel.Core.Models;

namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Proactive anomaly detection over the platform's authoritative data stores.
/// Runs on a timer per tenant; detectors compare current observations against
/// persisted rolling baselines and open anomalies (deduped by fingerprint).
/// </summary>
public interface IAnomalyDetectionService
{
    /// <summary>
    /// Runs all detectors for a tenant. Returns the anomalies that are newly
    /// opened by this scan (existing open fingerprints are refreshed, not returned).
    /// </summary>
    Task<IReadOnlyList<Anomaly>> ScanTenantAsync(Guid tenantId);
}

/// <summary>
/// The remediation engine: proposes actions from playbooks, enforces the
/// approval gate, and executes approved actions via cloud provider adapters.
/// </summary>
public interface IRemediationService
{
    /// <summary>
    /// Creates a remediation action in Proposed/PendingApproval state (or
    /// Approved immediately when tenant policy allows auto-approval for the
    /// playbook's risk level). Never executes inline — execution is queued.
    /// </summary>
    Task<RemediationAction> ProposeAsync(
        Guid tenantId,
        string playbookKey,
        string? resourceId,
        string title,
        string reason,
        string? parametersJson,
        string requestedBy,
        long? anomalyId = null,
        long? incidentId = null);

    /// <summary>Human approval of a gated action. Transitions PendingApproval → Approved.</summary>
    Task<RemediationAction> ApproveAsync(Guid tenantId, long actionId, string approvedBy);

    /// <summary>Human rejection of a gated action. Transitions PendingApproval → Rejected.</summary>
    Task<RemediationAction> RejectAsync(Guid tenantId, long actionId, string rejectedBy, string? reason);

    /// <summary>Executes a single approved action via the matching provider adapter.</summary>
    Task<RemediationAction> ExecuteAsync(Guid tenantId, long actionId);

    /// <summary>Drains the approved-pending-execution queue (timer worker entry point).</summary>
    Task<int> ExecuteApprovedActionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Uniform surface over a cloud platform: inventory collection and allow-listed
/// remediation execution. One adapter per provider (Azure, AWS, GCP).
/// </summary>
public interface ICloudProviderAdapter
{
    CloudProvider Provider { get; }

    /// <summary>Verifies credentials/reachability for a linked account.</summary>
    Task<(bool Healthy, string? Error)> TestConnectivityAsync(CloudAccount account);

    /// <summary>
    /// Collects the account's resources normalized into the shared inventory model.
    /// Rows are tagged with the provider so they merge into tenant snapshots.
    /// </summary>
    Task<IReadOnlyList<InventoryResource>> CollectInventoryAsync(CloudAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an allow-listed remediation action. Implementations MUST switch on
    /// the playbook's ActionType and reject unknown actions — arbitrary operations
    /// are never permitted.
    /// </summary>
    Task<RemediationExecutionResult> ExecuteRemediationAsync(
        Guid tenantId,
        RemediationPlaybook playbook,
        RemediationAction action,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the adapter for a provider.</summary>
public interface ICloudProviderAdapterFactory
{
    ICloudProviderAdapter GetAdapter(CloudProvider provider);
}

/// <summary>
/// Orchestrates AWS/GCP inventory collection: resources are merged into the
/// tenant's latest snapshot so the inventory views show all clouds together.
/// </summary>
public interface IMultiCloudInventoryService
{
    /// <summary>Syncs one linked account. Returns the number of resources collected.</summary>
    Task<int> SyncAccountAsync(CloudAccount account, CancellationToken cancellationToken = default);

    /// <summary>Syncs every connected non-Azure account across all tenants.</summary>
    Task SyncAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a remediation execution attempt.</summary>
public sealed class RemediationExecutionResult
{
    public bool Succeeded { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }

    public static RemediationExecutionResult Ok(string? resultJson = null) =>
        new() { Succeeded = true, ResultJson = resultJson };

    public static RemediationExecutionResult Fail(string error) =>
        new() { Succeeded = false, ErrorMessage = error };
}
