namespace AzureInventoryMonitor.Core.Models;

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
    public string? KeyVaultSecretName { get; set; }
    public string? LighthouseDelegationId { get; set; }
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
