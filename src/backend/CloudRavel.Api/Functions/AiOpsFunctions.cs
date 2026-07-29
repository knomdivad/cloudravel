using System.Net;
using System.Text.Json;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// HTTP endpoints for the AIOps surface: anomalies, incidents, remediation
/// actions (with the approval gate), the playbook catalog, linked multi-cloud
/// accounts, and the operations dashboard summary.
/// </summary>
public sealed class AiOpsFunctions
{
    private readonly IAnomalyRepository _anomalyRepo;
    private readonly IIncidentRepository _incidentRepo;
    private readonly IRemediationRepository _remediationRepo;
    private readonly IRemediationService _remediationService;
    private readonly ICloudOrgRepository _cloudOrgRepo;
    private readonly ICloudAccountRepository _cloudAccountRepo;
    private readonly ICloudProviderAdapterFactory _adapterFactory;
    private readonly IMultiCloudInventoryService _multiCloudInventory;
    private readonly IMultiCloudGovernanceSyncService _multiCloudGovernance;
    private readonly ITenantRepository _tenantRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IPlatformInfo _platform;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<AiOpsFunctions> _logger;

    public AiOpsFunctions(
        IAnomalyRepository anomalyRepo,
        IIncidentRepository incidentRepo,
        IRemediationRepository remediationRepo,
        IRemediationService remediationService,
        ICloudOrgRepository cloudOrgRepo,
        ICloudAccountRepository cloudAccountRepo,
        ICloudProviderAdapterFactory adapterFactory,
        IMultiCloudInventoryService multiCloudInventory,
        IMultiCloudGovernanceSyncService multiCloudGovernance,
        ITenantRepository tenantRepo,
        IInventoryRepository inventoryRepo,
        IPlatformInfo platform,
        ILogger<AiOpsFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _anomalyRepo = anomalyRepo;
        _incidentRepo = incidentRepo;
        _remediationRepo = remediationRepo;
        _remediationService = remediationService;
        _cloudOrgRepo = cloudOrgRepo;
        _cloudAccountRepo = cloudAccountRepo;
        _adapterFactory = adapterFactory;
        _multiCloudInventory = multiCloudInventory;
        _multiCloudGovernance = multiCloudGovernance;
        _tenantRepo = tenantRepo;
        _inventoryRepo = inventoryRepo;
        _platform = platform;
        _secretStore = secretStore;
        _logger = logger;
    }

    // ========================================================================
    // Anomalies
    // ========================================================================

    /// <summary>GET /api/anomalies — filterable anomaly queue.</summary>
    [Function("GetAnomalies")]
    public async Task<HttpResponseData> GetAnomalies(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "anomalies")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var status = Enum.TryParse<AnomalyStatus>(query["status"], true, out var st) ? st : (AnomalyStatus?)null;
        var severity = Enum.TryParse<AnomalySeverity>(query["severity"], true, out var sev) ? sev : (AnomalySeverity?)null;
        var kind = Enum.TryParse<AnomalyKind>(query["kind"], true, out var k) ? k : (AnomalyKind?)null;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var anomalies = await _anomalyRepo.GetAnomaliesAsync(tenantId, status, severity, kind, offset, limit);
        var estate = await ResolveEstateProviderAsync(tenantId);
        await PersistCorrectedProvidersAsync(tenantId, anomalies, estate);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new AnomaliesResponse
        {
            Anomalies = anomalies.Select(a => ToDto(a, estate)).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = anomalies.Count }
        });
        return response;
    }

    /// <summary>PATCH /api/anomalies/{id}/status — acknowledge/resolve/suppress.</summary>
    [Function("UpdateAnomalyStatus")]
    public async Task<HttpResponseData> UpdateAnomalyStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "anomalies/{id:long}/status")] HttpRequestData req,
        long id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<UpdateAnomalyStatusRequest>();

        if (body == null || !Enum.TryParse<AnomalyStatus>(body.Status, true, out var status))
            return await BadRequest(req, "INVALID_STATUS",
                $"Status must be one of: {string.Join(", ", Enum.GetNames<AnomalyStatus>())}");

        try
        {
            await _anomalyRepo.UpdateStatusAsync(tenantId, id, status, context.GetActor());
        }
        catch (KeyNotFoundException)
        {
            return await NotFound(req, $"Anomaly {id} not found.");
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { id, status = status.ToString() });
        return response;
    }

    // ========================================================================
    // Incidents
    // ========================================================================

    /// <summary>GET /api/incidents — incident queue with SLA state.</summary>
    [Function("GetIncidents")]
    public async Task<HttpResponseData> GetIncidents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incidents")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var status = Enum.TryParse<IncidentStatus>(query["status"], true, out var st) ? st : (IncidentStatus?)null;
        var severity = Enum.TryParse<AnomalySeverity>(query["severity"], true, out var sev) ? sev : (AnomalySeverity?)null;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var incidents = await _incidentRepo.GetIncidentsAsync(tenantId, status, severity, offset, limit);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new IncidentsResponse
        {
            Incidents = incidents.Select(i => ToDto(i, null)).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = incidents.Count }
        });
        return response;
    }

    /// <summary>GET /api/incidents/{id} — incident detail with full timeline.</summary>
    [Function("GetIncidentDetail")]
    public async Task<HttpResponseData> GetIncidentDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incidents/{id:long}")] HttpRequestData req,
        long id,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var incident = await _incidentRepo.GetByIdAsync(tenantId, id);
        if (incident == null)
            return await NotFound(req, $"Incident {id} not found.");

        var events = await _incidentRepo.GetEventsAsync(tenantId, id);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(incident, events));
        return response;
    }

    /// <summary>PATCH /api/incidents/{id} — status transitions, assignment, notes.</summary>
    [Function("UpdateIncident")]
    public async Task<HttpResponseData> UpdateIncident(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "incidents/{id:long}")] HttpRequestData req,
        long id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<UpdateIncidentRequest>();
        if (body == null)
            return await BadRequest(req, "INVALID_BODY", "Request body is required.");

        var incident = await _incidentRepo.GetByIdAsync(tenantId, id);
        if (incident == null)
            return await NotFound(req, $"Incident {id} not found.");

        var actor = context.GetActor();

        if (!string.IsNullOrEmpty(body.Status))
        {
            if (!Enum.TryParse<IncidentStatus>(body.Status, true, out var status))
                return await BadRequest(req, "INVALID_STATUS",
                    $"Status must be one of: {string.Join(", ", Enum.GetNames<IncidentStatus>())}");

            await _incidentRepo.UpdateStatusAsync(tenantId, id, status, actor);
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = id,
                TenantId = tenantId,
                EventType = "status_change",
                Message = $"Status changed to {status}",
                ActorName = actor
            });
        }

        if (body.AssignedTo != null)
        {
            await _incidentRepo.UpdateAssignmentAsync(tenantId, id,
                string.IsNullOrWhiteSpace(body.AssignedTo) ? null : body.AssignedTo);
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = id,
                TenantId = tenantId,
                EventType = "note",
                Message = string.IsNullOrWhiteSpace(body.AssignedTo)
                    ? "Incident unassigned"
                    : $"Assigned to {body.AssignedTo}",
                ActorName = actor
            });
        }

        if (!string.IsNullOrEmpty(body.Note))
        {
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = id,
                TenantId = tenantId,
                EventType = "note",
                Message = body.Note,
                ActorName = actor
            });
        }

        var updated = await _incidentRepo.GetByIdAsync(tenantId, id);
        var events = await _incidentRepo.GetEventsAsync(tenantId, id);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(updated!, events));
        return response;
    }

    // ========================================================================
    // Remediation actions + approval gate
    // ========================================================================

    /// <summary>GET /api/remediations — action history; ?status=PendingApproval is the approval queue.</summary>
    [Function("GetRemediations")]
    public async Task<HttpResponseData> GetRemediations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "remediations")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var status = Enum.TryParse<RemediationStatus>(query["status"], true, out var st) ? st : (RemediationStatus?)null;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var limit = int.TryParse(query["limit"], out var l) ? Math.Min(l, 500) : 100;

        var actions = await _remediationRepo.GetActionsAsync(tenantId, status, offset, limit);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new RemediationActionsResponse
        {
            Actions = actions.Select(ToDto).ToList(),
            Pagination = new PaginationDto { Offset = offset, Limit = limit, Total = actions.Count }
        });
        return response;
    }

    /// <summary>POST /api/remediations — manually propose a playbook action.</summary>
    [Function("ProposeRemediation")]
    public async Task<HttpResponseData> ProposeRemediation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "remediations")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<ProposeRemediationRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.PlaybookKey))
            return await BadRequest(req, "INVALID_REQUEST", "playbookKey is required.");

        try
        {
            var action = await _remediationService.ProposeAsync(
                tenantId, body.PlaybookKey, body.ResourceId,
                body.Title ?? string.Empty,
                string.IsNullOrWhiteSpace(body.Reason) ? $"Manually proposed by {context.GetActor()}" : body.Reason,
                body.ParametersJson,
                requestedBy: $"user:{context.GetActor()}");

            var response = req.CreateCorsResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(ToDto(action));
            return response;
        }
        catch (KeyNotFoundException ex)
        {
            return await NotFound(req, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return await BadRequest(req, "INVALID_OPERATION", ex.Message);
        }
    }

    /// <summary>POST /api/remediations/{id}/approve — approve and execute a gated action.</summary>
    [Function("ApproveRemediation")]
    public async Task<HttpResponseData> ApproveRemediation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "remediations/{id:long}/approve")] HttpRequestData req,
        long id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        try
        {
            await _remediationService.ApproveAsync(tenantId, id, context.GetActor());
            // Execute immediately so the approver sees the outcome; the timer
            // worker is the safety net for anything that slips through.
            var executed = await _remediationService.ExecuteAsync(tenantId, id);

            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ToDto(executed));
            return response;
        }
        catch (KeyNotFoundException ex)
        {
            return await NotFound(req, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return await BadRequest(req, "INVALID_OPERATION", ex.Message);
        }
    }

    /// <summary>POST /api/remediations/{id}/reject — reject a gated action.</summary>
    [Function("RejectRemediation")]
    public async Task<HttpResponseData> RejectRemediation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "remediations/{id:long}/reject")] HttpRequestData req,
        long id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<RejectRemediationRequest>();

        try
        {
            var action = await _remediationService.RejectAsync(tenantId, id, context.GetActor(), body?.Reason);
            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ToDto(action));
            return response;
        }
        catch (KeyNotFoundException ex)
        {
            return await NotFound(req, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return await BadRequest(req, "INVALID_OPERATION", ex.Message);
        }
    }

    /// <summary>GET /api/remediations/playbooks — the allow-listed playbook catalog.</summary>
    [Function("GetPlaybooks")]
    public async Task<HttpResponseData> GetPlaybooks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "remediations/playbooks")] HttpRequestData req,
        FunctionContext context)
    {
        var playbooks = await _remediationRepo.GetPlaybooksAsync();

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new PlaybooksResponse
        {
            Playbooks = playbooks.Select(p => new PlaybookDto
            {
                PlaybookKey = p.PlaybookKey,
                DisplayName = p.DisplayName,
                Description = p.Description,
                Provider = p.Provider.ToString(),
                Category = p.Category,
                ActionType = p.ActionType,
                RiskLevel = p.RiskLevel.ToString(),
                AlwaysRequiresApproval = p.AlwaysRequiresApproval,
                ParametersSchemaJson = p.ParametersSchemaJson
            }).ToList()
        });
        return response;
    }

    // ========================================================================
    // Cloud orgs + accounts (multi-cloud, provider-agnostic peers)
    // ========================================================================

    /// <summary>GET /api/cloud-orgs — cloud organizations with their accounts/projects.</summary>
    [Function("GetCloudOrgs")]
    public async Task<HttpResponseData> GetCloudOrgs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cloud-orgs")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var orgs = await _cloudOrgRepo.GetByTenantAsync(tenantId);
        var accounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);
        var byOrg = accounts.GroupBy(a => a.OrgId).ToDictionary(g => g.Key, g => g.ToList());

        // Latest-snapshot resource counts per member (subscription_id = account/project
        // external id, or an Azure subscription id), so every org card shows stats.
        var countsByProvider = new Dictionary<CloudProvider, IReadOnlyDictionary<string, int>>();
        foreach (var provider in orgs.Select(o => o.Provider).Distinct())
        {
            countsByProvider[provider] = await _inventoryRepo.GetResourceCountsBySubscriptionAsync(
                tenantId, provider.ToString().ToLowerInvariant());
        }

        // Azure orgs don't use cloud_accounts — a subscription isn't independently
        // credentialed like an AWS account/GCP project, so it's just a scope filter
        // under its connection's one credential (see azure_org_subscriptions). All
        // Azure connections in a workspace share one merged snapshot, so its
        // completion time doubles as "last collected" for every Azure org card.
        DateTime? latestAzureSnapshotAt = null;
        if (orgs.Any(o => o.Provider == CloudProvider.Azure))
            latestAzureSnapshotAt = (await _inventoryRepo.GetLatestSnapshotAsync(tenantId))?.CompletedAt;

        var orgDtos = new List<CloudOrgDto>();
        foreach (var o in orgs)
        {
            var counts = countsByProvider.TryGetValue(o.Provider, out var c) ? c : new Dictionary<string, int>();

            if (o.Provider == CloudProvider.Azure)
            {
                IReadOnlyList<string> subIds;
                if (o.SubscriptionScope == "specific")
                {
                    var pinned = await _cloudOrgRepo.GetAzureSubscriptionsAsync(tenantId, o.OrgId);
                    subIds = pinned.Select(p => p.SubscriptionId).ToList();
                }
                else
                {
                    // "all": show every subscription Resource Graph has actually found.
                    subIds = counts.Keys.ToList();
                }

                var azureAccountDtos = subIds.Select(subId => new CloudAccountDto
                {
                    AccountId = DeterministicGuid($"azure:{o.OrgId}:{subId}"),
                    OrgId = o.OrgId,
                    Provider = "Azure",
                    ExternalId = subId,
                    DisplayName = subId,
                    Status = "Connected",
                    ResourceCount = counts.TryGetValue(subId, out var n) ? n : 0,
                    LastInventoryAt = latestAzureSnapshotAt,
                    CreatedAt = o.CreatedAt
                }).ToList();

                orgDtos.Add(new CloudOrgDto
                {
                    OrgId = o.OrgId,
                    Provider = "Azure",
                    Name = o.Name,
                    ExternalId = o.ExternalId,
                    Status = o.Status.ToString(),
                    CreatedAt = o.CreatedAt,
                    AccountCount = azureAccountDtos.Count,
                    ResourceCount = azureAccountDtos.Sum(a => a.ResourceCount),
                    LastInventoryAt = latestAzureSnapshotAt,
                    Accounts = azureAccountDtos,
                    SubscriptionScope = o.SubscriptionScope,
                    OnboardingMethod = o.OnboardingMethod,
                    HasCredentials = string.Equals(o.OnboardingMethod, "app_registration", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(o.CredentialSecretName)
                });
                continue;
            }

            var members = byOrg.TryGetValue(o.OrgId, out var list) ? list : new List<CloudAccount>();
            var memberDtos = members
                .Select(a => ToDto(a, counts.TryGetValue(a.ExternalId, out var n) ? n : 0))
                .ToList();

            orgDtos.Add(new CloudOrgDto
            {
                OrgId = o.OrgId,
                Provider = o.Provider.ToString(),
                Name = o.Name,
                ExternalId = o.ExternalId,
                Status = o.Status.ToString(),
                CreatedAt = o.CreatedAt,
                AccountCount = memberDtos.Count,
                ResourceCount = memberDtos.Sum(a => a.ResourceCount),
                LastInventoryAt = memberDtos
                    .Where(a => a.LastInventoryAt.HasValue)
                    .Select(a => a.LastInventoryAt!.Value)
                    .DefaultIfEmpty()
                    .Max() is var max && max != default ? max : (DateTime?)null,
                Accounts = memberDtos,
                HasCredentials = false
            });
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new CloudOrgsResponse { Orgs = orgDtos });
        return response;
    }

    /// <summary>Stable pseudo-id for a display-only Azure subscription row (no real cloud_accounts row backs it).</summary>
    private static Guid DeterministicGuid(string input)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }

    /// <summary>POST /api/cloud-orgs — create a cloud organization (Azure/AWS/GCP peer grouping).</summary>
    [Function("CreateCloudOrg")]
    public async Task<HttpResponseData> CreateCloudOrg(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cloud-orgs")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<CreateCloudOrgRequest>();

        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await BadRequest(req, "INVALID_REQUEST", "provider and name are required.");
        if (!Enum.TryParse<CloudProvider>(body.Provider, true, out var provider))
            return await BadRequest(req, "INVALID_PROVIDER", "Provider must be azure, aws, or gcp.");

        var org = await _cloudOrgRepo.CreateAsync(new CloudOrg
        {
            OrgId = Guid.NewGuid(),
            TenantId = tenantId,
            Provider = provider,
            Name = body.Name.Trim(),
            ExternalId = string.IsNullOrWhiteSpace(body.ExternalId) ? null : body.ExternalId.Trim(),
            CreatedBy = context.GetActor()
        });

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new CloudOrgDto
        {
            OrgId = org.OrgId,
            Provider = org.Provider.ToString(),
            Name = org.Name,
            ExternalId = org.ExternalId,
            Status = org.Status.ToString(),
            CreatedAt = org.CreatedAt
        });
        return response;
    }

    /// <summary>
    /// PATCH /api/cloud-orgs/{orgId}/status — suspend / reactivate an AWS or GCP
    /// organization, parity with the Azure tenant status lifecycle.
    /// </summary>
    [Function("UpdateCloudOrgStatus")]
    public async Task<HttpResponseData> UpdateCloudOrgStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "cloud-orgs/{orgId:guid}/status")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<UpdateCloudOrgStatusRequest>();
        if (body == null || !Enum.TryParse<CloudOrgStatus>(body.Status, true, out var status))
            return await BadRequest(req, "INVALID_STATUS", "status must be Active, Degraded, or Disconnected.");

        var org = await _cloudOrgRepo.GetByIdAsync(tenantId, orgId);
        if (org == null)
            return await NotFound(req, $"Cloud org {orgId} not found.");

        await _cloudOrgRepo.UpdateStatusAsync(tenantId, orgId, status);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { orgId, status = status.ToString() });
        return response;
    }

    /// <summary>GET /api/cloud-accounts — flat list of all linked accounts/projects.</summary>
    [Function("GetCloudAccounts")]
    public async Task<HttpResponseData> GetCloudAccounts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cloud-accounts")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();
        var accounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new CloudAccountsResponse
        {
            Accounts = accounts.Select(a => ToDto(a)).ToList()
        });
        return response;
    }

    /// <summary>
    /// POST /api/cloud-accounts — add an AWS account or GCP project to a cloud org.
    /// Credentials go straight to the secret store; SQL only stores the secret name.
    /// The account inherits its provider from its org.
    /// </summary>
    [Function("LinkCloudAccount")]
    public async Task<HttpResponseData> LinkCloudAccount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cloud-accounts")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();
        var body = await req.ReadFromJsonAsync<LinkCloudAccountRequest>();

        if (body == null || body.OrgId == Guid.Empty || string.IsNullOrWhiteSpace(body.ExternalId) || string.IsNullOrWhiteSpace(body.DisplayName))
            return await BadRequest(req, "INVALID_REQUEST", "orgId, externalId, and displayName are required.");

        var org = await _cloudOrgRepo.GetByIdAsync(tenantId, body.OrgId);
        if (org == null)
            return await NotFound(req, $"Cloud org {body.OrgId} not found.");
        if (org.Provider == CloudProvider.Azure)
            return await BadRequest(req, "INVALID_PROVIDER",
                "Azure subscriptions are pinned when connecting the Azure tenant (POST /organizations/{orgId}/azure), not as cloud accounts.");

        var provider = org.Provider;
        var account = new CloudAccount
        {
            AccountId = Guid.NewGuid(),
            TenantId = tenantId,
            OrgId = org.OrgId,
            Provider = provider,
            ExternalId = body.ExternalId,
            DisplayName = body.DisplayName,
            Regions = body.Regions,
            CreatedBy = context.GetActor()
        };

        if (string.IsNullOrWhiteSpace(body.CredentialJson))
            return await BadRequest(req, "INVALID_REQUEST",
                "credentialJson is required when linking an AWS account or GCP project.");
        if (_secretStore == null)
            return await BadRequest(req, "SECRET_STORE_REQUIRED",
                "A secret store is not configured; cannot store cloud credentials.");

        account.CredentialSecretName = $"cloudaccount-{account.AccountId}";
        await _secretStore.SetSecretAsync(account.CredentialSecretName, body.CredentialJson);

        await _cloudAccountRepo.CreateAsync(account);

        // Immediate connectivity check so a bad credential is visible at link time
        try
        {
            var adapter = _adapterFactory.GetAdapter(provider);
            var (healthy, error) = await adapter.TestConnectivityAsync(account);
            await _cloudAccountRepo.UpdateStatusAsync(account.AccountId,
                healthy ? CloudAccountStatus.Connected : CloudAccountStatus.Degraded, error);
            account.Status = healthy ? CloudAccountStatus.Connected : CloudAccountStatus.Degraded;
            account.LastError = error;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connectivity test failed for new {Provider} account {ExternalId}", provider, body.ExternalId);
            await _cloudAccountRepo.UpdateStatusAsync(account.AccountId, CloudAccountStatus.Degraded, ex.Message);
        }

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(ToDto(account));
        return response;
    }

    /// <summary>
    /// POST /api/cloud-accounts/{id}/collect — on-demand inventory collection for
    /// one AWS account / GCP project (parity with the Azure snapshot trigger).
    /// Blocked in Development so demo/seed clouds are never contacted.
    /// </summary>
    [Function("CollectCloudAccount")]
    public async Task<HttpResponseData> CollectCloudAccount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cloud-accounts/{id:guid}/collect")] HttpRequestData req,
        Guid id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        if (!_platform.IsProduction)
            return await BadRequest(req, "DEVELOPMENT_MODE",
                $"Inventory collection is disabled while the instance environment is {_platform.Environment}. " +
                "Set Platform:Environment=Production to collect against real clouds.");

        var account = await _cloudAccountRepo.GetByIdAsync(tenantId, id);
        if (account == null)
            return await NotFound(req, $"Cloud account {id} not found.");

        try
        {
            var count = await _multiCloudInventory.SyncAccountAsync(account);
            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { accountId = id, resourcesCollected = count });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "On-demand collection failed for cloud account {AccountId}", id);
            return await BadRequest(req, "COLLECTION_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// POST /api/cloud-orgs/governance/sync — on-demand AWS/GCP Security · Governance · Policy
    /// sync for the current workspace (parity with Azure Advisor/Policy/Defender timers).
    /// Also runs after each multi-cloud Collect.
    /// </summary>
    [Function("SyncMultiCloudGovernance")]
    public async Task<HttpResponseData> SyncMultiCloudGovernance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cloud-orgs/governance/sync")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        if (!_platform.IsProduction)
            return await BadRequest(req, "DEVELOPMENT_MODE",
                $"Governance sync is disabled while the instance environment is {_platform.Environment}. " +
                "Set Platform:Environment=Production to sync against real clouds.");

        try
        {
            var snap = await _multiCloudGovernance.SyncTenantAsync(tenantId);
            var response = req.CreateCorsResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                tenantId,
                securityFindings = snap.SecurityFindings.Count,
                recommendations = snap.Recommendations.Count,
                policyRecords = snap.PolicyRecords.Count,
                notes = snap.SourceNotes
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "On-demand multi-cloud governance sync failed for tenant {TenantId}", tenantId);
            return await BadRequest(req, "GOVERNANCE_SYNC_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// DELETE /api/cloud-accounts/{id} — unlink an AWS account / GCP project.
    /// Removes the SQL row and best-effort deletes the secret-store credential.
    /// </summary>
    [Function("DeleteCloudAccount")]
    public async Task<HttpResponseData> DeleteCloudAccount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "cloud-accounts/{id:guid}")] HttpRequestData req,
        Guid id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        var account = await _cloudAccountRepo.GetByIdAsync(tenantId, id);
        if (account == null)
            return await NotFound(req, $"Cloud account {id} not found.");

        await _cloudAccountRepo.DeleteAsync(tenantId, id);
        await TryDeleteSecretAsync(account.CredentialSecretName);

        _logger.LogInformation("Deleted {Provider} account {ExternalId} ({AccountId}) from workspace {TenantId}",
            account.Provider, account.ExternalId, id, tenantId);

        return req.CreateCorsResponse(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// PUT /api/cloud-accounts/{id}/credentials — rotate AWS/GCP credentials in the secret store.
    /// </summary>
    [Function("UpdateCloudAccountCredentials")]
    public async Task<HttpResponseData> UpdateCloudAccountCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "cloud-accounts/{id:guid}/credentials")] HttpRequestData req,
        Guid id,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        var body = await req.ReadFromJsonAsync<UpdateCloudAccountCredentialsRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.CredentialJson))
            return await BadRequest(req, "INVALID_REQUEST", "credentialJson is required.");
        if (_secretStore == null)
            return await BadRequest(req, "SECRET_STORE_REQUIRED",
                "A secret store is not configured; cannot store cloud credentials.");

        var account = await _cloudAccountRepo.GetByIdAsync(tenantId, id);
        if (account == null)
            return await NotFound(req, $"Cloud account {id} not found.");

        var secretName = string.IsNullOrWhiteSpace(account.CredentialSecretName)
            ? $"cloudaccount-{account.AccountId}"
            : account.CredentialSecretName;

        await _secretStore.SetSecretAsync(secretName, body.CredentialJson);
        if (!string.Equals(account.CredentialSecretName, secretName, StringComparison.Ordinal))
            await _cloudAccountRepo.UpdateCredentialSecretNameAsync(tenantId, id, secretName);
        else
            // Clear last_error / restore Connected so a rotated key can recover a Degraded account.
            await _cloudAccountRepo.UpdateCredentialSecretNameAsync(tenantId, id, secretName);

        account.CredentialSecretName = secretName;
        try
        {
            var adapter = _adapterFactory.GetAdapter(account.Provider);
            var (healthy, error) = await adapter.TestConnectivityAsync(account);
            await _cloudAccountRepo.UpdateStatusAsync(account.AccountId,
                healthy ? CloudAccountStatus.Connected : CloudAccountStatus.Degraded, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connectivity test failed after credential rotation for account {AccountId}", id);
            await _cloudAccountRepo.UpdateStatusAsync(account.AccountId, CloudAccountStatus.Degraded, ex.Message);
        }

        var refreshed = await _cloudAccountRepo.GetByIdAsync(tenantId, id);
        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(refreshed ?? account));
        return response;
    }

    /// <summary>
    /// DELETE /api/cloud-orgs/{orgId} — remove an Azure tenant connection or AWS/GCP organization.
    /// Cascades: member accounts (and their secrets), azure_org_subscriptions, then the org row.
    /// </summary>
    [Function("DeleteCloudOrg")]
    public async Task<HttpResponseData> DeleteCloudOrg(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "cloud-orgs/{orgId:guid}")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        var org = await _cloudOrgRepo.GetByIdAsync(tenantId, orgId);
        if (org == null)
            return await NotFound(req, $"Cloud org {orgId} not found.");

        var members = await _cloudAccountRepo.GetByOrgAsync(tenantId, orgId);
        foreach (var account in members)
        {
            await _cloudAccountRepo.DeleteAsync(tenantId, account.AccountId);
            await TryDeleteSecretAsync(account.CredentialSecretName);
        }

        await _cloudOrgRepo.DeleteAsync(tenantId, orgId);
        await TryDeleteSecretAsync(org.CredentialSecretName);

        _logger.LogInformation("Deleted {Provider} cloud org '{Name}' ({OrgId}) and {Count} member account(s) from workspace {TenantId}",
            org.Provider, org.Name, orgId, members.Count, tenantId);

        return req.CreateCorsResponse(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// PUT /api/cloud-orgs/{orgId}/credentials — rotate Azure app-registration credentials
    /// for a cloud_orgs connection. Lighthouse connections have no secret to rotate.
    /// </summary>
    [Function("UpdateCloudOrgCredentials")]
    public async Task<HttpResponseData> UpdateCloudOrgCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "cloud-orgs/{orgId:guid}/credentials")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;
        var tenantId = context.GetTenantId();

        var body = await req.ReadFromJsonAsync<UpdateCloudOrgCredentialsRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
            return await BadRequest(req, "INVALID_REQUEST", "clientId and clientSecret are required.");
        if (_secretStore == null)
            return await BadRequest(req, "SECRET_STORE_REQUIRED",
                "A secret store is not configured; cannot store cloud credentials.");

        var org = await _cloudOrgRepo.GetByIdAsync(tenantId, orgId);
        if (org == null)
            return await NotFound(req, $"Cloud org {orgId} not found.");
        if (org.Provider != CloudProvider.Azure)
            return await BadRequest(req, "INVALID_PROVIDER",
                "Credential rotation on cloud-orgs is for Azure app registrations. Use PUT /cloud-accounts/{id}/credentials for AWS/GCP.");
        if (!string.Equals(org.OnboardingMethod, "app_registration", StringComparison.OrdinalIgnoreCase))
            return await BadRequest(req, "INVALID_METHOD",
                "Only app_registration Azure connections store client credentials. Lighthouse uses delegated access.");

        var secretName = string.IsNullOrWhiteSpace(org.CredentialSecretName)
            ? $"cloudorg-{org.OrgId}-creds"
            : org.CredentialSecretName;

        await _secretStore.SetSecretAsync(secretName, JsonSerializer.Serialize(new
        {
            clientId = body.ClientId.Trim(),
            clientSecret = body.ClientSecret
        }));
        await _cloudOrgRepo.UpdateCredentialSecretNameAsync(tenantId, orgId, secretName);

        // Keep workspace shell secret in sync when this connection was the primary fill.
        var workspace = await _tenantRepo.GetByIdAsync(tenantId);
        if (workspace != null &&
            (string.IsNullOrWhiteSpace(workspace.SecretName)
             || string.Equals(workspace.SecretName, org.CredentialSecretName, StringComparison.Ordinal)
             || string.Equals(workspace.AzureTenantId, org.ExternalId, StringComparison.OrdinalIgnoreCase)))
        {
            workspace.SecretName = secretName;
            await _tenantRepo.UpdateAsync(workspace);
        }

        _logger.LogInformation("Rotated credentials for Azure cloud org {OrgId} in workspace {TenantId}", orgId, tenantId);

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { orgId, updated = true });
        return response;
    }

    private async Task TryDeleteSecretAsync(string? secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName) || _secretStore == null) return;
        try
        {
            await _secretStore.DeleteSecretAsync(secretName);
        }
        catch (Exception ex)
        {
            // Don't fail the delete path if secret cleanup fails (already rotated / store down).
            _logger.LogWarning(ex, "Failed to delete secret {SecretName} during cloud cleanup", secretName);
        }
    }

    // ========================================================================
    // Operations summary (AIOps dashboard)
    // ========================================================================

    /// <summary>GET /api/operations/summary — single-call payload for the ops dashboard.</summary>
    [Function("GetOpsSummary")]
    public async Task<HttpResponseData> GetOpsSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "operations/summary")] HttpRequestData req,
        FunctionContext context)
    {
        var tenantId = context.GetTenantId();

        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        var openAnomalies = await _anomalyRepo.GetAnomaliesAsync(tenantId, AnomalyStatus.Open, limit: 200);
        var openIncidents = await _incidentRepo.GetIncidentsAsync(tenantId, limit: 200);
        var pendingApprovals = await _remediationRepo.GetPendingApprovalCountAsync(tenantId);
        var recentActions = await _remediationRepo.GetActionsAsync(tenantId, limit: 100);
        var cloudAccounts = await _cloudAccountRepo.GetByTenantAsync(tenantId);
        var estate = await ResolveEstateProviderAsync(tenantId, cloudAccounts);
        await PersistCorrectedProvidersAsync(tenantId, openAnomalies, estate);

        var now = DateTime.UtcNow;
        var week = now.AddDays(-7);
        var activeIncidents = openIncidents
            .Where(i => i.Status is IncidentStatus.Open or IncidentStatus.Acknowledged or IncidentStatus.Mitigated)
            .ToList();
        var resolvedWithTimes = openIncidents
            .Where(i => i.ResolvedAt.HasValue)
            .Select(i => (i.ResolvedAt!.Value - i.CreatedAt).TotalHours)
            .ToList();
        var recentWeekActions = recentActions.Where(a => a.CreatedAt >= week).ToList();

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new OpsSummaryResponse
        {
            TenantId = tenantId,
            OpenAnomalies = openAnomalies.Count,
            CriticalAnomalies = openAnomalies.Count(a => a.Severity == AnomalySeverity.Critical),
            OpenIncidents = activeIncidents.Count,
            SlaBreachedIncidents = activeIncidents.Count(i => i.SlaDueAt.HasValue && i.SlaDueAt < now),
            PendingApprovals = pendingApprovals,
            RemediationsLast7d = recentWeekActions.Count(a => a.Status == RemediationStatus.Succeeded),
            AutoRemediationsLast7d = recentWeekActions.Count(a => a.Status == RemediationStatus.Succeeded && a.ApprovalMode == "auto"),
            MeanTimeToResolveHours = resolvedWithTimes.Count > 0 ? Math.Round(resolvedWithTimes.Average(), 1) : null,
            AutoRemediationMode = tenant?.AutoRemediationMode.ToString() ?? "Gated",
            MonitoringEnabled = tenant?.AiOpsMonitoringEnabled ?? true,
            RecentAnomalies = openAnomalies.Take(10).Select(a => ToDto(a, estate)).ToList(),
            RecentIncidents = activeIncidents.Take(10).Select(i => ToDto(i, null)).ToList(),
            RecentRemediations = recentActions.Take(10).Select(ToDto).ToList(),
            CloudAccounts = cloudAccounts.Select(a => ToDto(a)).ToList()
        });
        return response;
    }

    // ========================================================================
    // Mapping + helpers
    // ========================================================================

    private static AnomalyDto ToDto(Anomaly a, CloudProvider? estateDefault = null)
    {
        var provider = CloudProviderInference.Correct(a, estateDefault);
        return new AnomalyDto
        {
            Id = a.Id,
            Kind = a.Kind.ToString(),
            Severity = a.Severity.ToString(),
            Status = a.Status.ToString(),
            Provider = provider.ToString(),
            Title = a.Title,
            Description = a.Description,
            ResourceId = a.ResourceId,
            MetricName = a.MetricName,
            ObservedValue = a.ObservedValue,
            BaselineMean = a.BaselineMean,
            Score = a.Score,
            DetectedAt = a.DetectedAt,
            LastSeenAt = a.LastSeenAt,
            IncidentId = a.IncidentId
        };
    }

    /// <summary>
    /// Dominant provider for this workspace from linked cloud accounts + inventory sample.
    /// Used so GCP-only estates don't keep Azure badges on tenant-wide anomalies.
    /// </summary>
    private async Task<CloudProvider?> ResolveEstateProviderAsync(
        Guid tenantId, IReadOnlyList<CloudAccount>? accounts = null)
    {
        accounts ??= await _cloudAccountRepo.GetByTenantAsync(tenantId);
        var active = accounts.Where(a => a.Status != CloudAccountStatus.Disconnected).ToList();
        if (active.Count > 0)
        {
            var providers = active.Select(a => a.Provider).Distinct().ToList();
            // Homogeneous multi-cloud estate (all GCP or all AWS) → that provider.
            if (providers.Count == 1)
                return providers[0];
            // No Azure accounts linked → majority among AWS/GCP.
            var nonAzure = active.Where(a => a.Provider != CloudProvider.Azure).ToList();
            if (nonAzure.Count == active.Count && nonAzure.Count > 0)
                return nonAzure.GroupBy(a => a.Provider).OrderByDescending(g => g.Count()).First().Key;
        }

        try
        {
            var sample = await _inventoryRepo.GetResourcesAsync(tenantId, limit: 80);
            if (sample.Count == 0) return null;
            return sample
                .Select(r => CloudProviderInference.FromResource(r.ResourceId, r.Provider))
                .GroupBy(p => p)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persist corrected providers so subsequent reads and UI stay accurate.</summary>
    private async Task PersistCorrectedProvidersAsync(
        Guid tenantId, IReadOnlyList<Anomaly> anomalies, CloudProvider? estateDefault)
    {
        foreach (var a in anomalies)
        {
            var corrected = CloudProviderInference.Correct(a, estateDefault);
            if (corrected == a.Provider) continue;
            try
            {
                await _anomalyRepo.UpdateProviderAsync(tenantId, a.Id, corrected);
                a.Provider = corrected;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist corrected provider for anomaly {Id}", a.Id);
            }
        }
    }

    private static IncidentDto ToDto(Incident i, IReadOnlyList<IncidentEvent>? events) => new()
    {
        Id = i.Id,
        Title = i.Title,
        Severity = i.Severity.ToString(),
        Status = i.Status.ToString(),
        Source = i.Source,
        SummaryMarkdown = i.SummaryMarkdown,
        AssignedTo = i.AssignedTo,
        CreatedAt = i.CreatedAt,
        AcknowledgedAt = i.AcknowledgedAt,
        ResolvedAt = i.ResolvedAt,
        SlaDueAt = i.SlaDueAt,
        SlaBreached = i.SlaDueAt.HasValue && i.SlaDueAt < DateTime.UtcNow &&
                      i.Status is IncidentStatus.Open or IncidentStatus.Acknowledged or IncidentStatus.Mitigated,
        AnomalyCount = i.AnomalyCount,
        RemediationCount = i.RemediationCount,
        Events = events?.Select(e => new IncidentEventDto
        {
            OccurredAt = e.OccurredAt,
            EventType = e.EventType,
            Message = e.Message,
            ActorName = e.ActorName
        }).ToList()
    };

    private static RemediationActionDto ToDto(RemediationAction a) => new()
    {
        Id = a.Id,
        PlaybookKey = a.PlaybookKey,
        Provider = a.Provider.ToString(),
        ResourceId = a.ResourceId,
        Title = a.Title,
        Reason = a.Reason,
        ParametersJson = a.ParametersJson,
        Status = a.Status.ToString(),
        RiskLevel = a.RiskLevel.ToString(),
        RequestedBy = a.RequestedBy,
        AnomalyId = a.AnomalyId,
        IncidentId = a.IncidentId,
        ApprovalMode = a.ApprovalMode,
        ApprovedBy = a.ApprovedBy,
        ApprovedAt = a.ApprovedAt,
        RejectedReason = a.RejectedReason,
        ExecutedAt = a.ExecutedAt,
        CompletedAt = a.CompletedAt,
        ResultJson = a.ResultJson,
        ErrorMessage = a.ErrorMessage,
        CreatedAt = a.CreatedAt,
        ExpiresAt = a.ExpiresAt
    };

    private static CloudAccountDto ToDto(CloudAccount a) => ToDto(a, 0);

    private static CloudAccountDto ToDto(CloudAccount a, int resourceCount) => new()
    {
        AccountId = a.AccountId,
        OrgId = a.OrgId,
        Provider = a.Provider.ToString(),
        ExternalId = a.ExternalId,
        DisplayName = a.DisplayName,
        Status = a.Status.ToString(),
        Regions = a.Regions,
        LastInventoryAt = a.LastInventoryAt,
        LastError = a.LastError,
        ResourceCount = resourceCount,
        CreatedAt = a.CreatedAt
    };

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = "NOT_FOUND", Message = message });
        return response;
    }
}
