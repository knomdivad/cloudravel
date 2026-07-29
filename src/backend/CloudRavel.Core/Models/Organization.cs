namespace CloudRavel.Core.Models;

/// <summary>
/// Placeholder Azure tenant id on the workspace <c>tenants</c> shell created with an
/// Organization before any cloud is connected. Replaced when Azure is linked.
/// </summary>
public static class WorkspaceTenantPlaceholders
{
    public const string AzureTenantId = "00000000-0000-0000-0000-000000000000";
}

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

/// <summary>
/// Per-organization SSO configuration. Stored and manageable now; per-org token
/// federation (multi-issuer validation, user→org mapping on first login) is a
/// documented follow-up. The IdP client secret, if any, lives in the secret
/// store — only its name is kept here.
/// </summary>
public sealed class OrgSsoSettings
{
    public Guid OrgId { get; set; }
    public string Provider { get; set; } = "none"; // none | entra | oidc
    public string? IdpTenantId { get; set; }
    public string? IdpClientId { get; set; }
    public string? Domain { get; set; }
    public string? ClientSecretName { get; set; }
    public bool Enabled { get; set; }
}
