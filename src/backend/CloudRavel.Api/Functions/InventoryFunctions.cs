using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// HTTP-triggered functions for inventory data access.
/// 
/// All endpoints require:
///   - Valid JWT (Entra ID)
///   - X-Tenant-Id header
///   - User must have access to the specified tenant
/// 
/// Data is always served from ARI snapshots (ground truth), not live Azure queries.
/// </summary>
public sealed class InventoryFunctions
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IInventoryCollectionService _collectionService;
    private readonly ICloudAccountRepository _cloudAccountRepo;
    private readonly ICloudOrgRepository _cloudOrgRepo;
    private readonly IPlatformInfo _platform;
    private readonly ILogger<InventoryFunctions> _logger;

    public InventoryFunctions(
        IInventoryRepository inventoryRepo,
        IInventoryCollectionService collectionService,
        ICloudAccountRepository cloudAccountRepo,
        ICloudOrgRepository cloudOrgRepo,
        IPlatformInfo platform,
        ILogger<InventoryFunctions> logger)
    {
        _inventoryRepo = inventoryRepo;
        _collectionService = collectionService;
        _cloudAccountRepo = cloudAccountRepo;
        _cloudOrgRepo = cloudOrgRepo;
        _platform = platform;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/inventory/resources
    /// Returns inventory resources for the current (or specified) snapshot.
    /// Supports filtering by resource type, subscription/account/project, resource group, provider.
    /// Each row includes multi-cloud context (cloud, scope, org).
    /// </summary>
    [Function("GetInventoryResources")]
    public async Task<HttpResponseData> GetResources(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/resources")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var resourceType = query["resourceType"];
        var subscriptionId = query["subscriptionId"];
        var resourceGroup = query["resourceGroup"];
        var provider = query["provider"];
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;
        long? snapshotId = long.TryParse(query["snapshotId"], out var sid) ? sid : null;

        var resources = await _inventoryRepo.GetResourcesAsync(
            tenantId, snapshotId, resourceType, subscriptionId, resourceGroup, provider, offset, limit);
        var total = await _inventoryRepo.GetResourceCountAsync(tenantId, snapshotId);
        var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);
        var scope = await BuildScopeLookupAsync(tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new InventoryResponse
        {
            SnapshotId = latestSnapshot?.SnapshotId ?? 0,
            SnapshotTime = latestSnapshot?.CompletedAt ?? DateTime.MinValue,
            TotalResources = total,
            Resources = resources.Select(r => ToDto(r, scope)).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = total }
        });
        return response;
    }

    /// <summary>
    /// GET /api/inventory/resource/{resourceId}
    /// Returns full detail for a single resource, including properties, networking, security config,
    /// plus multi-cloud context fields (provider, scope, cloud org).
    /// </summary>
    [Function("GetInventoryResourceDetail")]
    public async Task<HttpResponseData> GetResourceDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/resource/{*resourceId}")] HttpRequestData req,
        string resourceId,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var decodedId = Uri.UnescapeDataString(resourceId);
        // Catch-all route strips the leading '/' from ARM resource IDs
        // (e.g., /subscriptions/... → subscriptions/...) due to HTTP path normalization
        if (!decodedId.StartsWith("/") && !decodedId.StartsWith("arn:", StringComparison.OrdinalIgnoreCase)
            && !decodedId.StartsWith("//", StringComparison.Ordinal))
            decodedId = "/" + decodedId;

        var resource = await _inventoryRepo.GetResourceByIdAsync(tenantId, decodedId);
        // GCP/AWS ids don't use leading slash — try original if ARM-style prepend failed
        if (resource == null && decodedId.StartsWith('/'))
            resource = await _inventoryRepo.GetResourceByIdAsync(tenantId, decodedId.TrimStart('/'));
        if (resource == null)
            resource = await _inventoryRepo.GetResourceByIdAsync(tenantId, Uri.UnescapeDataString(resourceId));

        if (resource == null)
        {
            var notFound = req.CreateCorsResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "RESOURCE_NOT_FOUND",
                Message = $"Resource '{decodedId}' not found in current inventory."
            });
            return notFound;
        }

        var scope = await BuildScopeLookupAsync(tenantId);
        var dto = ToDto(resource, scope);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        // Merge cloud-context DTO fields with full resource payload for the detail page.
        await response.WriteAsJsonAsync(new
        {
            resource.ResourceId,
            resource.SubscriptionId,
            resource.ResourceGroup,
            resource.ResourceType,
            resource.ResourceName,
            resource.Location,
            resource.SkuName,
            resource.SkuTier,
            resource.SkuCapacity,
            resource.Tags,
            resource.IdentityType,
            resource.IdentityPrincipalIds,
            resource.PropertiesJson,
            resource.NetworkingJson,
            resource.SecurityConfigJson,
            provider = dto.Provider,
            cloud = dto.Cloud,
            scopeKind = dto.ScopeKind,
            scopeId = dto.ScopeId,
            scopeName = dto.ScopeName,
            cloudOrgName = dto.CloudOrgName,
            azureTenantId = dto.AzureTenantId,
            resourceGroupKind = dto.ResourceGroupKind
        });
        return response;
    }

    private async Task<ScopeLookup> BuildScopeLookupAsync(Guid tenantId)
    {
        var accounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);
        var orgs = await _cloudOrgRepo.GetByTenantAsync(tenantId);
        var orgsById = orgs.ToDictionary(o => o.OrgId, o => o);

        var byProviderScope = new Dictionary<(string Provider, string ExternalId), (CloudAccount? Account, CloudOrg? Org)>(
            new ProviderScopeComparer());

        foreach (var a in accounts)
        {
            var key = (a.Provider.ToString().ToLowerInvariant(), a.ExternalId);
            orgsById.TryGetValue(a.OrgId, out var org);
            byProviderScope[key] = (a, org);
        }

        // Azure resources use subscription_id = Azure subscription GUID, not always a cloud_accounts row.
        // Map Azure cloud_orgs (connections) by external_id for tenant id display.
        var azureOrgs = orgs.Where(o => o.Provider == CloudProvider.Azure).ToList();

        return new ScopeLookup(byProviderScope, azureOrgs);
    }

    private static InventoryResourceDto ToDto(InventoryResource r, ScopeLookup scope)
    {
        var provider = NormalizeProvider(r.Provider, r.ResourceId);
        var (cloud, scopeKind, rgKind) = provider switch
        {
            "aws" => ("AWS", "account", "Service"),
            "gcp" => ("GCP", "project", "Namespace"),
            _ => ("Azure", "subscription", "Resource group")
        };

        string? scopeName = null;
        string? cloudOrgName = null;
        string? azureTenantId = null;

        if (scope.ByProviderScope.TryGetValue((provider, r.SubscriptionId), out var hit))
        {
            scopeName = hit.Account?.DisplayName;
            cloudOrgName = hit.Org?.Name;
            if (provider == "azure")
                azureTenantId = hit.Org?.ExternalId;
        }

        if (provider == "azure" && azureTenantId == null && scope.AzureOrgs.Count == 1)
        {
            azureTenantId = scope.AzureOrgs[0].ExternalId;
            cloudOrgName ??= scope.AzureOrgs[0].Name;
        }

        // Infer Azure tenant from ARM id when present
        if (provider == "azure" && string.IsNullOrEmpty(azureTenantId) && r.ResourceId.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
        {
            // leave null — subscription is the primary scope
        }

        return new InventoryResourceDto
        {
            ResourceId = r.ResourceId,
            Provider = provider,
            Cloud = cloud,
            ScopeKind = scopeKind,
            ScopeId = r.SubscriptionId,
            ScopeName = scopeName,
            CloudOrgName = cloudOrgName,
            AzureTenantId = azureTenantId,
            ResourceGroup = r.ResourceGroup,
            ResourceGroupKind = rgKind,
            SubscriptionId = r.SubscriptionId,
            ResourceType = r.ResourceType,
            ResourceName = FriendlyResourceName(r),
            Location = r.Location,
            SkuName = r.SkuName,
            SkuTier = r.SkuTier,
            Tags = r.Tags,
            IdentityType = r.IdentityType
        };
    }

    /// <summary>
    /// Prefer a human-readable name over full ARNs / Cloud Asset paths for list UI.
    /// </summary>
    private static string FriendlyResourceName(InventoryResource r)
    {
        // AWS Name tag / GCP label often already stored on Tags
        if (r.Tags != null)
        {
            foreach (var key in new[] { "Name", "name", "displayName" })
            {
                if (r.Tags.TryGetValue(key, out var tag) && !string.IsNullOrWhiteSpace(tag)
                    && !LooksLikeFullResourceId(tag))
                    return tag.Trim();
            }
        }

        var name = r.ResourceName?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name) && !LooksLikeFullResourceId(name) && name != r.ResourceId)
            return name;

        var leaf = LeafName(r.ResourceId);
        if (!string.IsNullOrEmpty(leaf) && !LooksLikeFullResourceId(leaf))
            return leaf;

        return !string.IsNullOrEmpty(name) ? name : r.ResourceId;
    }

    private static bool LooksLikeFullResourceId(string value) =>
        value.StartsWith("arn:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("//", StringComparison.Ordinal)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || (value.Contains("/projects/", StringComparison.OrdinalIgnoreCase) && value.Count(c => c == '/') >= 4)
        || (value.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) && value.Count(c => c == '/') >= 6);

    private static string? LeafName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        // ARN: last path segment after final / (or full resource part for s3:::bucket)
        if (id.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split(':', 6);
            var resourcePart = parts.Length > 5 ? parts[5] : id;
            if (string.IsNullOrEmpty(resourcePart) && parts.Length > 4)
                resourcePart = parts[^1];
            if (resourcePart.Contains('/'))
                return resourcePart[(resourcePart.LastIndexOf('/') + 1)..];
            return resourcePart;
        }
        if (id.Contains('/'))
            return id[(id.LastIndexOf('/') + 1)..];
        return id;
    }

    private static string NormalizeProvider(string? provider, string resourceId)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var p = provider.Trim().ToLowerInvariant();
            if (p is "azure" or "aws" or "gcp") return p;
            if (p == "amazon") return "aws";
            if (p is "google" or "googlecloud") return "gcp";
        }
        return CloudProviderInference.FromResource(resourceId).ToString().ToLowerInvariant() switch
        {
            "aws" => "aws",
            "gcp" => "gcp",
            _ => "azure"
        };
    }

    private sealed class ScopeLookup(
        Dictionary<(string Provider, string ExternalId), (CloudAccount? Account, CloudOrg? Org)> byProviderScope,
        List<CloudOrg> azureOrgs)
    {
        public Dictionary<(string Provider, string ExternalId), (CloudAccount? Account, CloudOrg? Org)> ByProviderScope { get; } = byProviderScope;
        public List<CloudOrg> AzureOrgs { get; } = azureOrgs;
    }

    private sealed class ProviderScopeComparer : IEqualityComparer<(string Provider, string ExternalId)>
    {
        public bool Equals((string Provider, string ExternalId) x, (string Provider, string ExternalId) y) =>
            string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Provider, string ExternalId) obj) =>
            HashCode.Combine(obj.Provider.ToLowerInvariant(), obj.ExternalId.ToLowerInvariant());
    }

    /// <summary>
    /// GET /api/inventory/summary
    /// Returns resource type breakdown for the current snapshot.
    /// </summary>
    [Function("GetInventorySummary")]
    public async Task<HttpResponseData> GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/summary")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();

        var summary = await _inventoryRepo.GetResourceTypeSummaryAsync(tenantId);
        var byProvider = await _inventoryRepo.GetResourceCountsByProviderAsync(tenantId);
        var total = await _inventoryRepo.GetResourceCountAsync(tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            TenantId = tenantId,
            TotalResources = total,
            ResourceTypes = summary.Select(s => new ResourceTypeSummaryDto
            {
                ResourceType = s.ResourceType,
                Count = s.Count
            }).ToList(),
            // Multi-cloud breakdown for dashboard pie (azure / aws / gcp → count)
            ByProvider = byProvider
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { provider = kv.Key, count = kv.Value })
                .ToList()
        });
        return response;
    }

    /// <summary>
    /// GET /api/inventory/snapshots
    /// Returns snapshot history for the tenant.
    /// </summary>
    [Function("GetSnapshotHistory")]
    public async Task<HttpResponseData> GetSnapshots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/snapshots")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 200) : 50;

        var snapshots = await _inventoryRepo.GetSnapshotHistoryAsync(tenantId, limit);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { Snapshots = snapshots });
        return response;
    }

    /// <summary>
    /// POST /api/inventory/snapshots/trigger
    /// Manually triggers an inline inventory snapshot using Azure Resource Graph.
    /// </summary>
    [Function("TriggerSnapshot")]
    public async Task<HttpResponseData> TriggerSnapshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inventory/snapshots/trigger")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;

        var tenantId = context.GetTenantId();

        if (!_platform.IsProduction)
        {
            var devResp = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await devResp.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "DEVELOPMENT_MODE",
                Message = $"Inventory collection is disabled while the instance environment is {_platform.Environment}. " +
                          "Set Platform:Environment=Production to collect against real clouds."
            });
            return devResp;
        }

        try
        {
            var (snapshotId, resourceCount) = await _collectionService.CollectInventoryAsync(tenantId, "manual");

            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                SnapshotId = snapshotId,
                ResourceCount = resourceCount,
                Status = "completed",
                Message = $"Inventory snapshot completed with {resourceCount} resources."
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot trigger failed for tenant {TenantId}", tenantId);

            var errResponse = req.CreateCorsResponse(HttpStatusCode.InternalServerError);
            await errResponse.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "SNAPSHOT_FAILED",
                Message = $"Inventory collection failed: {ex.Message}"
            });
            return errResponse;
        }
    }
}
