using System.Data.Common;
using System.Net;
using CloudRavel.Api.Auth;
using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Infrastructure.Chat;
using CloudRavel.Tests.Assessment;
using CloudRavel.Tests.BotMonitor;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using InventorySnapshot = CloudRavel.Core.Models.InventorySnapshot;

namespace CloudRavel.Tests.Auth;

internal static class EntitlementSweepHandlers
{
    internal static readonly Guid WorkspaceId = EntitlementMiddlewarePipelineHost.WorkspaceId;
    internal static readonly Guid UserId = EntitlementMiddlewarePipelineHost.UserId;
    internal static readonly Guid BotId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    internal static readonly Guid DetailId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    internal const string ResourceId =
        "subscriptions/sub/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm";

    private static readonly Fx F = new();

    public static async Task<HttpResponseData?> InvokeAsync(
        FunctionContext ctx,
        string method,
        string routeTemplate,
        EntitlementRouteSweepTests.Persona persona)
    {
        F.ApplyPersona(persona);
        var req = await ctx.GetHttpRequestDataAsync()
            ?? throw new InvalidOperationException("Sweep handler requires HTTP request data.");

        var m = method.ToUpperInvariant();
        var r = routeTemplate.ToLowerInvariant();

        try
        {
            return await DispatchAsync(req, ctx, m, r);
        }
        catch (Exception ex)
        {
            var error = req.CreateResponse();
            error.StatusCode = HttpStatusCode.BadRequest;
            await error.WriteAsJsonAsync(new { code = "SWEEP_HANDLER_ERROR", message = ex.Message });
            return error;
        }
    }

    private static async Task<HttpResponseData?> DispatchAsync(
        HttpRequestData req,
        FunctionContext ctx,
        string m,
        string r)
    {
        return (m, r) switch
        {
            ("GET", "admin/users") => await F.Admin.AdminUsers(req, ctx),
            ("POST", "admin/users") => await F.Admin.AdminUsers(req, ctx),
            ("POST", "admin/users/create") => await F.Admin.CreateUser(req, ctx),
            ("POST", "ai/chat") => await F.Chat.Chat(req, ctx),
            ("POST", "ai/query") => await F.Ai.Query(req, ctx),
            ("GET", "anomalies") => await F.AiOps.GetAnomalies(req, ctx),
            ("PATCH", "anomalies/{id:long}/status") => await F.AiOps.UpdateAnomalyStatus(req, 1L, ctx),
            ("POST", "auth/login") => await F.Auth.Login(req),
            ("GET", "auth/me") => await F.Auth.Me(req, ctx),
            ("GET", "bot-monitor/ops/bots") => await F.Bot.OpsListBots(req, ctx),
            ("GET", "bot-monitor/ops/fleet") => await F.Bot.OpsFleet(req, ctx),
            ("GET", "bot-monitor/ops/fleet/tenants/{tenantid:guid}/users") => await F.Bot.OpsFleetUsers(req, WorkspaceId.ToString(), ctx),
            ("GET", "bot-monitor/ops/fleet/tenants/{tenantid:guid}/users/{userid:guid}/bots") => await F.Bot.OpsFleetUserBots(req, WorkspaceId.ToString(), UserId.ToString(), ctx),
            ("GET", "bot-monitor/ops/metrics") => await F.Bot.OpsMetrics(req, ctx),
            ("GET", "bot-monitor/portal/bot-types") => await F.Bot.PortalBotTypes(req, ctx),
            ("GET", "bot-monitor/portal/bots") => await F.Bot.PortalListBots(req, ctx),
            ("GET", "bot-monitor/portal/bots/{botid:guid}") => await F.Bot.PortalGetBot(req, BotId.ToString(), ctx),
            ("POST", "bot-monitor/portal/magic-link") => await F.Bot.MagicLinkStub(req, ctx),
            ("GET", "bot-monitor/portal/metrics") => await F.Bot.PortalMetrics(req, ctx),
            ("GET", "bot-monitor/registry/bot-types") => await F.Bot.RegistryBotTypes(req, ctx),
            ("POST", "bot-monitor/registry/bot-types") => await F.Bot.RegistryBotTypes(req, ctx),
            ("GET", "bot-monitor/registry/bots") => await F.Bot.RegistryBots(req, ctx),
            ("POST", "bot-monitor/registry/bots") => await F.Bot.RegistryBots(req, ctx),
            ("POST", "bot-monitor/registry/bots/{botid:guid}/revoke") => await F.Bot.RevokeBot(req, BotId.ToString(), ctx),
            ("POST", "bot-monitor/registry/bots/{botid:guid}/rotate") => await F.Bot.RotateBot(req, BotId.ToString(), ctx),
            ("GET", "bot-monitor/registry/tenants") => await F.Bot.RegistryTenants(req, ctx),
            ("POST", "bot-monitor/registry/tenants") => await F.Bot.RegistryTenants(req, ctx),
            ("GET", "changes") => await F.Change.GetChanges(req, ctx),
            ("GET", "changes/recent") => await F.Change.GetRecentChanges(req, ctx),
            ("GET", "changes/timeline") => await F.Change.GetChangeTimeline(req, ctx),
            ("GET", "cloud-accounts") => await F.AiOps.GetCloudAccounts(req, ctx),
            ("POST", "cloud-accounts") => await F.AiOps.LinkCloudAccount(req, ctx),
            ("DELETE", "cloud-accounts/{id:guid}") => await F.AiOps.DeleteCloudAccount(req, EntitlementSweepHandlers.DetailId, ctx),
            ("POST", "cloud-accounts/{id:guid}/collect") => await F.AiOps.CollectCloudAccount(req, EntitlementSweepHandlers.DetailId, ctx),
            ("PUT", "cloud-accounts/{id:guid}/credentials") => await F.AiOps.UpdateCloudAccountCredentials(req, EntitlementSweepHandlers.DetailId, ctx),
            ("GET", "cloud-orgs") => await F.AiOps.GetCloudOrgs(req, ctx),
            ("POST", "cloud-orgs") => await F.AiOps.CreateCloudOrg(req, ctx),
            ("POST", "cloud-orgs/governance/sync") => await F.AiOps.SyncMultiCloudGovernance(req, ctx),
            ("DELETE", "cloud-orgs/{orgid:guid}") => await F.AiOps.DeleteCloudOrg(req, DetailId, ctx),
            ("PUT", "cloud-orgs/{orgid:guid}/credentials") => await F.AiOps.UpdateCloudOrgCredentials(req, DetailId, ctx),
            ("PATCH", "cloud-orgs/{orgid:guid}/status") => await F.AiOps.UpdateCloudOrgStatus(req, DetailId, ctx),
            ("GET", "dashboard") => await F.Tenant.GetDashboard(req, ctx),
            ("GET", "health") => await F.HealthFn.Health(req),
            ("GET", "health/ready") => await F.HealthFn.Ready(req),
            ("GET", "incidents") => await F.AiOps.GetIncidents(req, ctx),
            ("GET", "incidents/{id:long}") => await F.AiOps.GetIncidentDetail(req, 1L, ctx),
            ("PATCH", "incidents/{id:long}") => await F.AiOps.UpdateIncident(req, 1L, ctx),
            ("GET", "inventory/resource/{*resourceid}") => await F.Inventory.GetResourceDetail(req, ResourceId, ctx),
            ("GET", "inventory/resources") => await F.Inventory.GetResources(req, ctx),
            ("GET", "inventory/snapshots") => await F.Inventory.GetSnapshots(req, ctx),
            ("POST", "inventory/snapshots/trigger") => await F.Inventory.TriggerSnapshot(req, ctx),
            ("GET", "inventory/summary") => await F.Inventory.GetSummary(req, ctx),
            ("GET", "operations/summary") => await F.AiOps.GetOpsSummary(req, ctx),
            ("GET", "organizations") => await F.Org.ListOrganizations(req, ctx),
            ("POST", "organizations") => await F.Org.CreateOrganization(req, ctx),
            ("DELETE", "organizations/{orgid:guid}") => await F.Org.DeleteOrganization(req, WorkspaceId, ctx),
            ("POST", "organizations/{orgid:guid}/azure") => await F.Org.ConnectAzureTenant(req, WorkspaceId, ctx),
            ("GET", "organizations/{orgid:guid}/sso") => await F.OrgAdmin.GetOrgSso(req, WorkspaceId, ctx),
            ("PUT", "organizations/{orgid:guid}/sso") => await F.OrgAdmin.UpdateOrgSso(req, WorkspaceId, ctx),
            ("GET", "organizations/{orgid:guid}/users") => await F.OrgAdmin.ListOrgUsers(req, WorkspaceId, ctx),
            ("POST", "organizations/{orgid:guid}/users") => await F.OrgAdmin.AddOrgUser(req, WorkspaceId, ctx),
            ("DELETE", "organizations/{orgid:guid}/users/{userid:guid}") => await F.OrgAdmin.RemoveOrgUser(req, WorkspaceId, UserId, ctx),
            ("PATCH", "organizations/{orgid:guid}/users/{userid:guid}") => await F.OrgAdmin.UpdateOrgUserRole(req, WorkspaceId, UserId, ctx),
            ("GET", "recommendations/advisor") => await F.Rec.GetAdvisorRecommendations(req, ctx),
            ("PATCH", "recommendations/advisor/{recommendationid}/lifecycle") => await F.Rec.UpdateLifecycle(req, "1", ctx),
            ("GET", "recommendations/defender") => await F.Rec.GetDefenderFindings(req, ctx),
            ("GET", "recommendations/policy") => await F.Rec.GetPolicyCompliance(req, ctx),
            ("GET", "remediations") => await F.AiOps.GetRemediations(req, ctx),
            ("POST", "remediations") => await F.AiOps.ProposeRemediation(req, ctx),
            ("GET", "remediations/playbooks") => await F.AiOps.GetPlaybooks(req, ctx),
            ("POST", "remediations/{id:long}/approve") => await F.AiOps.ApproveRemediation(req, 1L, ctx),
            ("POST", "remediations/{id:long}/reject") => await F.AiOps.RejectRemediation(req, 1L, ctx),
            ("POST", "system/ai/test") => await F.Ai.TestConnection(req, ctx),
            ("PATCH", "system/organizations/{orgid:guid}/entitlements") => await F.Admin.UpdateOrgEntitlements(req, WorkspaceId, ctx),
            ("GET", "system/settings") => await F.Admin.SystemSettings(req, ctx),
            ("PUT", "system/settings") => await F.Admin.SystemSettings(req, ctx),
            ("GET", "system/users") => await F.Admin.SystemUsers(req, ctx),
            ("POST", "system/users") => await F.Admin.SystemUsers(req, ctx),
            ("PATCH", "system/users/{userid:guid}") => await F.Admin.UpdateUser(req, UserId, ctx),
            ("GET", "tenants") => await F.Tenant.ListTenants(req, ctx),
            ("POST", "tenants") => await F.Tenant.OnboardTenant(req, ctx),
            ("GET", "tenants/{tenantid:guid}") => await F.Tenant.GetTenant(req, WorkspaceId.ToString(), ctx),
            ("PATCH", "tenants/{tenantid:guid}/status") => await F.Tenant.UpdateTenantStatus(req, WorkspaceId.ToString(), ctx),
            _ => throw new InvalidOperationException($"Unhandled sweep route {m} {r}"),
        };
    }

    private sealed class Fx
    {
        internal readonly IUserRepository Users = Substitute.For<IUserRepository>();
        internal readonly IOrganizationRepository Orgs = Substitute.For<IOrganizationRepository>();
        internal readonly ITenantRepository Tenants = Substitute.For<ITenantRepository>();
        internal readonly IInventoryRepository InventoryRepo = Substitute.For<IInventoryRepository>();
        internal readonly IChangeRepository Changes = Substitute.For<IChangeRepository>();
        internal readonly IRecommendationRepository Recommendations = Substitute.For<IRecommendationRepository>();
        internal readonly IAuditRepository Audit = Substitute.For<IAuditRepository>();
        internal readonly IInventoryCollectionService InventoryCollection = Substitute.For<IInventoryCollectionService>();
        internal readonly ICloudOrgRepository CloudOrgs = Substitute.For<ICloudOrgRepository>();
        internal readonly ICloudAccountRepository CloudAccounts = Substitute.For<ICloudAccountRepository>();
        internal readonly IAnomalyRepository Anomalies = Substitute.For<IAnomalyRepository>();
        internal readonly IIncidentRepository Incidents = Substitute.For<IIncidentRepository>();
        internal readonly IRemediationRepository Remediations = Substitute.For<IRemediationRepository>();
        internal readonly IRemediationService RemediationService = Substitute.For<IRemediationService>();
        internal readonly ICloudProviderAdapterFactory AdapterFactory = Substitute.For<ICloudProviderAdapterFactory>();
        internal readonly IMultiCloudInventoryService MultiCloudInventory = Substitute.For<IMultiCloudInventoryService>();
        internal readonly IMultiCloudGovernanceSyncService MultiCloudGovernance = Substitute.For<IMultiCloudGovernanceSyncService>();
        internal readonly ISystemSettingsRepository SystemSettings = Substitute.For<ISystemSettingsRepository>();
        internal readonly IOrgSsoRepository OrgSso = Substitute.For<IOrgSsoRepository>();
        internal readonly ILocalAuthService LocalAuth = Substitute.For<ILocalAuthService>();
        internal readonly IBotMonitorRepository BotRepo = Substitute.For<IBotMonitorRepository>();
        internal readonly ITenantDbConnectionFactory DbFactory = Substitute.For<ITenantDbConnectionFactory>();
        internal readonly IChatUsageStore ChatUsage = Substitute.For<IChatUsageStore>();
        internal readonly IPlatformInfo Platform = new FakePlatformInfo();
        internal readonly IConfiguration Config;

        internal readonly HealthFunctions HealthFn;
        internal readonly AuthFunctions Auth;
        internal readonly OrganizationFunctions Org;
        internal readonly OrgAdminFunctions OrgAdmin;
        internal readonly TenantFunctions Tenant;
        internal readonly InventoryFunctions Inventory;
        internal readonly RecommendationFunctions Rec;
        internal readonly ChangeFunctions Change;
        internal readonly AiOpsFunctions AiOps;
        internal readonly AiFunctions Ai;
        internal readonly CustomerChatFunctions Chat;
        internal readonly AdminFunctions Admin;
        internal readonly BotMonitorFunctions Bot;

        internal Fx()
        {
            Config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalAuth:JwtSigningKey"] = "sweep-test-signing-key-min-32-chars!!",
                    ["BotMonitor:AllowDevKey"] = "true",
                    ["BotMonitor:DevDataProtectionKeyBase64"] = BotMonitorTestConfig.DevKeyBase64,
                })
                .Build();

            DbFactory.CreateAdminConnectionAsync().Returns(_ =>
                Task.FromException<DbConnection>(new InvalidOperationException("db offline")));

            SeedStaticData();

            var cred = BotMonitorTestConfig.Credentials();
            var hb = new BotMonitorHeartbeatService(BotRepo, cred, Config, () => DateTime.UtcNow);
            var rateLimiter = new LoginRateLimiter();
            var chatGateway = new ChatContextGateway(DbFactory, Tenants, NullLogger<ChatContextGateway>.Instance);

            HealthFn = new HealthFunctions(DbFactory, Config, Platform, NullLogger<HealthFunctions>.Instance);
            Auth = new AuthFunctions(LocalAuth, Users, rateLimiter, NullLogger<AuthFunctions>.Instance);
            Org = new OrganizationFunctions(Orgs, Tenants, CloudOrgs, CloudAccounts, Users, InventoryCollection, Platform,
                NullLogger<OrganizationFunctions>.Instance);
            OrgAdmin = new OrgAdminFunctions(Users, OrgSso, Orgs, Tenants, NullLogger<OrgAdminFunctions>.Instance);
            Tenant = new TenantFunctions(Tenants, InventoryRepo, Changes, Recommendations, Audit, InventoryCollection,
                NullLogger<TenantFunctions>.Instance);
            Inventory = new InventoryFunctions(InventoryRepo, InventoryCollection, CloudAccounts, CloudOrgs, Platform,
                NullLogger<InventoryFunctions>.Instance);
            Rec = new RecommendationFunctions(Recommendations, NullLogger<RecommendationFunctions>.Instance);
            Change = new ChangeFunctions(Changes, NullLogger<ChangeFunctions>.Instance);
            AiOps = new AiOpsFunctions(Anomalies, Incidents, Remediations, RemediationService, CloudOrgs, CloudAccounts,
                AdapterFactory, MultiCloudInventory, MultiCloudGovernance, Tenants, InventoryRepo, Platform,
                NullLogger<AiOpsFunctions>.Instance);
            Ai = new AiFunctions(InventoryRepo, Changes, Recommendations, Anomalies, Incidents, Remediations,
                RemediationService, CloudAccounts, Tenants, SystemSettings, Config, NullLogger<AiFunctions>.Instance);
            Chat = new CustomerChatFunctions(chatGateway, new ChatRateLimiter(ChatUsage), Audit, Config,
                NullLogger<CustomerChatFunctions>.Instance);
            Admin = new AdminFunctions(SystemSettings, Users, Orgs, NullLogger<AdminFunctions>.Instance);
            Bot = new BotMonitorFunctions(BotRepo, Users, Orgs, cred, hb, Config, NullLogger<BotMonitorFunctions>.Instance);
        }

        internal void ApplyPersona(EntitlementRouteSweepTests.Persona persona)
        {
            Users.GetFeatureEntitlementsAsync(UserId, WorkspaceId).Returns(persona.Member);
            Users.GetFeatureEntitlementsAsync(UserId, Arg.Any<Guid>()).Returns(persona.Member);
            Orgs.GetFeatureEntitlementsAsync(WorkspaceId).Returns(persona.Org);
            Orgs.GetFeatureEntitlementsAsync(Arg.Any<Guid>()).Returns(persona.Org);
        }

        private void SeedStaticData()
        {
            var workspaceTenant = new Tenant
            {
                TenantId = WorkspaceId,
                DisplayName = "Sweep Workspace",
                Status = TenantStatus.Active,
                AzureTenantId = Guid.NewGuid().ToString(),
            };

            Users.GetByIdAsync(UserId).Returns(new User
            {
                UserId = UserId,
                DisplayName = "Sweep User",
                Email = "sweep@example.test",
                GlobalRole = SystemRole.Member,
                IsActive = true,
                AuthProvider = "local",
            });
            Users.GetTenantRoleAsync(UserId, WorkspaceId).Returns(OrgRole.ReadOnly);
            Users.GetUserTenantAccessAsync(UserId).Returns(new List<UserTenantAccess>
            {
                new() { UserId = UserId, TenantId = WorkspaceId, Role = OrgRole.ReadOnly },
            });
            Users.ListByTenantAsync(WorkspaceId).Returns(Array.Empty<(User, string, FeatureEntitlements)>());

            Orgs.GetAllAsync().Returns(new List<Organization>
            {
                new() { OrgId = WorkspaceId, Name = "Sweep Org", Status = "active", Environment = "Development" },
            });
            Orgs.GetByIdAsync(WorkspaceId).Returns(new Organization
            {
                OrgId = WorkspaceId,
                Name = "Sweep Org",
                Status = "active",
                Environment = "Development",
            });

            Tenants.GetByUserAccessAsync(UserId).Returns(new List<Tenant> { workspaceTenant });
            Tenants.GetAllActiveAsync().Returns(new List<Tenant> { workspaceTenant });
            Tenants.GetByIdAsync(WorkspaceId).Returns(workspaceTenant);

            InventoryRepo.GetResourcesAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(new List<InventoryResource>
                {
                    new()
                    {
                        ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm",
                        ResourceName = "vm",
                        ResourceType = "microsoft.compute/virtualmachines",
                        ResourceGroup = "rg",
                    },
                });
            InventoryRepo.GetResourceCountAsync(Arg.Any<Guid>(), Arg.Any<long?>()).Returns(1);
            InventoryRepo.GetLatestSnapshotAsync(Arg.Any<Guid>()).Returns(new InventorySnapshot
            {
                SnapshotId = 1,
                TenantId = WorkspaceId,
                CompletedAt = DateTime.UtcNow,
            });
            InventoryRepo.GetResourceByIdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long?>())
                .Returns(new InventoryResource
                {
                    ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm",
                    ResourceName = "vm",
                    ResourceType = "microsoft.compute/virtualmachines",
                });
            InventoryRepo.GetSnapshotHistoryAsync(Arg.Any<Guid>(), Arg.Any<int>())
                .Returns(new List<InventorySnapshot> { new() { SnapshotId = 1, TenantId = WorkspaceId, CompletedAt = DateTime.UtcNow } });
            InventoryRepo.GetResourceTypeSummaryAsync(Arg.Any<Guid>(), Arg.Any<long?>())
                .Returns(Array.Empty<ResourceTypeSummary>());
            InventoryRepo.GetResourceCountsByProviderAsync(Arg.Any<Guid>(), Arg.Any<long?>())
                .Returns(new Dictionary<string, int> { ["azure"] = 1 });

            Changes.GetChangesAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<string?>(),
                    Arg.Any<ChangeClassification?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<ResourceChange>());
            Changes.GetRecentChangesAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<ResourceChange>());
            Changes.GetChangeTimelineAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
                .Returns(Array.Empty<ChangeTimeline>());
            Changes.GetChangeCountAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns(0);

            Recommendations.GetAdvisorRecommendationsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<RecommendationLifecycle?>(),
                    Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<AdvisorRecommendation>());
            Recommendations.GetPolicyComplianceAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<PolicyComplianceRecord>());
            Recommendations.GetDefenderFindingsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<DefenderFinding>());

            Anomalies.GetAnomaliesAsync(Arg.Any<Guid>(), Arg.Any<AnomalyStatus?>(), Arg.Any<AnomalySeverity?>(), Arg.Any<AnomalyKind?>(),
                    Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<Anomaly>());
            Incidents.GetIncidentsAsync(Arg.Any<Guid>(), Arg.Any<IncidentStatus?>(), Arg.Any<AnomalySeverity?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<Incident>());
            Incidents.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<long>()).Returns((Incident?)null);
            Remediations.GetActionsAsync(Arg.Any<Guid>(), Arg.Any<RemediationStatus?>(), Arg.Any<int>(), Arg.Any<int>())
                .Returns(Array.Empty<RemediationAction>());
            Remediations.GetPlaybooksAsync(Arg.Any<CloudProvider?>(), Arg.Any<bool>())
                .Returns(Array.Empty<RemediationPlaybook>());

            CloudOrgs.GetByTenantAsync(Arg.Any<Guid>()).Returns(Array.Empty<CloudOrg>());
            CloudAccounts.GetByTenantAsync(Arg.Any<Guid>()).Returns(Array.Empty<CloudAccount>());

            BotRepo.ListBotsForTenantAsync(WorkspaceId, Arg.Any<Guid?>(), Arg.Any<string?>())
                .Returns(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?)>());
            BotRepo.ListMetricsForTenantAsync(WorkspaceId, Arg.Any<Guid?>(), Arg.Any<DateTime>())
                .Returns(Array.Empty<(Guid, string, BotMonitorHealthMetricsSnapshot)>());
            BotRepo.ListBotTypesAsync(true).Returns(Array.Empty<BotMonitorBotType>());
            BotRepo.GetBotByIdAsync(WorkspaceId, BotId).Returns(new BotMonitorBot
            {
                BotId = BotId,
                TenantId = WorkspaceId,
                OwnerUserId = UserId,
                DisplayName = "sweep-bot",
            });
            BotRepo.GetTenantAsync(WorkspaceId).Returns(new BotMonitorTenant { TenantId = WorkspaceId, Name = "Sweep" });
            BotRepo.ListFleetTenantSummariesAsync(Arg.Any<DateTime>()).Returns(Array.Empty<(Guid, string, int, int)>());
            BotRepo.ListAllBotsAsync(Arg.Any<Guid?>(), Arg.Any<string?>())
                .Returns(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?, string)>());
            BotRepo.ListTenantsAsync().Returns(new List<BotMonitorTenant> { new() { TenantId = WorkspaceId, Name = "Sweep" } });

            SystemSettings.GetAllAsync().Returns(new Dictionary<string, string?>());
            OrgSso.GetAsync(WorkspaceId).Returns((OrgSsoSettings?)null);
        }
    }
}
