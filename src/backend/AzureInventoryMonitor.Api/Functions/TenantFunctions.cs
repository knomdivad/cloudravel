using System.Net;
using System.Text.Json;
using AzureInventoryMonitor.Api.Middleware;
using AzureInventoryMonitor.Core.DTOs;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// HTTP-triggered functions for tenant management.
/// These endpoints manage the tenant lifecycle: onboarding, listing, updating, and monitoring status.
/// </summary>
public sealed class TenantFunctions
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IInventoryCollectionService _inventoryCollection;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<TenantFunctions> _logger;

    public TenantFunctions(
        ITenantRepository tenantRepo,
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        IRecommendationRepository recRepo,
        IAuditRepository auditRepo,
        IInventoryCollectionService inventoryCollection,
        ILogger<TenantFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _tenantRepo = tenantRepo;
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
        _recRepo = recRepo;
        _auditRepo = auditRepo;
        _inventoryCollection = inventoryCollection;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/tenants
    /// Returns all tenants the caller has access to, with summary stats.
    /// </summary>
    [Function("ListTenants")]
    public async Task<HttpResponseData> ListTenants(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenants")] HttpRequestData req,
        FunctionContext context)
    {
        // Tenant header is optional for listing — returns all accessible tenants
        var tenants = await _tenantRepo.GetAllActiveAsync();

        var summaries = new List<TenantSummaryDto>();
        foreach (var tenant in tenants)
        {
            var resourceCount = await _inventoryRepo.GetResourceCountAsync(tenant.TenantId);
            var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenant.TenantId);
            var recentChanges = await _changeRepo.GetRecentChangesAsync(tenant.TenantId, hours: 24, limit: 1);
            var changeCount = await _changeRepo.GetChangeCountAsync(tenant.TenantId,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

            summaries.Add(new TenantSummaryDto
            {
                TenantId = tenant.TenantId,
                DisplayName = tenant.DisplayName,
                AzureTenantId = tenant.AzureTenantId,
                Status = tenant.Status.ToString().ToLowerInvariant(),
                ResourceCount = resourceCount,
                LastSnapshotAt = latestSnapshot?.CompletedAt,
                Changes24H = changeCount
            });
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new TenantListResponse { Tenants = summaries });
        return response;
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}
    /// Returns detailed information about a specific tenant.
    /// </summary>
    [Function("GetTenant")]
    public async Task<HttpResponseData> GetTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenants/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        if (!Guid.TryParse(tenantId, out var id))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_ID", Message = "Tenant ID must be a valid GUID." });
            return badReq;
        }

        var tenant = await _tenantRepo.GetByIdAsync(id);
        if (tenant == null)
        {
            var notFound = req.CreateCorsResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new ErrorResponse { Code = "TENANT_NOT_FOUND", Message = $"Tenant '{id}' not found." });
            return notFound;
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(tenant);
        return response;
    }

    /// <summary>
    /// POST /api/tenants
    /// Onboards a new customer tenant.
    /// </summary>
    [Function("OnboardTenant")]
    public async Task<HttpResponseData> OnboardTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants")] HttpRequestData req,
        FunctionContext context)
    {
        var body = await req.ReadFromJsonAsync<OnboardTenantRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.AzureTenantId))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "INVALID_REQUEST",
                Message = "DisplayName and AzureTenantId are required."
            });
            return badReq;
        }

        if (!Enum.TryParse<OnboardingMethod>(body.OnboardingMethod.Replace("_", ""), true, out var method))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "INVALID_METHOD",
                Message = "OnboardingMethod must be 'lighthouse' or 'app_registration'."
            });
            return badReq;
        }

        var tenant = new Tenant
        {
            DisplayName = body.DisplayName,
            AzureTenantId = body.AzureTenantId,
            OnboardingMethod = method,
            Status = TenantStatus.Active,
            LighthouseDelegationId = body.LighthouseDelegationId
        };

        // For App Registration, a secret name would be generated or provided
        if (method == OnboardingMethod.AppRegistration && !string.IsNullOrEmpty(body.ClientId))
        {
            tenant.SecretName = $"tenant-{body.AzureTenantId}-creds";
        }

        var createdBy = context.GetUserId() ?? Guid.Empty;
        var created = await _tenantRepo.CreateAsync(tenant, createdBy);

        // Store credentials in the secret store for App Registration tenants
        if (method == OnboardingMethod.AppRegistration
            && !string.IsNullOrEmpty(body.ClientId)
            && !string.IsNullOrEmpty(body.ClientSecret))
        {
            if (_secretStore == null)
            {
                _logger.LogWarning("Secret store not configured — cannot store credentials for tenant {TenantId}", created.TenantId);
            }
            else
            {
                var secretPayload = JsonSerializer.Serialize(new
                {
                    clientId = body.ClientId,
                    clientSecret = body.ClientSecret
                });
                await _secretStore.SetSecretAsync(created.SecretName!, secretPayload);
                _logger.LogInformation("Stored credentials in the secret store for tenant {TenantId} as {SecretName}",
                    created.TenantId, created.SecretName);
            }
        }

        await _auditRepo.LogAsync(new AuditEvent
        {
            TenantId = created.TenantId,
            UserId = createdBy,
            Action = "tenant.onboard",
            EntityType = "tenant",
            EntityId = created.TenantId.ToString()
        });

        _logger.LogInformation("Onboarded tenant {TenantId} ({DisplayName})", created.TenantId, created.DisplayName);

        // Trigger initial inventory collection in the background (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting initial inventory collection for tenant {TenantId}", created.TenantId);
                var (snapshotId, resourceCount) = await _inventoryCollection.CollectInventoryAsync(created.TenantId, "onboarding");
                _logger.LogInformation("Initial inventory collection completed for tenant {TenantId}: {Count} resources in snapshot {SnapshotId}",
                    created.TenantId, resourceCount, snapshotId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial inventory collection failed for tenant {TenantId}", created.TenantId);
            }
        });

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new
        {
            created.TenantId,
            created.DisplayName,
            Status = created.Status.ToString().ToLowerInvariant(),
            Message = "Tenant onboarded successfully. Initial snapshot will be triggered shortly."
        });
        return response;
    }

    /// <summary>
    /// PATCH /api/tenants/{tenantId}/status
    /// Updates a tenant's status (active, suspended, offboarded, etc.).
    /// </summary>
    [Function("UpdateTenantStatus")]
    public async Task<HttpResponseData> UpdateTenantStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/status")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        if (!Guid.TryParse(tenantId, out var id))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse { Code = "INVALID_ID", Message = "Tenant ID must be a valid GUID." });
            return badReq;
        }

        var body = await req.ReadFromJsonAsync<StatusUpdateRequest>();
        if (body == null || !Enum.TryParse<TenantStatus>(body.Status, true, out var newStatus))
        {
            var badReq = req.CreateCorsResponse(HttpStatusCode.BadRequest);
            await badReq.WriteAsJsonAsync(new ErrorResponse
            {
                Code = "INVALID_STATUS",
                Message = "Valid statuses: Active, Degraded, Suspended, Offboarded"
            });
            return badReq;
        }

        await _tenantRepo.UpdateStatusAsync(id, newStatus);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { TenantId = id, Status = newStatus.ToString().ToLowerInvariant(), UpdatedAt = DateTime.UtcNow });
        return response;
    }

    /// <summary>
    /// GET /api/dashboard
    /// Returns aggregated dashboard data for the current tenant.
    /// </summary>
    [Function("GetDashboard")]
    public async Task<HttpResponseData> GetDashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);

        var resourceCount = await _inventoryRepo.GetResourceCountAsync(tenantId);
        var latestSnapshot = await _inventoryRepo.GetLatestSnapshotAsync(tenantId);
        var resourceTypes = await _inventoryRepo.GetResourceTypeSummaryAsync(tenantId);
        var changeCount = await _changeRepo.GetChangeCountAsync(tenantId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        var advisorRecs = await _recRepo.GetAdvisorRecommendationsAsync(tenantId, status: RecommendationLifecycle.Active, limit: 500);
        var policyNonCompliant = await _recRepo.GetPolicyComplianceAsync(tenantId, "NonCompliant", limit: 500);
        var defenderFindings = await _recRepo.GetDefenderFindingsAsync(tenantId, status: "Unhealthy", limit: 500);

        var timeline = await _changeRepo.GetChangeTimelineAsync(tenantId, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        var savings = advisorRecs.Where(r => r.Category == "Cost" && r.EstimatedSavings.HasValue)
            .Sum(r => r.EstimatedSavings!.Value);

        var severityCounts = defenderFindings
            .GroupBy(f => f.Severity)
            .Select(g => new SeverityCountDto { Severity = g.Key, Count = g.Count() })
            .ToList();

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new TenantDashboardResponse
        {
            TenantId = tenantId,
            TenantName = tenant?.DisplayName ?? "Unknown",
            TotalResources = resourceCount,
            LastSnapshotAt = latestSnapshot?.CompletedAt,
            Changes24H = changeCount,
            OpenAdvisorRecs = advisorRecs.Count,
            NonCompliantPolicies = policyNonCompliant.Count,
            OpenDefenderFindings = defenderFindings.Count,
            EstimatedMonthlySavings = savings / 12,
            ResourceBreakdown = resourceTypes.Take(15).Select(r => new ResourceTypeSummaryDto
            {
                ResourceType = r.ResourceType,
                Count = r.Count
            }).ToList(),
            ChangeTimeline = timeline.Select(t => new ChangeTimelineDto
            {
                BucketStart = t.BucketStart,
                Total = t.TotalChanges,
                Security = t.SecurityChanges,
                Governance = t.GovernanceChanges,
                Cost = t.CostChanges,
                Operational = t.OperationalChanges
            }).ToList(),
            FindingsBySeverity = severityCounts
        });
        return response;
    }

    private sealed class StatusUpdateRequest
    {
        public string Status { get; set; } = string.Empty;
    }
}
