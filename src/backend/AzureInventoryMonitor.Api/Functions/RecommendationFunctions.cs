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
/// HTTP-triggered functions for recommendations, policy compliance, and Defender findings.
/// Provides a unified view across Advisor, Policy, and Defender sources.
/// </summary>
public sealed class RecommendationFunctions
{
    private readonly IRecommendationRepository _recRepo;
    private readonly ILogger<RecommendationFunctions> _logger;

    public RecommendationFunctions(IRecommendationRepository recRepo, ILogger<RecommendationFunctions> logger)
    {
        _recRepo = recRepo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/recommendations/advisor
    /// Returns Azure Advisor recommendations.
    /// </summary>
    [Function("GetAdvisorRecommendations")]
    public async Task<HttpResponseData> GetAdvisorRecommendations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations/advisor")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var category = query["category"];
        var status = Enum.TryParse<RecommendationLifecycle>(query["status"], true, out var s) ? s : (RecommendationLifecycle?)null;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var recs = await _recRepo.GetAdvisorRecommendationsAsync(tenantId, category, status, offset, limit);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new RecommendationsResponse
        {
            Recommendations = recs.Select(r => new RecommendationDto
            {
                Source = "advisor",
                Id = r.RecommendationId,
                ResourceId = r.ResourceId,
                Category = r.Category,
                Severity = r.Impact,
                Title = r.Title,
                Description = r.Description,
                RemediationAction = r.RemediationAction,
                EstimatedSavings = r.EstimatedSavings,
                Status = r.LifecycleStatus.ToString(),
                FirstSeenAt = r.FirstSeenAt,
                LastSeenAt = r.LastSeenAt
            }).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = recs.Count }
        });
        return response;
    }

    /// <summary>
    /// GET /api/recommendations/policy
    /// Returns Azure Policy compliance states (non-compliant focus).
    /// </summary>
    [Function("GetPolicyCompliance")]
    public async Task<HttpResponseData> GetPolicyCompliance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations/policy")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var complianceState = query["state"] ?? "NonCompliant";
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var records = await _recRepo.GetPolicyComplianceAsync(tenantId, complianceState, offset, limit);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            TenantId = tenantId,
            Records = records.Select(r => new
            {
                r.PolicyName,
                r.PolicyDefinitionId,
                r.ResourceId,
                r.ComplianceState,
                r.Category,
                r.LastEvaluatedAt
            }).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = records.Count }
        });
        return response;
    }

    /// <summary>
    /// GET /api/recommendations/defender
    /// Returns Microsoft Defender for Cloud findings.
    /// </summary>
    [Function("GetDefenderFindings")]
    public async Task<HttpResponseData> GetDefenderFindings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recommendations/defender")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var severity = query["severity"];
        var status = query["status"] ?? "Unhealthy";
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var findings = await _recRepo.GetDefenderFindingsAsync(tenantId, severity, status, offset, limit);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            TenantId = tenantId,
            Findings = findings.Select(f => new RecommendationDto
            {
                Source = "defender",
                Id = f.FindingId,
                ResourceId = f.ResourceId,
                Category = f.Categories?.FirstOrDefault() ?? "Security",
                Severity = f.Severity,
                Title = f.AssessmentName,
                Description = f.Description,
                RemediationAction = f.RemediationSteps,
                Status = f.Status,
                FirstSeenAt = f.FirstSeenAt,
                LastSeenAt = f.LastSeenAt
            }).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = findings.Count }
        });
        return response;
    }

    /// <summary>
    /// PATCH /api/recommendations/advisor/{recommendationId}/lifecycle
    /// Updates the lifecycle status of an Advisor recommendation (acknowledge, snooze, dismiss).
    /// </summary>
    [Function("UpdateRecommendationLifecycle")]
    public async Task<HttpResponseData> UpdateLifecycle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "recommendations/advisor/{recommendationId}/lifecycle")] HttpRequestData req,
        string recommendationId,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<LifecycleUpdateRequest>();
        if (body == null || !Enum.TryParse<RecommendationLifecycle>(body.Status, true, out var newStatus))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "INVALID_STATUS",
                Message = "Valid statuses: Active, Acknowledged, Snoozed, Resolved, Dismissed"
            });
            return badRequest;
        }

        await _recRepo.UpdateRecommendationLifecycleAsync(
            tenantId,
            Uri.UnescapeDataString(recommendationId),
            newStatus,
            body.AcknowledgedBy);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { Status = newStatus.ToString(), UpdatedAt = DateTime.UtcNow });
        return response;
    }

    private sealed class LifecycleUpdateRequest
    {
        public string Status { get; set; } = string.Empty;
        public Guid? AcknowledgedBy { get; set; }
    }
}
