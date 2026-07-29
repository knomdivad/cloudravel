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
/// HTTP endpoints for Organizations — the in-app workspace that owns clouds.
///
/// An Organization (org_id = the RLS workspace boundary) is the thing an operator
/// selects in the sidebar; clouds are added UNDER an org and never create one:
///   * Azure tenant  → POST /api/organizations/{orgId}/azure  (a cloud_orgs peer,
///                     callable repeatedly — an org can hold multiple Azure
///                     tenants, exactly like multiple AWS/GCP orgs)
///   * AWS/GCP orgs  → POST /api/cloud-orgs                    (scoped to org_id)
///   * accounts      → POST /api/cloud-accounts               (scoped to org_id)
/// </summary>
public sealed class OrganizationFunctions
{
    private readonly IOrganizationRepository _orgRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ICloudOrgRepository _cloudOrgRepo;
    private readonly IUserRepository _userRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<OrganizationFunctions> _logger;

    public OrganizationFunctions(
        IOrganizationRepository orgRepo,
        ITenantRepository tenantRepo,
        ICloudOrgRepository cloudOrgRepo,
        IUserRepository userRepo,
        ILogger<OrganizationFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _orgRepo = orgRepo;
        _tenantRepo = tenantRepo;
        _cloudOrgRepo = cloudOrgRepo;
        _userRepo = userRepo;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/organizations — every workspace the operator can pick, each with a
    /// rollup of the clouds it owns. Tenant header optional (like /tenants listing).
    /// </summary>
    [Function("ListOrganizations")]
    public async Task<HttpResponseData> ListOrganizations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "organizations")] HttpRequestData req,
        FunctionContext context)
    {
        var orgs = await _orgRepo.GetAllAsync();

        // System admins see every workspace (acting as org_admin). Everyone else
        // sees only the workspaces they've been granted access to, tagged with
        // their role there — the frontend uses callerRole to gate admin controls.
        var userId = context.GetUserId();
        var isSystemAdmin = context.IsSystemAdmin();
        IReadOnlyDictionary<Guid, string> access = isSystemAdmin || userId == null
            ? new Dictionary<Guid, string>()
            : (await _userRepo.GetUserTenantAccessAsync(userId.Value))
                .GroupBy(a => a.TenantId)
                .ToDictionary(g => g.Key, g => g.First().Role);

        var dtos = new List<OrganizationDto>();
        foreach (var o in orgs)
        {
            string callerRole;
            if (isSystemAdmin) callerRole = OrgRole.OrgAdmin;
            else if (access.TryGetValue(o.OrgId, out var r)) callerRole = r;
            else continue; // no access → omit from this user's list

            var dto = await BuildDto(o);
            dto.CallerRole = callerRole;
            dtos.Add(dto);
        }

        var response = req.CreateCorsResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new OrganizationListResponse { Organizations = dtos });
        return response;
    }

    /// <summary>POST /api/organizations — create a new workspace (no clouds yet).</summary>
    [Function("CreateOrganization")]
    public async Task<HttpResponseData> CreateOrganization(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "organizations")] HttpRequestData req,
        FunctionContext context)
    {
        // Only system administrators may create organizations.
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var body = await req.ReadFromJsonAsync<CreateOrganizationRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await BadRequest(req, "INVALID_REQUEST", "name is required.");

        var environment = string.Equals(body.Environment, "Production", StringComparison.OrdinalIgnoreCase)
            ? "Production"
            : "Development";

        var org = await _orgRepo.CreateAsync(new Organization
        {
            OrgId = Guid.NewGuid(),
            Name = body.Name.Trim(),
            Environment = environment,
            Status = "active",
            CreatedBy = context.GetActor()
        });

        _logger.LogInformation("Created organization {OrgId} ({Name})", org.OrgId, org.Name);

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(await BuildDto(org));
        return response;
    }

    /// <summary>
    /// POST /api/organizations/{orgId}/azure — connect an Azure tenant to an org.
    /// Callable repeatedly: an Organization can hold multiple Azure tenants as
    /// peers (each becomes its own cloud_orgs connection), the same way it can
    /// hold multiple AWS/GCP organizations. Never creates a new Organization.
    /// </summary>
    [Function("ConnectAzureTenant")]
    public async Task<HttpResponseData> ConnectAzureTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "organizations/{orgId:guid}/azure")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.CloudAdmin);
        if (forbid != null) return forbid;

        var mismatch = await context.RequirePathTenantMatchAsync(req, orgId);
        if (mismatch != null) return mismatch;

        var org = await _orgRepo.GetByIdAsync(orgId);
        if (org == null)
            return await NotFound(req, $"Organization {orgId} not found.");

        var body = await req.ReadFromJsonAsync<ConnectAzureTenantRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.AzureTenantId))
            return await BadRequest(req, "INVALID_REQUEST", "displayName and azureTenantId are required.");

        if (!Enum.TryParse<OnboardingMethod>(body.OnboardingMethod.Replace("_", ""), true, out var method))
            return await BadRequest(req, "INVALID_METHOD", "onboardingMethod must be 'lighthouse' or 'app_registration'.");

        var azureTenantId = body.AzureTenantId.Trim();

        // Connecting the SAME Azure AD tenant twice would double-collect its
        // subscriptions — block that. A DIFFERENT Azure tenant is always welcome.
        var existingConnections = await _cloudOrgRepo.GetByTenantAsync(orgId);
        if (existingConnections.Any(c => c.Provider == CloudProvider.Azure
            && string.Equals(c.ExternalId, azureTenantId, StringComparison.OrdinalIgnoreCase)))
        {
            return await Conflict(req, "AZURE_ALREADY_CONNECTED",
                "This Azure tenant is already connected to this organization.");
        }

        var subscriptionScope = body.SubscriptionIds is { Count: > 0 } ? "specific" : "all";
        var newOrgId = Guid.NewGuid();
        string? credentialSecretName = method == OnboardingMethod.AppRegistration && !string.IsNullOrEmpty(body.ClientId)
            ? $"cloudorg-{newOrgId}-creds"
            : null;

        // App Registration credentials require a secret store — fail closed.
        if (credentialSecretName != null && !string.IsNullOrEmpty(body.ClientSecret) && _secretStore == null)
            return await BadRequest(req, "SECRET_STORE_REQUIRED",
                "A secret store is required to store App Registration credentials.");

        // Always create the peer cloud_orgs connection — the inventory collector
        // loops these, so this is the single source of truth for N Azure tenants.
        var azureOrg = await _cloudOrgRepo.CreateAsync(new CloudOrg
        {
            OrgId = newOrgId,
            TenantId = orgId,
            Provider = CloudProvider.Azure,
            Name = body.DisplayName.Trim(),
            ExternalId = azureTenantId,
            Status = CloudOrgStatus.Active,
            OnboardingMethod = method == OnboardingMethod.AppRegistration ? "app_registration" : "lighthouse",
            CredentialSecretName = credentialSecretName,
            LighthouseDelegationId = body.LighthouseDelegationId,
            SubscriptionScope = subscriptionScope,
            CreatedBy = context.GetActor()
        });

        if (body.SubscriptionIds is { Count: > 0 })
            await _cloudOrgRepo.AddAzureSubscriptionsAsync(orgId, newOrgId, body.SubscriptionIds);

        if (credentialSecretName != null && !string.IsNullOrEmpty(body.ClientSecret) && _secretStore != null)
        {
            await _secretStore.SetSecretAsync(credentialSecretName, JsonSerializer.Serialize(new
            {
                clientId = body.ClientId,
                clientSecret = body.ClientSecret
            }));
        }

        // The FIRST Azure connection for this workspace also materializes the
        // legacy `tenants` row: workspace-level policy (AutoRemediationMode,
        // AiOpsMonitoringEnabled, snapshot/change-poll cadence) lives there, and
        // the scheduler timers (CollectInventoryTimer, PollChangesTimer, ...)
        // enumerate `tenants` to know which workspaces to run at all. A 2nd+
        // connection skips this — tenant_id is the workspace's PK, already taken.
        var existingWorkspaceTenant = await _tenantRepo.GetByIdAsync(orgId);
        if (existingWorkspaceTenant == null)
        {
            var tenant = new Tenant
            {
                TenantId = orgId,
                DisplayName = body.DisplayName.Trim(),
                AzureTenantId = azureTenantId,
                OnboardingMethod = method,
                Status = TenantStatus.Active,
                LighthouseDelegationId = body.LighthouseDelegationId,
                SecretName = credentialSecretName
            };
            var createdBy = context.GetUserId() ?? Guid.Empty;
            await _tenantRepo.CreateAsync(tenant, createdBy);

            if (body.SubscriptionIds is { Count: > 0 })
                await _tenantRepo.AddSubscriptionsAsync(orgId, body.SubscriptionIds);
        }

        _logger.LogInformation("Connected Azure tenant {AzureTenantId} ({OnboardingMethod}) to organization {OrgId}",
            azureTenantId, method, orgId);

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(await BuildDto(org));
        return response;
    }

    /// <summary>Rollup: how many Azure/AWS/GCP connections the org owns.</summary>
    private async Task<OrganizationDto> BuildDto(Organization o)
    {
        var cloudOrgs = await _cloudOrgRepo.GetByTenantAsync(o.OrgId);
        var azureOrgs = cloudOrgs.Where(c => c.Provider == CloudProvider.Azure).ToList();
        var aws = cloudOrgs.Count(c => c.Provider == CloudProvider.Aws);
        var gcp = cloudOrgs.Count(c => c.Provider == CloudProvider.Gcp);

        return new OrganizationDto
        {
            OrgId = o.OrgId,
            Name = o.Name,
            Environment = o.Environment,
            Status = o.Status,
            CreatedAt = o.CreatedAt,
            AzureConnected = azureOrgs.Count > 0,
            AzureTenantName = azureOrgs.Count == 1 ? azureOrgs[0].Name : azureOrgs.Count > 1 ? $"{azureOrgs.Count} Azure tenants" : null,
            AzureOrgCount = azureOrgs.Count,
            AwsOrgCount = aws,
            GcpOrgCount = gcp,
            CloudCount = azureOrgs.Count + aws + gcp
        };
    }

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

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Conflict);
        await response.WriteAsJsonAsync(new ErrorResponse { Code = code, Message = message });
        return response;
    }
}
