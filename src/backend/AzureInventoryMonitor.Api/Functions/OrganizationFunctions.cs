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
/// HTTP endpoints for Organizations — the in-app workspace that owns clouds.
///
/// An Organization (org_id = the RLS workspace boundary) is the thing an operator
/// selects in the sidebar; clouds are added UNDER an org and never create one:
///   * Azure tenant  → POST /api/organizations/{orgId}/azure  (tenants row @ org_id)
///   * AWS/GCP orgs  → POST /api/cloud-orgs                    (scoped to org_id)
///   * accounts      → POST /api/cloud-accounts               (scoped to org_id)
/// </summary>
public sealed class OrganizationFunctions
{
    private readonly IOrganizationRepository _orgRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ICloudOrgRepository _cloudOrgRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<OrganizationFunctions> _logger;

    public OrganizationFunctions(
        IOrganizationRepository orgRepo,
        ITenantRepository tenantRepo,
        ICloudOrgRepository cloudOrgRepo,
        ILogger<OrganizationFunctions> logger,
        ISecretStore? secretStore = null)
    {
        _orgRepo = orgRepo;
        _tenantRepo = tenantRepo;
        _cloudOrgRepo = cloudOrgRepo;
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
        var dtos = new List<OrganizationDto>();
        foreach (var o in orgs)
        {
            dtos.Add(await BuildDto(o));
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
            CreatedBy = GetActor(req)
        });

        _logger.LogInformation("Created organization {OrgId} ({Name})", org.OrgId, org.Name);

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(await BuildDto(org));
        return response;
    }

    /// <summary>
    /// POST /api/organizations/{orgId}/azure — connect the Azure tenant for an org.
    /// Materialises the tenants row at tenant_id = org_id. Never creates a new org.
    /// </summary>
    [Function("ConnectAzureTenant")]
    public async Task<HttpResponseData> ConnectAzureTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "organizations/{orgId:guid}/azure")] HttpRequestData req,
        Guid orgId,
        FunctionContext context)
    {
        var org = await _orgRepo.GetByIdAsync(orgId);
        if (org == null)
            return await NotFound(req, $"Organization {orgId} not found.");

        var body = await req.ReadFromJsonAsync<ConnectAzureTenantRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.AzureTenantId))
            return await BadRequest(req, "INVALID_REQUEST", "displayName and azureTenantId are required.");

        if (!Enum.TryParse<OnboardingMethod>(body.OnboardingMethod.Replace("_", ""), true, out var method))
            return await BadRequest(req, "INVALID_METHOD", "onboardingMethod must be 'lighthouse' or 'app_registration'.");

        // The Azure tenant for an org lives at tenant_id = org_id. If one already
        // exists, this is a no-op replace we don't support inline — keep it explicit.
        var existing = await _tenantRepo.GetByIdAsync(orgId);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.AzureTenantId))
            return await Conflict(req, "AZURE_ALREADY_CONNECTED",
                "This organization already has an Azure tenant connected.");

        var tenant = new Tenant
        {
            TenantId = orgId,                    // bind the Azure tenant to this workspace
            DisplayName = body.DisplayName.Trim(),
            AzureTenantId = body.AzureTenantId.Trim(),
            OnboardingMethod = method,
            Status = TenantStatus.Active,
            LighthouseDelegationId = body.LighthouseDelegationId
        };

        if (method == OnboardingMethod.AppRegistration && !string.IsNullOrEmpty(body.ClientId))
            tenant.SecretName = $"tenant-{tenant.AzureTenantId}-creds";

        var createdBy = context.GetUserId() ?? Guid.Empty;
        await _tenantRepo.CreateAsync(tenant, createdBy);

        // Specific subscriptions (empty = all subscriptions)
        if (body.SubscriptionIds is { Count: > 0 })
            await _tenantRepo.AddSubscriptionsAsync(orgId, body.SubscriptionIds);

        // App-registration credentials go to the secret store, never to SQL
        if (method == OnboardingMethod.AppRegistration
            && !string.IsNullOrEmpty(body.ClientId)
            && !string.IsNullOrEmpty(body.ClientSecret))
        {
            if (_secretStore == null)
                _logger.LogWarning("Secret store not configured — cannot store credentials for org {OrgId}", orgId);
            else
                await _secretStore.SetSecretAsync(tenant.SecretName!, JsonSerializer.Serialize(new
                {
                    clientId = body.ClientId,
                    clientSecret = body.ClientSecret
                }));
        }

        _logger.LogInformation("Connected Azure tenant {AzureTenantId} to organization {OrgId}",
            tenant.AzureTenantId, orgId);

        var response = req.CreateCorsResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(await BuildDto(org));
        return response;
    }

    /// <summary>Rollup: does the org have an Azure tenant + how many AWS/GCP orgs.</summary>
    private async Task<OrganizationDto> BuildDto(Organization o)
    {
        var azure = await _tenantRepo.GetByIdAsync(o.OrgId);
        var azureConnected = azure != null && !string.IsNullOrWhiteSpace(azure.AzureTenantId);

        var cloudOrgs = await _cloudOrgRepo.GetByTenantAsync(o.OrgId);
        var aws = cloudOrgs.Count(c => c.Provider == CloudProvider.Aws);
        var gcp = cloudOrgs.Count(c => c.Provider == CloudProvider.Gcp);

        return new OrganizationDto
        {
            OrgId = o.OrgId,
            Name = o.Name,
            Environment = o.Environment,
            Status = o.Status,
            CreatedAt = o.CreatedAt,
            AzureConnected = azureConnected,
            AzureTenantName = azureConnected ? azure!.DisplayName : null,
            AwsOrgCount = aws,
            GcpOrgCount = gcp,
            CloudCount = (azureConnected ? 1 : 0) + aws + gcp
        };
    }

    private static string GetActor(HttpRequestData req) =>
        req.Headers.TryGetValues("X-User-Name", out var values) ? values.First() : "operator";

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
