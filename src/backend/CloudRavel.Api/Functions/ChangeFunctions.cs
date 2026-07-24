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
/// HTTP-triggered functions for change intelligence.
/// Exposes Resource Graph Change History and Activity Log events.
/// </summary>
public sealed class ChangeFunctions
{
    private readonly IChangeRepository _changeRepo;
    private readonly ILogger<ChangeFunctions> _logger;

    public ChangeFunctions(IChangeRepository changeRepo, ILogger<ChangeFunctions> logger)
    {
        _changeRepo = changeRepo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/changes
    /// Returns resource changes with filtering support.
    /// </summary>
    [Function("GetChanges")]
    public async Task<HttpResponseData> GetChanges(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "changes")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var from = DateTime.TryParse(query["from"], out var f) ? f : (DateTime?)null;
        var to = DateTime.TryParse(query["to"], out var t) ? t : (DateTime?)null;
        var resourceId = query["resourceId"];
        var classification = Enum.TryParse<ChangeClassification>(query["classification"], true, out var c) ? c : (ChangeClassification?)null;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var changes = await _changeRepo.GetChangesAsync(tenantId, from, to, resourceId, classification, offset, limit);
        var total = await _changeRepo.GetChangeCountAsync(tenantId, from, to);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ChangesResponse
        {
            Changes = changes.Select(ch => new ResourceChangeDto
            {
                ChangeId = ch.ChangeId,
                ResourceId = ch.ResourceId,
                ResourceType = ch.ResourceType,
                ChangeType = ch.ChangeType.ToString(),
                DetectedAt = ch.DetectedAt,
                ChangedProperties = ch.ChangedProperties?.Select(p => new PropertyChangeDto
                {
                    Property = p.Path,
                    OldValue = p.Before,
                    NewValue = p.After
                }).ToList(),
                ActorName = ch.ActorName,
                ActorType = ch.ActorType,
                ClientType = ch.ClientType,
                Classification = ch.Classification.ToString(),
                Severity = ch.Severity?.ToString()
            }).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = total }
        });
        return response;
    }

    /// <summary>
    /// GET /api/changes/recent
    /// Returns changes from the last N hours (default: 24).
    /// </summary>
    [Function("GetRecentChanges")]
    public async Task<HttpResponseData> GetRecentChanges(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "changes/recent")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var hours = int.TryParse(query["hours"], out var h) ? Math.Min(h, 168) : 24; // Max 7 days

        var changes = await _changeRepo.GetRecentChangesAsync(tenantId, hours);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            TenantId = tenantId,
            WindowHours = hours,
            Changes = changes.Select(ch => new ResourceChangeDto
            {
                ChangeId = ch.ChangeId,
                ResourceId = ch.ResourceId,
                ResourceType = ch.ResourceType,
                ChangeType = ch.ChangeType.ToString(),
                DetectedAt = ch.DetectedAt,
                ActorName = ch.ActorName,
                ClientType = ch.ClientType,
                Classification = ch.Classification.ToString(),
                Severity = ch.Severity?.ToString()
            }).ToList()
        });
        return response;
    }

    /// <summary>
    /// GET /api/changes/timeline
    /// Returns bucketed change counts for timeline charting.
    /// </summary>
    [Function("GetChangeTimeline")]
    public async Task<HttpResponseData> GetChangeTimeline(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "changes/timeline")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var from = DateTime.TryParse(query["from"], out var f) ? f : DateTime.UtcNow.AddDays(-7);
        var to = DateTime.TryParse(query["to"], out var t) ? t : DateTime.UtcNow;

        var timeline = await _changeRepo.GetChangeTimelineAsync(tenantId, from, to);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            TenantId = tenantId,
            From = from,
            To = to,
            Buckets = timeline.Select(b => new ChangeTimelineDto
            {
                BucketStart = b.BucketStart,
                Total = b.TotalChanges,
                Security = b.SecurityChanges,
                Governance = b.GovernanceChanges,
                Cost = b.CostChanges,
                Operational = b.OperationalChanges
            }).ToList()
        });
        return response;
    }
}
