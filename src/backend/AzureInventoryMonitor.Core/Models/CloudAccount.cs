namespace AzureInventoryMonitor.Core.Models;

/// <summary>
/// The cloud platform a resource or account belongs to.
/// Azure remains the first-class provider; AWS and GCP accounts are attached
/// to a tenant as additional inventory + remediation scopes.
/// </summary>
public enum CloudProvider
{
    Azure,
    Aws,
    Gcp
}

/// <summary>
/// A provider-agnostic cloud organization — the top-level grouping and a peer
/// across providers: an Azure tenant, an AWS Organization, or a GCP Organization.
/// AWS accounts and GCP projects belong to one of these, NOT to an Azure tenant.
/// tenant_id here is only the workspace / RLS boundary (the enterprise).
/// </summary>
public sealed class CloudOrg
{
    public Guid OrgId { get; set; }
    public Guid TenantId { get; set; }
    public CloudProvider Provider { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Azure tenant GUID / AWS Organization ID / GCP Organization ID (optional).</summary>
    public string? ExternalId { get; set; }
    public CloudAccountStatus Status { get; set; } = CloudAccountStatus.Connected;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// A cloud account/project belonging to a <see cref="CloudOrg"/>: an AWS member
/// account or a GCP project, linked explicitly with credentials held in the
/// secret store. Grouped under its org; the workspace (tenant_id) is inherited
/// from the org for RLS.
/// </summary>
public sealed class CloudAccount
{
    public Guid AccountId { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Parent organization this account/project belongs to.</summary>
    public Guid OrgId { get; set; }
    public CloudProvider Provider { get; set; }

    /// <summary>AWS account ID, GCP project ID, or Azure subscription/tenant ID.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public CloudAccountStatus Status { get; set; } = CloudAccountStatus.Connected;

    /// <summary>
    /// Key Vault secret holding the credential material:
    ///   AWS: { "accessKeyId", "secretAccessKey", "defaultRegion" }
    ///   GCP: service account key JSON
    ///   Azure: unused (credentials resolved via IAzureCredentialFactory)
    /// </summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Regions (AWS) or zones (GCP) to scan; JSON array. Null = provider default.</summary>
    public List<string>? Regions { get; set; }

    public DateTime? LastInventoryAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public enum CloudAccountStatus
{
    Connected,
    Degraded,
    Disconnected
}
