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
/// A cloud account attached to a tenant: an Azure subscription set is implicit
/// via the tenant's Lighthouse/App Registration onboarding, while AWS accounts
/// and GCP projects are linked explicitly with credentials held in Key Vault.
/// </summary>
public sealed class CloudAccount
{
    public Guid AccountId { get; set; }
    public Guid TenantId { get; set; }
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
