using CloudRavel.Core.Models;

namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Repository for anomaly records and metric baselines.
/// </summary>
public interface IAnomalyRepository
{
    /// <summary>
    /// Insert a new anomaly, or refresh last_seen/observed values when the fingerprint
    /// is already open. Returns the row id and whether a new anomaly was opened.
    /// </summary>
    Task<(long Id, bool Created)> UpsertAnomalyAsync(Anomaly anomaly);
    Task<Anomaly?> GetByIdAsync(Guid tenantId, long anomalyId);
    Task<IReadOnlyList<Anomaly>> GetAnomaliesAsync(
        Guid tenantId,
        AnomalyStatus? status = null,
        AnomalySeverity? severity = null,
        AnomalyKind? kind = null,
        int offset = 0,
        int limit = 100);
    Task<int> GetOpenCountAsync(Guid tenantId);
    Task UpdateStatusAsync(Guid tenantId, long anomalyId, AnomalyStatus status, string? actor);
    Task LinkToIncidentAsync(Guid tenantId, long anomalyId, long incidentId);
    /// <summary>Auto-resolve open anomalies of a kind whose condition no longer holds.</summary>
    Task ResolveByFingerprintAsync(Guid tenantId, string fingerprint);

    Task<MetricBaseline?> GetBaselineAsync(Guid tenantId, string metricKey);
    Task UpsertBaselineAsync(MetricBaseline baseline);
}

/// <summary>
/// Repository for incidents and their timeline events.
/// </summary>
public interface IIncidentRepository
{
    Task<long> CreateAsync(Incident incident);
    Task<Incident?> GetByIdAsync(Guid tenantId, long incidentId);
    Task<IReadOnlyList<Incident>> GetIncidentsAsync(
        Guid tenantId,
        IncidentStatus? status = null,
        AnomalySeverity? severity = null,
        int offset = 0,
        int limit = 100);
    Task<int> GetOpenCountAsync(Guid tenantId);
    Task UpdateStatusAsync(Guid tenantId, long incidentId, IncidentStatus status, string? actor);
    Task UpdateAssignmentAsync(Guid tenantId, long incidentId, string? assignedTo);
    Task<Incident?> FindOpenIncidentForFingerprintAsync(Guid tenantId, string fingerprint);
    Task AddEventAsync(IncidentEvent evt);
    Task<IReadOnlyList<IncidentEvent>> GetEventsAsync(Guid tenantId, long incidentId, int limit = 200);
}

/// <summary>
/// Repository for the remediation playbook catalog and remediation actions.
/// </summary>
public interface IRemediationRepository
{
    Task<IReadOnlyList<RemediationPlaybook>> GetPlaybooksAsync(CloudProvider? provider = null, bool enabledOnly = true);
    Task<RemediationPlaybook?> GetPlaybookAsync(string playbookKey);

    Task<long> CreateActionAsync(RemediationAction action);
    Task<RemediationAction?> GetActionByIdAsync(Guid tenantId, long actionId);
    Task<IReadOnlyList<RemediationAction>> GetActionsAsync(
        Guid tenantId,
        RemediationStatus? status = null,
        int offset = 0,
        int limit = 100);
    Task<int> GetPendingApprovalCountAsync(Guid tenantId);
    /// <summary>Actions approved (manually or automatically) but not yet executed.</summary>
    Task<IReadOnlyList<RemediationAction>> GetApprovedPendingExecutionAsync(int limit = 50);
    /// <summary>True if an equivalent non-terminal action already exists (dedup guard).</summary>
    Task<bool> HasOpenActionAsync(Guid tenantId, string playbookKey, string? resourceId);
    Task UpdateStatusAsync(Guid tenantId, long actionId, RemediationStatus status,
        string? actor = null, string? rejectedReason = null);
    Task MarkExecutionStartedAsync(long actionId);
    Task MarkExecutionCompletedAsync(long actionId, bool succeeded, string? resultJson, string? errorMessage);
    Task ExpireStaleActionsAsync(DateTime cutoffUtc);
}

/// <summary>
/// Repository for Organizations — the in-app workspace (org_id = RLS boundary)
/// that owns Azure/AWS/GCP clouds as peers.
/// </summary>
public interface IOrganizationRepository
{
    Task<IReadOnlyList<Organization>> GetAllAsync();
    Task<Organization?> GetByIdAsync(Guid orgId);
    Task<Organization> CreateAsync(Organization org);
    /// <summary>
    /// Soft-delete: status → suspended. Active cloud connections should be
    /// removed by the caller first. Workspace shell rows (tenants) stay for FK safety.
    /// </summary>
    Task SoftDeleteAsync(Guid orgId);
}

/// <summary>Repository for per-organization SSO settings.</summary>
public interface IOrgSsoRepository
{
    Task<OrgSsoSettings?> GetAsync(Guid orgId);
    Task UpsertAsync(OrgSsoSettings settings, string updatedBy);
}

/// <summary>
/// Repository for cloud organizations (top-level provider-agnostic grouping:
/// Azure tenant / AWS Organization / GCP Organization).
/// </summary>
public interface ICloudOrgRepository
{
    Task<CloudOrg> CreateAsync(CloudOrg org);
    Task<CloudOrg?> GetByIdAsync(Guid tenantId, Guid orgId);
    Task<IReadOnlyList<CloudOrg>> GetByTenantAsync(Guid tenantId);
    Task UpdateStatusAsync(Guid tenantId, Guid orgId, CloudOrgStatus status);
    /// <summary>
    /// Hard-delete a cloud connection and its children (azure_org_subscriptions for Azure;
    /// member accounts must be deleted first by the caller).
    /// </summary>
    Task DeleteAsync(Guid tenantId, Guid orgId);
    /// <summary>Point the org at a (new or rotated) secret-store credential name.</summary>
    Task UpdateCredentialSecretNameAsync(Guid tenantId, Guid orgId, string? credentialSecretName);

    /// <summary>Pin subscriptions to an Azure connection (subscription_scope='specific'). No-op if empty.</summary>
    Task AddAzureSubscriptionsAsync(Guid tenantId, Guid orgId, IReadOnlyList<string> subscriptionIds);
    Task<IReadOnlyList<AzureOrgSubscription>> GetAzureSubscriptionsAsync(Guid tenantId, Guid orgId);
}

/// <summary>
/// Repository for linked multi-cloud accounts (AWS accounts, GCP projects),
/// each belonging to a <see cref="CloudOrg"/>.
/// </summary>
public interface ICloudAccountRepository
{
    Task<CloudAccount> CreateAsync(CloudAccount account);
    Task<CloudAccount?> GetByIdAsync(Guid tenantId, Guid accountId);
    Task<IReadOnlyList<CloudAccount>> GetByTenantAsync(Guid tenantId);
    Task<IReadOnlyList<CloudAccount>> GetByOrgAsync(Guid tenantId, Guid orgId);
    Task<IReadOnlyList<CloudAccount>> GetAllActiveAsync();
    Task UpdateStatusAsync(Guid accountId, CloudAccountStatus status, string? lastError);
    Task TouchInventoryAsync(Guid accountId, DateTime inventoryAt);
    /// <summary>Hard-delete an AWS account / GCP project link. Caller cleans up the secret.</summary>
    Task DeleteAsync(Guid tenantId, Guid accountId);
    /// <summary>Point the account at a (new or rotated) secret-store credential name.</summary>
    Task UpdateCredentialSecretNameAsync(Guid tenantId, Guid accountId, string? credentialSecretName);
}
