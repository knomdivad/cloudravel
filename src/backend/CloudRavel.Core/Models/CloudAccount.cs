namespace CloudRavel.Core.Models;

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
/// Infers the cloud provider from a resource id / finding id / inventory provider hint.
/// Used so AIOps anomalies and UI badges are not stuck on the Azure default for AWS/GCP estates.
/// </summary>
public static class CloudProviderInference
{
    public static CloudProvider FromResource(string? resourceId, string? providerHint = null)
    {
        if (!string.IsNullOrWhiteSpace(providerHint))
        {
            var hint = providerHint.Trim();
            if (Enum.TryParse<CloudProvider>(hint, ignoreCase: true, out var parsed))
                return parsed;
            if (hint.Equals("google", StringComparison.OrdinalIgnoreCase)
                || hint.Equals("googlecloud", StringComparison.OrdinalIgnoreCase))
                return CloudProvider.Gcp;
        }

        if (string.IsNullOrWhiteSpace(resourceId))
            return CloudProvider.Azure;

        var id = resourceId.Trim();

        // Stable finding / recommendation ids we mint for multi-cloud governance
        if (id.StartsWith("gcp-", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("gcp:", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Gcp;
        if (id.StartsWith("aws-", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("arn:aws:", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Aws;

        // GCP Cloud Asset / SCC resource names: //compute.googleapis.com/projects/...
        if (id.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase)
            || id.Contains("//cloudresourcemanager.googleapis.com/", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("projects/", StringComparison.OrdinalIgnoreCase)
               && (id.Contains("/instances/", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("/buckets/", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("/zones/", StringComparison.OrdinalIgnoreCase)))
            return CloudProvider.Gcp;

        // Azure ARM ids
        if (id.Contains("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("/subscriptions", StringComparison.OrdinalIgnoreCase)
            || id.Contains("providers/Microsoft.", StringComparison.OrdinalIgnoreCase))
            return CloudProvider.Azure;

        return CloudProvider.Azure;
    }

    /// <summary>Majority vote across resource ids (ties prefer first non-default signal).</summary>
    public static CloudProvider FromResources(IEnumerable<string?> resourceIds)
    {
        var counts = new Dictionary<CloudProvider, int>();
        foreach (var id in resourceIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var p = FromResource(id);
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        if (counts.Count == 0) return CloudProvider.Azure;
        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }
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
    public CloudOrgStatus Status { get; set; } = CloudOrgStatus.Active;
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    // --- Azure-only connection fields (null/unused for Aws/Gcp) ---
    // A workspace can hold N Azure tenant connections as peers, each with its
    // own onboarding method, credentials, and subscription scope — the same
    // pattern AWS/GCP already use for their member accounts/projects.

    /// <summary>"lighthouse" | "app_registration". Azure connections only.</summary>
    public string? OnboardingMethod { get; set; }
    /// <summary>OpenBao secret name holding {clientId, clientSecret} for app_registration.</summary>
    public string? CredentialSecretName { get; set; }
    public string? LighthouseDelegationId { get; set; }
    /// <summary>"all" | "specific". Azure connections only; AWS/GCP always require explicit accounts.</summary>
    public string SubscriptionScope { get; set; } = "all";
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

/// <summary>
/// Status vocabulary for a cloud_orgs row (Azure connection / AWS org / GCP org) —
/// distinct from <see cref="CloudAccountStatus"/> because the cloud_orgs.status
/// CHECK constraint uses Active/Degraded/Disconnected, not Connected/Degraded/Disconnected.
/// </summary>
public enum CloudOrgStatus
{
    Active,
    Degraded,
    Disconnected
}

/// <summary>
/// A subscription pinned to an Azure cloud_orgs connection whose
/// SubscriptionScope is "specific" — scopes the Resource Graph query to just
/// these subscriptions instead of everything the connection's credential sees.
/// </summary>
public sealed class AzureOrgSubscription
{
    public Guid OrgId { get; set; }
    public Guid TenantId { get; set; }
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
