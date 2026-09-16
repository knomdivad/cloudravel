using System.Net;
using System.Text;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// HTTP endpoints for the fixed-fee assessment engagement:
///
///   GET  /api/assessment                 — current engagement state (all callers).
///   POST /api/assessment/start           — open the read-only watch window (cloud_admin).
///   POST /api/assessment/complete        — close the window; fix-list delivered (cloud_admin).
///   POST /api/assessment/convert         — convert to ongoing monitoring (cloud_admin).
///   GET  /api/assessment/report?format=json|csv|md — the ranked fix-list.
///
/// Starting an assessment forces AutoRemediationMode = Disabled; the remediation
/// engine additionally refuses approval/execution while an assessment is active
/// (see AssessmentPolicy). Converting restores the Gated default.
/// </summary>
public sealed class AssessmentFunctions
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IAnomalyRepository _anomalyRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ILogger<AssessmentFunctions> _logger;

    public AssessmentFunctions(
        ITenantRepository tenantRepo,
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        IRecommendationRepository recRepo,
        IAnomalyRepository anomalyRepo,
        IAuditRepository auditRepo,
        ILogger<AssessmentFunctions> logger)
    {
        _tenantRepo = tenantRepo;
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
        _recRepo = recRepo;
        _anomalyRepo = anomalyRepo;
        _auditRepo = auditRepo;
        _logger = logger;
    }

    /// <summary>GET /api/assessment — engagement state for the current workspace.</summary>
    [Function("GetAssessment")]
    public async Task<HttpResponseData> GetAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessment")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null)
            return await NotFound(req, $"Workspace {tenantId} not found.");

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(BuildStateDto(tenant));
        return response;
    }

    /// <summary>
    /// POST /api/assessment/start — open the watch window. Body:
    /// { "windowDays": 10, "fee": 3000 } (both optional; fee is informational,
    /// used by the report's guarantee math).
    /// </summary>
    [Function("StartAssessment")]
    public async Task<HttpResponseData> StartAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessment/start")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;

        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null)
            return await NotFound(req, $"Workspace {tenantId} not found.");

        if (tenant.EngagementKind == EngagementKind.Assessment && tenant.AssessmentCompletedAt is null)
            return await Conflict(req, "ASSESSMENT_ALREADY_ACTIVE",
                "An assessment is already in progress for this workspace. Complete or convert it first.");

        var body = await req.ReadFromJsonAsync<StartAssessmentRequest>();

        var windowDays = body?.WindowDays ?? AssessmentReportBuilder.DefaultWindowDays;
        if (windowDays is < 1 or > 90)
            return await BadRequest(req, "INVALID_WINDOW", "windowDays must be between 1 and 90.");

        var now = DateTime.UtcNow;
        tenant.EngagementKind = EngagementKind.Assessment;
        tenant.AssessmentStartedAt = now;
        tenant.AssessmentEndsAt = now.AddDays(windowDays);
        tenant.AssessmentCompletedAt = null;
        // Read-only in effect: nothing auto-approves or executes during an assessment.
        tenant.AutoRemediationMode = AutoRemediationMode.Disabled;
        await _tenantRepo.UpdateAsync(tenant);

        await AuditAsync(tenantId, context, "assessment.start",
            $"{{\"windowDays\":{windowDays},\"fee\":{body?.Fee ?? AssessmentReportBuilder.DefaultFee}}}");

        _logger.LogInformation("Assessment started for workspace {TenantId} (window {Days} days, ends {Ends})",
            tenantId, windowDays, tenant.AssessmentEndsAt);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(BuildStateDto(tenant));
        return response;
    }

    /// <summary>POST /api/assessment/complete — the fix-list was delivered; close the engagement.</summary>
    [Function("CompleteAssessment")]
    public async Task<HttpResponseData> CompleteAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessment/complete")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;

        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null)
            return await NotFound(req, $"Workspace {tenantId} not found.");
        if (tenant.EngagementKind != EngagementKind.Assessment || tenant.AssessmentStartedAt is null)
            return await Conflict(req, "NO_ACTIVE_ASSESSMENT", "No assessment has been started for this workspace.");
        if (tenant.AssessmentCompletedAt.HasValue)
            return await Conflict(req, "ASSESSMENT_ALREADY_COMPLETED", "This assessment is already completed.");

        tenant.AssessmentCompletedAt = DateTime.UtcNow;
        await _tenantRepo.UpdateAsync(tenant);

        await AuditAsync(tenantId, context, "assessment.complete", null);

        _logger.LogInformation("Assessment completed for workspace {TenantId}", tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(BuildStateDto(tenant));
        return response;
    }

    /// <summary>
    /// POST /api/assessment/convert — the customer keeps CloudRavel: convert the
    /// completed assessment into an ongoing monitoring engagement (restores the
    /// Gated approval default).
    /// </summary>
    [Function("ConvertAssessment")]
    public async Task<HttpResponseData> ConvertAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessment/convert")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;

        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null)
            return await NotFound(req, $"Workspace {tenantId} not found.");
        if (tenant.EngagementKind != EngagementKind.Assessment)
            return await Conflict(req, "NOT_AN_ASSESSMENT", "This workspace is not in an assessment engagement.");

        tenant.EngagementKind = EngagementKind.Standard;
        tenant.AutoRemediationMode = AutoRemediationMode.Gated;
        await _tenantRepo.UpdateAsync(tenant);

        await AuditAsync(tenantId, context, "assessment.convert", null);

        _logger.LogInformation("Workspace {TenantId} converted from assessment to ongoing monitoring", tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(BuildStateDto(tenant));
        return response;
    }

    /// <summary>
    /// GET /api/assessment/report?format=json|csv|md&amp;fee=3000 — the ranked fix-list.
    /// Pulls the estate picture from the authoritative stores (inventory, changes,
    /// Advisor, anomalies) and ranks findings by dollars.
    /// </summary>
    [Function("GetAssessmentReport")]
    public async Task<HttpResponseData> GetAssessmentReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessment/report")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null)
            return await NotFound(req, $"Workspace {tenantId} not found.");

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var format = (query["format"] ?? "json").Trim().ToLowerInvariant();
        var fee = decimal.TryParse(query["fee"], out var f) && f > 0 ? f : AssessmentReportBuilder.DefaultFee;

        // The watch window bounds the change summary; fall back to the last 30 days
        // so a report still means something before/at start.
        var windowStart = tenant.AssessmentStartedAt ?? DateTime.UtcNow.AddDays(-30);
        var windowEnd = tenant.AssessmentEndsAt ?? DateTime.UtcNow;

        var advisorRecs = await _recRepo.GetAdvisorRecommendationsAsync(tenantId, status: RecommendationLifecycle.Active, limit: 500);
        var anomalies = await _anomalyRepo.GetAnomaliesAsync(tenantId, limit: 500);
        var resourceCount = await _inventoryRepo.GetResourceCountAsync(tenantId);
        var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);
        var changesInWindow = await _changeRepo.GetChangeCountAsync(tenantId, windowStart, DateTime.UtcNow);

        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = tenant.DisplayName,
            AssessmentStartedAt = tenant.AssessmentStartedAt,
            AssessmentEndsAt = tenant.AssessmentEndsAt,
            AssessmentCompletedAt = tenant.AssessmentCompletedAt,
            ResourceCount = resourceCount,
            LastSnapshotAt = latestSnapshot?.CompletedAt,
            ChangesInWindow = changesInWindow,
            AdvisorRecommendations = advisorRecs,
            Anomalies = anomalies
        }, fee);

        HttpResponseData response = format switch
        {
            "csv" => await TextResponse(req, HttpStatusCode.OK, "text/csv; charset=utf-8", AssessmentReportBuilder.ToCsv(report)),
            "md" or "markdown" => await TextResponse(req, HttpStatusCode.OK, "text/markdown; charset=utf-8", AssessmentReportBuilder.ToMarkdown(report)),
            _ => await JsonResponse(req, report)
        };

        _logger.LogInformation("Assessment report generated for workspace {TenantId} ({Format}, {Items} items, ${Savings}/yr)",
            tenantId, format, report.FixList.Count, report.Savings.IdentifiedAnnual);
        return response;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static object BuildStateDto(Tenant tenant) => new
    {
        tenantId = tenant.TenantId,
        workspaceName = tenant.DisplayName,
        engagementKind = tenant.EngagementKind.ToString().ToLowerInvariant(),
        state = tenant.EngagementKind == EngagementKind.Assessment
            ? (tenant.AssessmentCompletedAt.HasValue ? "completed"
                : tenant.AssessmentStartedAt.HasValue ? "watching" : "not_started")
            : "standard",
        assessmentStartedAt = tenant.AssessmentStartedAt,
        assessmentEndsAt = tenant.AssessmentEndsAt,
        assessmentCompletedAt = tenant.AssessmentCompletedAt,
        autoRemediationMode = tenant.AutoRemediationMode.ToString().ToLowerInvariant(),
        readOnly = AssessmentPolicy.BlocksActions(tenant)
    };

    private async Task AuditAsync(Guid tenantId, FunctionContext context, string action, string? detailsJson)
    {
        await _auditRepo.LogAsync(new AuditEvent
        {
            TenantId = tenantId,
            UserId = context.GetUserId() ?? Guid.Empty,
            Action = action,
            EntityType = "tenant",
            EntityId = tenantId.ToString(),
            DetailsJson = detailsJson
        });
    }

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, object payload)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(payload);
        return response;
    }

    private static async Task<HttpResponseData> TextResponse(
        HttpRequestData req, HttpStatusCode status, string contentType, string body)
    {
        var response = req.CreateCorsResponse(status);
        response.Headers.Add("Content-Type", contentType);
        await response.WriteStringAsync(body, Encoding.UTF8);
        return response;
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Conflict);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = "NOT_FOUND", Message = message });
        return response;
    }

    public sealed class StartAssessmentRequest
    {
        /// <summary>Watch-window length in days. Default 10.</summary>
        public int? WindowDays { get; set; }
        /// <summary>Engagement fee (informational; feeds the guarantee math in reports). Default 3000.</summary>
        public decimal? Fee { get; set; }
    }
}
