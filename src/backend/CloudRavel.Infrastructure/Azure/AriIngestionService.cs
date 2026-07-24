using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Azure;

/// <summary>
/// Ingests raw ARI (Azure Resource Inventory) output from Blob Storage.
/// 
/// Pipeline:
///   1. Downloads ARI JSON from the snapshot blob path
///   2. Parses and normalizes resources into the unified schema
///   3. Creates a snapshot record, bulk inserts resources
///   4. Updates the latest_snapshot pointer on success
///   5. Marks the snapshot as failed on error
/// 
/// ARI output format: JSON array of ARM resource objects with extended properties.
/// </summary>
public sealed class AriIngestionService : IAriIngestionService
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<AriIngestionService> _logger;
    private readonly string _containerName;

    public AriIngestionService(
        IInventoryRepository inventoryRepo,
        IConfiguration configuration,
        ILogger<AriIngestionService> logger)
    {
        _inventoryRepo = inventoryRepo;
        _logger = logger;

        var blobStorageUrl = configuration["BlobStorageUrl"]
            ?? throw new InvalidOperationException("BlobStorageUrl is required.");
        _blobClient = new BlobServiceClient(new Uri(blobStorageUrl), new DefaultAzureCredential());
        _containerName = configuration["Storage:SnapshotContainer"] ?? "ari-snapshots";
    }

    public async Task<long> IngestSnapshotAsync(Guid tenantId, string blobPath, string triggeredBy)
    {
        _logger.LogInformation("Starting ARI ingestion for tenant {TenantId} from {BlobPath}", tenantId, blobPath);

        // 1. Create snapshot record
        var snapshot = await _inventoryRepo.CreateSnapshotAsync(tenantId, triggeredBy);
        var snapshotId = snapshot.SnapshotId;

        try
        {
            // 2. Download ARI output from Blob Storage
            var container = _blobClient.GetBlobContainerClient(_containerName);
            var blobClient = container.GetBlobClient(blobPath);

            if (!await blobClient.ExistsAsync())
            {
                throw new FileNotFoundException($"ARI output blob not found: {blobPath}");
            }

            var downloadResult = await blobClient.DownloadContentAsync();
            var content = downloadResult.Value.Content.ToString();

            // 3. Parse ARI JSON output
            var ariResources = JsonSerializer.Deserialize<List<AriResourceEntry>>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("ARI output is empty or invalid JSON.");

            _logger.LogInformation("Parsed {Count} resources from ARI output for tenant {TenantId}",
                ariResources.Count, tenantId);

            // 4. Normalize into inventory resources
            var resources = ariResources.Select(ari => NormalizeResource(tenantId, snapshotId, ari)).ToList();

            // 5. Bulk insert
            await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, resources);

            // 6. Complete snapshot and update pointer
            await _inventoryRepo.CompleteSnapshotAsync(snapshotId, resources.Count, blobPath);
            await _inventoryRepo.SetLatestSnapshotAsync(tenantId, snapshotId);

            _logger.LogInformation("ARI ingestion completed: snapshot {SnapshotId}, {Count} resources for tenant {TenantId}",
                snapshotId, resources.Count, tenantId);

            return snapshotId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARI ingestion failed for tenant {TenantId}, snapshot {SnapshotId}", tenantId, snapshotId);
            await _inventoryRepo.FailSnapshotAsync(snapshotId, ex.Message);
            throw;
        }
    }

    private static InventoryResource NormalizeResource(Guid tenantId, long snapshotId, AriResourceEntry ari)
    {
        // Extract subscription ID from ARM resource ID
        // Format: /subscriptions/{subId}/resourceGroups/{rg}/providers/{type}/{name}
        var subscriptionId = ExtractFromResourceId(ari.Id, "subscriptions");
        var resourceGroup = ExtractFromResourceId(ari.Id, "resourceGroups");

        // Extract identity information
        string? identityType = null;
        List<string>? principalIds = null;
        if (ari.Identity != null)
        {
            identityType = ari.Identity.Type;
            if (!string.IsNullOrEmpty(ari.Identity.PrincipalId))
            {
                principalIds = [ari.Identity.PrincipalId];
            }
            if (ari.Identity.UserAssignedIdentities?.Count > 0)
            {
                principalIds ??= [];
                principalIds.AddRange(ari.Identity.UserAssignedIdentities.Keys);
            }
        }

        // Extract networking config
        string? networkingJson = null;
        if (ari.Properties != null)
        {
            var networkingProps = ExtractNetworkingProperties(ari.Properties);
            if (networkingProps.Count > 0)
                networkingJson = JsonSerializer.Serialize(networkingProps);
        }

        // Extract security config
        string? securityConfigJson = null;
        if (ari.Properties != null)
        {
            var securityProps = ExtractSecurityProperties(ari.Properties);
            if (securityProps.Count > 0)
                securityConfigJson = JsonSerializer.Serialize(securityProps);
        }

        return new InventoryResource
        {
            TenantId = tenantId,
            SnapshotId = snapshotId,
            ResourceId = ari.Id ?? string.Empty,
            SubscriptionId = subscriptionId ?? string.Empty,
            ResourceGroup = resourceGroup ?? string.Empty,
            ResourceType = ari.Type ?? string.Empty,
            ResourceName = ari.Name ?? string.Empty,
            Location = ari.Location ?? string.Empty,
            SkuName = ari.Sku?.Name,
            SkuTier = ari.Sku?.Tier,
            SkuCapacity = ari.Sku?.Capacity,
            Tags = ari.Tags,
            IdentityType = identityType,
            IdentityPrincipalIds = principalIds,
            PropertiesJson = ari.Properties != null ? JsonSerializer.Serialize(ari.Properties) : null,
            NetworkingJson = networkingJson,
            SecurityConfigJson = securityConfigJson
        };
    }

    private static string? ExtractFromResourceId(string? resourceId, string segment)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;
        var parts = resourceId.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals(segment, StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        return null;
    }

    private static readonly HashSet<string> NetworkingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipAddress", "privateIPAddress", "publicIPAddress", "ipConfigurations",
        "networkInterfaces", "subnets", "virtualNetwork", "networkSecurityGroup",
        "frontendIPConfigurations", "backendAddressPools", "privateEndpointConnections",
        "dnsSettings", "natRules", "loadBalancingRules"
    };

    private static readonly HashSet<string> SecurityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "httpsOnly", "minTlsVersion", "publicNetworkAccess", "networkAcls",
        "encryptionSettings", "encryption", "sslEnforcement", "firewallRules",
        "supportsHttpsTrafficOnly", "enableRbacAuthorization", "softDeleteRetentionInDays",
        "enablePurgeProtection", "disableLocalAuth", "authSettings"
    };

    private static Dictionary<string, JsonElement> ExtractNetworkingProperties(Dictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in properties)
        {
            if (NetworkingKeys.Contains(key))
                result[key] = value;
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ExtractSecurityProperties(Dictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in properties)
        {
            if (SecurityKeys.Contains(key))
                result[key] = value;
        }
        return result;
    }
}

// ============================================================================
// ARI Output Schema Types
// ============================================================================

/// <summary>
/// Represents a single resource entry from ARI output.
/// Maps to the ARM resource JSON format.
/// </summary>
internal sealed class AriResourceEntry
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Location { get; set; }
    public AriSku? Sku { get; set; }
    public AriIdentity? Identity { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

internal sealed class AriSku
{
    public string? Name { get; set; }
    public string? Tier { get; set; }
    public int? Capacity { get; set; }
}

internal sealed class AriIdentity
{
    public string? Type { get; set; }
    public string? PrincipalId { get; set; }
    public string? TenantId { get; set; }
    public Dictionary<string, object>? UserAssignedIdentities { get; set; }
}
