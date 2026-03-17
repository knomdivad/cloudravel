using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Azure;

/// <summary>
/// Resolves Azure credentials for each customer tenant.
/// 
/// For Lighthouse tenants:
///   Uses DefaultAzureCredential (Managed Identity of the Functions app).
///   Lighthouse delegations automatically grant cross-tenant access.
///   No secrets stored.
/// 
/// For App Registration tenants:
///   Retrieves client credentials from Azure Key Vault.
///   Constructs a ClientSecretCredential for the customer tenant.
/// </summary>
public sealed class AzureCredentialFactory : IAzureCredentialFactory
{
    private readonly ITenantRepository _tenantRepo;
    private readonly SecretClient _secretClient;
    private readonly DefaultAzureCredential _defaultCredential;
    private readonly ILogger<AzureCredentialFactory> _logger;

    public AzureCredentialFactory(
        ITenantRepository tenantRepo,
        IConfiguration configuration,
        ILogger<AzureCredentialFactory> logger)
    {
        _tenantRepo = tenantRepo;
        _logger = logger;
        _defaultCredential = new DefaultAzureCredential();

        var vaultUri = configuration["KeyVault:VaultUri"]
            ?? configuration["KeyVaultUrl"]
            ?? throw new InvalidOperationException("KeyVault:VaultUri is required.");
        _secretClient = new SecretClient(new Uri(vaultUri), _defaultCredential);
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
        if (string.IsNullOrEmpty(tenant.KeyVaultSecretName))
        {
            throw new InvalidOperationException(
                $"Tenant {tenant.TenantId} uses App Registration but has no Key Vault secret configured.");
        }

        _logger.LogDebug("Retrieving App Registration credentials from Key Vault for tenant {TenantId}",
            tenant.TenantId);

        // Secret is stored as JSON: { "clientId": "...", "clientSecret": "..." }
        var secret = await _secretClient.GetSecretAsync(tenant.KeyVaultSecretName);
        var credentials = System.Text.Json.JsonSerializer.Deserialize<AppRegistrationCredentials>(
            secret.Value.Value,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize credentials for tenant {tenant.TenantId}.");

        return new ClientSecretCredential(
            tenant.AzureTenantId,
            credentials.ClientId,
            credentials.ClientSecret);
    }

    private sealed class AppRegistrationCredentials
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }
}
