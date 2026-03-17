using System.Text.Json;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using TenantResource = Azure.ResourceManager.Resources.TenantResource;

namespace AzureInventoryMonitor.Infrastructure.Azure;

public sealed class InventoryCollectionService : IInventoryCollectionService
{
    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly ILogger<InventoryCollectionService> _logger;

    public InventoryCollectionService(
        IAzureCredentialFactory credentialFactory,
        IInventoryRepository inventoryRepo,
        ILogger<InventoryCollectionService> logger)
    {
        _credentialFactory = credentialFactory;
        _inventoryRepo = inventoryRepo;
        _logger = logger;
    }

    public async Task<(long SnapshotId, int ResourceCount)> CollectInventoryAsync(Guid tenantId, string triggeredBy)
    {
        var snapshot = await _inventoryRepo.CreateSnapshotAsync(tenantId, triggeredBy);
        var snapshotId = snapshot.SnapshotId;

        try
        {
            var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
            var armClient = new ArmClient(credential);
            var tenantResource = armClient.GetTenants().GetAll().First();

            var allResources = new List<InventoryResource>();

            // 1. Collect resources
            await QueryResourceGraphAsync(tenantResource, @"
                Resources
                | project id, name, type, location, resourceGroup, subscriptionId,
                          sku, tags, identity, properties
                | order by type asc, name asc",
                el =>
                {
                    var resource = ParseResourceGraphResult(tenantId, snapshotId, el);
                    if (resource != null) allResources.Add(resource);
                });

            _logger.LogInformation("Collected {Count} resources from Resource Graph for tenant {TenantId}",
                allResources.Count, tenantId);

            // 2. Collect subscriptions from resourcecontainers
            var subscriptions = new List<InventoryResource>();
            await QueryResourceGraphAsync(tenantResource, @"
                resourcecontainers
                | where type == 'microsoft.resources/subscriptions'
                | project id, name, type, subscriptionId, tags, properties",
                el =>
                {
                    var sub = ParseSubscriptionResult(tenantId, snapshotId, el);
                    if (sub != null) subscriptions.Add(sub);
                });

            _logger.LogInformation("Collected {Count} subscriptions for tenant {TenantId}",
                subscriptions.Count, tenantId);

            // 3. Enrich subscriptions with secure scores
            var secureScores = new Dictionary<string, JsonElement>();
            try
            {
                await QueryResourceGraphAsync(tenantResource, @"
                    securityresources
                    | where type == 'microsoft.security/securescores'
                    | where properties.displayName == 'ASC score'
                    | project subscriptionId, properties",
                    el =>
                    {
                        if (el.TryGetProperty("subscriptionId", out var subIdProp))
                        {
                            var subId = subIdProp.GetString();
                            if (!string.IsNullOrEmpty(subId) && el.TryGetProperty("properties", out var props))
                                secureScores[subId] = props.Clone();
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Secure score query failed for tenant {TenantId} — Defender may not be enabled", tenantId);
            }

            // Merge secure scores into subscription properties
            foreach (var sub in subscriptions)
            {
                if (secureScores.TryGetValue(sub.SubscriptionId, out var scoreProps))
                {
                    var propsDict = string.IsNullOrEmpty(sub.PropertiesJson)
                        ? new Dictionary<string, object>()
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(sub.PropertiesJson) ?? new();

                    if (scoreProps.TryGetProperty("score", out var scoreProp))
                    {
                        if (scoreProp.TryGetProperty("current", out var cur))
                            propsDict["secureScoreCurrent"] = cur.GetDouble();
                        if (scoreProp.TryGetProperty("max", out var max))
                            propsDict["secureScoreMax"] = max.GetDouble();
                        if (scoreProp.TryGetProperty("percentage", out var pct))
                            propsDict["secureScorePercentage"] = pct.GetDouble();
                    }

                    sub.PropertiesJson = JsonSerializer.Serialize(propsDict);
                }
            }

            allResources.AddRange(subscriptions);

            await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, allResources);
            await _inventoryRepo.CompleteSnapshotAsync(snapshotId, allResources.Count, "inline-resource-graph");
            await _inventoryRepo.SetLatestSnapshotAsync(tenantId, snapshotId);

            _logger.LogInformation("Snapshot {SnapshotId} completed with {Count} resources for tenant {TenantId} (triggered by {TriggeredBy})",
                snapshotId, allResources.Count, tenantId, triggeredBy);

            return (snapshotId, allResources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot {SnapshotId} failed for tenant {TenantId}", snapshotId, tenantId);
            await _inventoryRepo.FailSnapshotAsync(snapshotId, ex.Message);
            throw;
        }
    }

    private static InventoryResource? ParseResourceGraphResult(Guid tenantId, long snapshotId, JsonElement el)
    {
        var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        var name = el.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        var type = el.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
        var location = el.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
        var rg = el.TryGetProperty("resourceGroup", out var rgProp) ? rgProp.GetString() ?? "" : "";
        var subId = el.TryGetProperty("subscriptionId", out var subProp) ? subProp.GetString() ?? "" : "";

        string? skuName = null, skuTier = null;
        if (el.TryGetProperty("sku", out var skuProp) && skuProp.ValueKind == JsonValueKind.Object)
        {
            skuName = skuProp.TryGetProperty("name", out var sn) ? sn.GetString() : null;
            skuTier = skuProp.TryGetProperty("tier", out var st) ? st.GetString() : null;
        }

        Dictionary<string, string>? tags = null;
        if (el.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Object)
        {
            tags = new Dictionary<string, string>();
            foreach (var kv in tagsProp.EnumerateObject())
                tags[kv.Name] = kv.Value.GetString() ?? "";
        }

        string? identityType = null;
        if (el.TryGetProperty("identity", out var idtyProp) && idtyProp.ValueKind == JsonValueKind.Object)
        {
            identityType = idtyProp.TryGetProperty("type", out var itProp) ? itProp.GetString() : null;
        }

        string? propsJson = null;
        if (el.TryGetProperty("properties", out var propsProp) && propsProp.ValueKind == JsonValueKind.Object)
        {
            propsJson = propsProp.GetRawText();
        }

        return new InventoryResource
        {
            TenantId = tenantId,
            SnapshotId = snapshotId,
            ResourceId = id,
            SubscriptionId = subId,
            ResourceGroup = rg,
            ResourceType = type,
            ResourceName = name,
            Location = location,
            SkuName = skuName,
            SkuTier = skuTier,
            Tags = tags,
            IdentityType = identityType,
            PropertiesJson = propsJson
        };
    }

    private static InventoryResource? ParseSubscriptionResult(Guid tenantId, long snapshotId, JsonElement el)
    {
        var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        var name = el.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        var subId = el.TryGetProperty("subscriptionId", out var subProp) ? subProp.GetString() ?? "" : "";

        Dictionary<string, string>? tags = null;
        if (el.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Object)
        {
            tags = new Dictionary<string, string>();
            foreach (var kv in tagsProp.EnumerateObject())
                tags[kv.Name] = kv.Value.GetString() ?? "";
        }

        // Build enriched properties from subscription metadata
        var propsDict = new Dictionary<string, object>();
        if (el.TryGetProperty("properties", out var propsProp) && propsProp.ValueKind == JsonValueKind.Object)
        {
            if (propsProp.TryGetProperty("state", out var stateProp))
                propsDict["state"] = stateProp.GetString() ?? "";
            if (propsProp.TryGetProperty("tenantId", out var tidProp))
                propsDict["homeTenantId"] = tidProp.GetString() ?? "";
            if (propsProp.TryGetProperty("authorizationSource", out var authProp))
                propsDict["authorizationSource"] = authProp.GetString() ?? "";

            // Extract management group chain
            if (propsProp.TryGetProperty("managementGroupAncestorsChain", out var mgChain)
                && mgChain.ValueKind == JsonValueKind.Array && mgChain.GetArrayLength() > 0)
            {
                var firstMg = mgChain[0];
                if (firstMg.TryGetProperty("displayName", out var mgName))
                    propsDict["managementGroupName"] = mgName.GetString() ?? "";
                if (firstMg.TryGetProperty("name", out var mgId))
                    propsDict["managementGroupId"] = mgId.GetString() ?? "";
            }
        }

        return new InventoryResource
        {
            TenantId = tenantId,
            SnapshotId = snapshotId,
            ResourceId = id,
            SubscriptionId = subId,
            ResourceGroup = "",
            ResourceType = "microsoft.resources/subscriptions",
            ResourceName = name,
            Location = "global",
            Tags = tags,
            PropertiesJson = propsDict.Count > 0 ? JsonSerializer.Serialize(propsDict) : null
        };
    }

    private static async Task QueryResourceGraphAsync(
        TenantResource tenantResource,
        string query,
        Action<JsonElement> processElement)
    {
        string? skipToken = null;
        do
        {
            var request = new ResourceQueryContent(query)
            {
                Options = new ResourceQueryRequestOptions
                {
                    ResultFormat = ResultFormat.ObjectArray,
                    Top = 1000
                }
            };
            if (skipToken != null)
                request.Options.SkipToken = skipToken;

            var result = await tenantResource.GetResourcesAsync(request);
            skipToken = result.Value.SkipToken;

            using var doc = JsonDocument.Parse(result.Value.Data);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    processElement(el);
            }
        } while (!string.IsNullOrEmpty(skipToken));
    }
}
