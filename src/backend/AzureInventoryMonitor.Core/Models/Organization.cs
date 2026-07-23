namespace AzureInventoryMonitor.Core.Models;

/// <summary>
/// An Organization — the in-app workspace an operator selects and adds clouds to.
///
/// org_id is the workspace / RLS boundary value shared by every tenant_id column,
/// so an Organization owns, as peers:
///   * an Azure tenant  (the tenants row whose tenant_id = org_id, + subscriptions)
///   * AWS Organizations (cloud_orgs, provider Aws)  and their member accounts
///   * GCP Organizations (cloud_orgs, provider Gcp)  and their projects
///
/// Adding a cloud never creates an Organization — clouds are always attached to
/// an existing one.
/// </summary>
public sealed class Organization
{
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Development | Production. Labels the workspace; the running instance's
    /// Platform:Environment setting is what actually gates inventory collection.
    /// </summary>
    public string Environment { get; set; } = "Development";

    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "system";
}
