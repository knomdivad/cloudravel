using Azure.Core;
using Azure.Identity;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Azure;

/// <summary>
/// Resolves Azure credentials for each customer tenant.
///
/// For Lighthouse tenants:
///   Uses DefaultAzureCredential (Managed Identity of the Functions app).
///   Lighthouse delegations automatically grant cross-tenant access.
///   No secrets stored.
///
/// For App Registration tenants:
///   Retrieves client credentials from the configured secret store (OpenBao).
///   Constructs a ClientSecretCredential for the customer tenant.
/// </summary>
public sealed class AzureCredentialFactory : IAzureCredentialFactory
{
    private readonly ITenantRepository _tenantRepo;
    private readonly ISecretStore? _secretStore;
    private readonly DefaultAzureCredential _defaultCredential;
    private readonly ILogger<AzureCredentialFactory> _logger;

    public AzureCredentialFactory(
        ITenantRepository tenantRepo,
        ILogger<AzureCredentialFactory> logger,
        ISecretStore? secretStore = null)
    {
        _tenantRepo = tenantRepo;
        _logger = logger;
        _defaultCredential = new DefaultAzureCredential();
        _secretStore = secretStore;
    }

    public async Task<TokenCredential> GetCredentialForTenantAsync(Guid tenantId)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        if (tenant.Status != TenantStatus.Active && tenant.Status != TenantStatus.Degraded)
        {
            throw new InvalidOperationException($"Tenant {tenantId} is {tenant.Status} and cannot be accessed.");
        }

        return tenant.OnboardingMethod switch
        {
            OnboardingMethod.Lighthouse => GetLighthouseCredential(tenant),
            OnboardingMethod.AppRegistration => await GetAppRegistrationCredentialAsync(tenant),
            _ => throw new InvalidOperationException($"Unknown onboarding method: {tenant.OnboardingMethod}")
        };
    }

    private TokenCredential GetLighthouseCredential(Tenant tenant)
    {
        // For Lighthouse, the MSP's Managed Identity already has cross-tenant access
        // via the Lighthouse delegation. No additional credential needed.
        _logger.LogDebug("Using Lighthouse delegation for tenant {TenantId} ({AzureTenantId})",
            tenant.TenantId, tenant.AzureTenantId);
        return _defaultCredential;
    }

    private async Task<TokenCredential> GetAppRegistrationCredentialAsync(Tenant tenant)
    {
        _logger.LogDebug("Retrieving App Registration credentials from the secret store for tenant {TenantId}",
            tenant.TenantId);
        return await ResolveAppRegistrationCredentialAsync(
            tenant.AzureTenantId, tenant.SecretName, $"tenant {tenant.TenantId}");
    }

    /// <summary>
    /// Resolves credentials for one Azure tenant CONNECTION (a cloud_orgs row,
    /// provider=Azure) — the basis for a workspace holding multiple Azure tenants
    /// as peers, rather than the single legacy connection on the tenants table.
    /// </summary>
    public async Task<TokenCredential> GetCredentialForAzureOrgAsync(CloudOrg azureOrg)
    {
        if (azureOrg.Provider != CloudProvider.Azure)
            throw new InvalidOperationException($"Cloud org {azureOrg.OrgId} is not an Azure connection.");
        if (azureOrg.Status == CloudOrgStatus.Disconnected)
            throw new InvalidOperationException($"Azure connection {azureOrg.OrgId} is disconnected.");

        return azureOrg.OnboardingMethod?.ToLowerInvariant() switch
        {
            "lighthouse" => GetLighthouseCredential(azureOrg),
            "app_registration" => await ResolveAppRegistrationCredentialAsync(
                azureOrg.ExternalId ?? "", azureOrg.CredentialSecretName, $"Azure org {azureOrg.OrgId}"),
            _ => throw new InvalidOperationException(
                $"Azure org {azureOrg.OrgId} has no onboarding method configured.")
        };
    }

    private TokenCredential GetLighthouseCredential(CloudOrg azureOrg)
    {
        _logger.LogDebug("Using Lighthouse delegation for Azure org {OrgId} ({ExternalId})",
            azureOrg.OrgId, azureOrg.ExternalId);
        return _defaultCredential;
    }

    private async Task<TokenCredential> ResolveAppRegistrationCredentialAsync(
        string azureTenantId, string? secretName, string context)
    {
        if (string.IsNullOrEmpty(secretName))
            throw new InvalidOperationException($"{context} uses App Registration but has no secret configured.");

        if (_secretStore == null)
            throw new InvalidOperationException(
                $"{context} uses App Registration, which requires a secret store (OpenBao:Address), but it is not configured.");

        // Secret is stored as JSON: { "clientId": "...", "clientSecret": "..." }
        var secretValue = await _secretStore.GetSecretAsync(secretName)
            ?? throw new InvalidOperationException($"No secret found for {context} ({secretName}).");
        var credentials = System.Text.Json.JsonSerializer.Deserialize<AppRegistrationCredentials>(
            secretValue,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize credentials for {context}.");

        return new ClientSecretCredential(azureTenantId, credentials.ClientId, credentials.ClientSecret);
    }

    private sealed class AppRegistrationCredentials
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }
}
