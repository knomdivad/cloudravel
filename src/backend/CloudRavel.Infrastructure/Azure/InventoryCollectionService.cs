using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;
using TenantResource = Azure.ResourceManager.Resources.TenantResource;

namespace CloudRavel.Infrastructure.Azure;

/// <summary>
/// Collects Azure inventory for a workspace by looping every Azure tenant
/// CONNECTION (cloud_orgs rows, provider=Azure) attached to it, so an
/// Organization can hold multiple Azure tenants as peers — the same pattern
/// already used for AWS Organizations and GCP Organizations. Falls back to the
/// legacy single-credential (tenants-table) path for a workspace with zero
/// Azure connections, which should only occur pre-migration-009.
/// </summary>
public sealed class InventoryCollectionService : IInventoryCollectionService
{
    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly ICloudOrgRepository _cloudOrgRepo;
    private readonly ILogger<InventoryCollectionService> _logger;

    public InventoryCollectionService(
        IAzureCredentialFactory credentialFactory,
        IInventoryRepository inventoryRepo,
        ICloudOrgRepository cloudOrgRepo,
        ILogger<InventoryCollectionService> logger)
    {
        _credentialFactory = credentialFactory;
        _inventoryRepo = inventoryRepo;
        _cloudOrgRepo = cloudOrgRepo;
        _logger = logger;
    }

    public async Task<(long SnapshotId, int ResourceCount)> CollectInventoryAsync(Guid tenantId, string triggeredBy)
    {
        var snapshot = await _inventoryRepo.CreateSnapshotAsync(tenantId, triggeredBy);
        var snapshotId = snapshot.SnapshotId;

        try
        {
            var azureOrgs = (await _cloudOrgRepo.GetByTenantAsync(tenantId))
                .Where(o => o.Provider == CloudProvider.Azure && o.Status != CloudOrgStatus.Disconnected)
                .ToList();

            var allResources = new List<InventoryResource>();

            if (azureOrgs.Count == 0)
            {
                // Legacy fallback: no cloud_orgs Azure connection exists yet for this
                // workspace (shouldn't occur post-migration-009, but keeps an
                // unmigrated workspace working) — collect via the single tenants-row
                // credential exactly as before. A failure here fails the whole
                // snapshot, matching the pre-multi-tenant behavior.
                var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
                var resources = await CollectFromCredentialAsync(credential, azureTenantId: null,
                    subscriptionIds: null, tenantId, snapshotId);
                allResources.AddRange(resources);
            }
            else
            {
                foreach (var azureOrg in azureOrgs)
                {
                    try
                    {
                        var credential = await _credentialFactory.GetCredentialForAzureOrgAsync(azureOrg);
                        IReadOnlyList<string>? subscriptionIds = null;
                        if (azureOrg.SubscriptionScope == "specific")
                        {
                            var pinned = await _cloudOrgRepo.GetAzureSubscriptionsAsync(tenantId, azureOrg.OrgId);
                            subscriptionIds = pinned.Select(p => p.SubscriptionId).ToList();
                        }

                        var resources = await CollectFromCredentialAsync(
                            credential, azureOrg.ExternalId, subscriptionIds, tenantId, snapshotId);
                        allResources.AddRange(resources);

                        if (azureOrg.Status != CloudOrgStatus.Active)
                            await _cloudOrgRepo.UpdateStatusAsync(tenantId, azureOrg.OrgId, CloudOrgStatus.Active);
                    }
                    catch (Exception ex)
                    {
                        // One bad Azure connection must not sink every other
                        // connection's data — matches the AWS/GCP multi-cloud pattern.
                        _logger.LogError(ex, "Azure connection {OrgId} ({ExternalId}) failed during collection for workspace {TenantId}",
                            azureOrg.OrgId, azureOrg.ExternalId, tenantId);
                        try
                        {
                            await _cloudOrgRepo.UpdateStatusAsync(tenantId, azureOrg.OrgId, CloudOrgStatus.Degraded);
                        }
                        catch (Exception statusEx)
                        {
                            _logger.LogWarning(statusEx, "Failed to mark Azure connection {OrgId} Degraded", azureOrg.OrgId);
                        }
                    }
                }
            }

            await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, allResources);
            await _inventoryRepo.CompleteSnapshotAsync(snapshotId, allResources.Count, "inline-resource-graph");
            await _inventoryRepo.SetLatestSnapshotAsync(tenantId, snapshotId);

            _logger.LogInformation("Snapshot {SnapshotId} completed with {Count} resources across {Connections} Azure connection(s) for tenant {TenantId} (triggered by {TriggeredBy})",
                snapshotId, allResources.Count, Math.Max(azureOrgs.Count, 1), tenantId, triggeredBy);

            return (snapshotId, allResources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot {SnapshotId} failed for tenant {TenantId}", snapshotId, tenantId);
            await _inventoryRepo.FailSnapshotAsync(snapshotId, ex.Message);
            throw;
        }
    }

    /// <summary>Runs the full Resource Graph collection (resources + subscriptions + secure scores) for one credential/connection.</summary>
    private async Task<List<InventoryResource>> CollectFromCredentialAsync(
        TokenCredential credential, string? azureTenantId, IReadOnlyList<string>? subscriptionIds,
        Guid tenantId, long snapshotId)
    {
        var armClient = new ArmClient(credential);
        var tenantResource = ResolveTenantResource(armClient, azureTenantId);
        var resources = new List<InventoryResource>();

        // 1. Collect resources
        await QueryResourceGraphAsync(tenantResource, subscriptionIds, @"
            Resources
            | project id, name, type, location, resourceGroup, subscriptionId,
                      sku, tags, identity, properties
            | order by type asc, name asc",
            el =>
            {
                var resource = ParseResourceGraphResult(tenantId, snapshotId, el);
                if (resource != null) resources.Add(resource);
            });

        // 2. Collect subscriptions from resourcecontainers
        var subscriptions = new List<InventoryResource>();
        await QueryResourceGraphAsync(tenantResource, subscriptionIds, @"
            resourcecontainers
            | where type == 'microsoft.resources/subscriptions'
            | project id, name, type, subscriptionId, tags, properties",
            el =>
            {
                var sub = ParseSubscriptionResult(tenantId, snapshotId, el);
                if (sub != null) subscriptions.Add(sub);
            });

        // 3. Enrich subscriptions with secure scores
        var secureScores = new Dictionary<string, JsonElement>();
        try
        {
            await QueryResourceGraphAsync(tenantResource, subscriptionIds, @"
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
            _logger.LogWarning(ex, "Secure score query failed for Azure connection {AzureTenantId} (workspace {TenantId}) — Defender may not be enabled",
                azureTenantId ?? "(primary)", tenantId);
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

        resources.AddRange(subscriptions);

        _logger.LogInformation("Collected {Count} resources ({SubCount} subscriptions) from Azure connection {AzureTenantId} for workspace {TenantId}",
            resources.Count, subscriptions.Count, azureTenantId ?? "(primary)", tenantId);

        return resources;
    }

    /// <summary>
    /// Resolves the ARM TenantResource matching a specific Azure AD tenant GUID.
    /// Matters once a credential (e.g. a Lighthouse-delegated managed identity) can
    /// see MULTIPLE Azure AD tenants: without this, ArmClient.GetTenants().GetAll()
    /// could resolve to the wrong tenant for a given connection.
    /// </summary>
    private static TenantResource ResolveTenantResource(ArmClient armClient, string? azureTenantId)
    {
        var all = armClient.GetTenants().GetAll().ToList();

        if (string.IsNullOrEmpty(azureTenantId))
            return all.First(); // legacy fallback: no specific tenant to match against

        var match = all.FirstOrDefault(t =>
            string.Equals(t.Id.Name, azureTenantId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Data.TenantId?.ToString(), azureTenantId, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            throw new InvalidOperationException(
                $"Azure AD tenant {azureTenantId} is not visible to the configured credential " +
                "(check the Lighthouse delegation or app registration tenant).");

        return match;
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
        IReadOnlyList<string>? subscriptionIds,
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
            // Scopes the query to specific subscriptions when the connection is
            // pinned to a subset; an empty list means "everything this credential
            // can see" (Resource Graph's default when Subscriptions is omitted).
            if (subscriptionIds is { Count: > 0 })
                foreach (var s in subscriptionIds) request.Subscriptions.Add(s);
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
