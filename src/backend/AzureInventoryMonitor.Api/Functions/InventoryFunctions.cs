using System.Net;
using AzureInventoryMonitor.Api.Middleware;
using AzureInventoryMonitor.Core.DTOs;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

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
    private readonly ILogger<InventoryFunctions> _logger;

    public InventoryFunctions(
        IInventoryRepository inventoryRepo,
        IInventoryCollectionService collectionService,
        ILogger<InventoryFunctions> logger)
    {
        _inventoryRepo = inventoryRepo;
        _collectionService = collectionService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/inventory/resources
    /// Returns inventory resources for the current (or specified) snapshot.
    /// Supports filtering by resource type, subscription, resource group.
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
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;
        long? snapshotId = long.TryParse(query["snapshotId"], out var sid) ? sid : null;

        var resources = await _inventoryRepo.GetResourcesAsync(
            tenantId, snapshotId, resourceType, subscriptionId, resourceGroup, offset, limit);
        var total = await _inventoryRepo.GetResourceCountAsync(tenantId, snapshotId);
        var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new InventoryResponse
        {
            SnapshotId = latestSnapshot?.SnapshotId ?? 0,
            SnapshotTime = latestSnapshot?.CompletedAt ?? DateTime.MinValue,
            TotalResources = total,
            Resources = resources.Select(r => new InventoryResourceDto
            {
                ResourceId = r.ResourceId,
                SubscriptionId = r.SubscriptionId,
                ResourceGroup = r.ResourceGroup,
                ResourceType = r.ResourceType,
                ResourceName = r.ResourceName,
                Location = r.Location,
                SkuName = r.SkuName,
                SkuTier = r.SkuTier,
                Tags = r.Tags,
                IdentityType = r.IdentityType
            }).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = total }
        });
        return response;
    }

    /// <summary>
    /// GET /api/inventory/resource/{resourceId}
    /// Returns full detail for a single resource, including properties, networking, security config.
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
        if (!decodedId.StartsWith("/"))
            decodedId = "/" + decodedId;

        var resource = await _inventoryRepo.GetResourceByIdAsync(tenantId, decodedId);
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

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(resource);
        return response;
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
            }).ToList()
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
        var tenantId = context.GetTenantId();

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
