using System.Net;
using System.Text.Json;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

public sealed class BotMonitorFunctions
{
    private readonly IBotMonitorRepository _repo;
    private readonly IOrganizationRepository _organizations;
    private readonly BotMonitorCredentialService _credentials;
    private readonly BotMonitorHeartbeatService _heartbeat;
    private readonly IConfiguration _config;
    private readonly ILogger<BotMonitorFunctions> _logger;
    private readonly Func<DateTime> _clock;

    public BotMonitorFunctions(
        IBotMonitorRepository repo,
        IOrganizationRepository organizations,
        BotMonitorCredentialService credentials,
        BotMonitorHeartbeatService heartbeat,
        IConfiguration config,
        ILogger<BotMonitorFunctions> logger)
    {
        _repo = repo;
        _organizations = organizations;
        _credentials = credentials;
        _heartbeat = heartbeat;
        _config = config;
        _logger = logger;
        _clock = () => DateTime.UtcNow;
    }

    [Function("BotMonitorHeartbeat")]
    public async Task<HttpResponseData> Heartbeat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bot-monitor/heartbeat")] HttpRequestData req,
        FunctionContext context)
    {
        var bearer = ExtractBearer(req);
        req.Headers.TryGetValues("X-CR-Timestamp", out var tsValues);
        req.Headers.TryGetValues("X-CR-Signature", out var sigValues);
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        var result = await _heartbeat.ProcessAsync(
            bearer ?? string.Empty,
            tsValues?.FirstOrDefault(),
            sigValues?.FirstOrDefault(),
            body);

        return result.Reason switch
        {
            BotMonitorHeartbeatService.HeartbeatRejectReason.None =>
                await WriteJsonAsync(req, HttpStatusCode.OK, new { ok = true }),
            BotMonitorHeartbeatService.HeartbeatRejectReason.PayloadTooLarge =>
                await WriteJsonAsync(req, HttpStatusCode.RequestEntityTooLarge, Error("PAYLOAD_TOO_LARGE", "Body exceeds 16 KB.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.Replay =>
                await WriteJsonAsync(req, HttpStatusCode.Conflict, Error("REPLAY", "This signed request was already accepted.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.RateLimited =>
                await WriteJsonAsync(req, (HttpStatusCode)429, Error("RATE_LIMITED", "Too many heartbeats for this bot.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.InvalidMetrics =>
                await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_METRICS", "Reported metrics failed validation or exceed size caps.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.TimestampSkew =>
                await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("TIMESTAMP_SKEW", "Timestamp outside the allowed window.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.BadSignature =>
                await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("BAD_SIGNATURE", "Signature verification failed.")),
            _ => await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("UNAUTHORIZED", "Invalid or revoked bot token.")),
        };
    }

    [Function("BotMonitorOpsFleet")]
    public async Task<HttpResponseData> OpsFleet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/ops/fleet")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        var now = _clock();
        var rows = await _repo.ListFleetTenantSummariesAsync(now);
        var tenants = new List<BotMonitorFleetTenantDto>();
        foreach (var r in rows)
        {
            var typeRows = await _repo.CountBotsByTypeAsync(r.TenantId, null, null);
            tenants.Add(new BotMonitorFleetTenantDto
            {
                TenantId = r.TenantId,
                TenantName = r.TenantName,
                BotCount = r.BotCount,
                OnlineCount = r.OnlineCount,
                TypeCounts = MapTypeCounts(typeRows),
            });
        }

        var fleetTypeRows = await _repo.CountBotsByTypeAsync(null, null, null);
        return await WriteJsonAsync(req, HttpStatusCode.OK, new
        {
            tenants,
            fleetTypeCounts = MapTypeCounts(fleetTypeRows),
        });
    }

    [Function("BotMonitorOpsFleetUsers")]
    public async Task<HttpResponseData> OpsFleetUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/ops/fleet/tenants/{tenantId:guid}/users")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (!Guid.TryParse(tenantId, out var tenantGuid) || tenantGuid == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid tenant id."));

        if (await _repo.GetTenantAsync(tenantGuid) is null)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Tenant not found."));

        var rows = await _repo.ListFleetUsersForTenantAsync(tenantGuid);
        var tenantTypeRows = await _repo.CountBotsByTypeAsync(tenantGuid, null, null);
        var users = new List<BotMonitorFleetUserDto>();
        foreach (var r in rows)
        {
            var userTypeRows = await _repo.CountBotsByTypeAsync(tenantGuid, r.UserId, null);
            users.Add(new BotMonitorFleetUserDto
            {
                UserId = r.UserId,
                DisplayName = r.DisplayName,
                Email = r.Email,
                BotCount = r.BotCount,
                TypeCounts = MapTypeCounts(userTypeRows),
            });
        }

        return await WriteJsonAsync(req, HttpStatusCode.OK, new
        {
            tenantId = tenantGuid,
            typeCounts = MapTypeCounts(tenantTypeRows),
            users,
        });
    }

    [Function("BotMonitorOpsFleetUserBots")]
    public async Task<HttpResponseData> OpsFleetUserBots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/ops/fleet/tenants/{tenantId:guid}/users/{userId:guid}/bots")] HttpRequestData req,
        string tenantId,
        string userId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (!Guid.TryParse(tenantId, out var tenantGuid) || !Guid.TryParse(userId, out var userGuid))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid tenant or user id."));

        if (await _repo.GetTenantAsync(tenantGuid) is null)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Tenant not found."));

        var botTypeKey = ParseBotTypeKeyQuery(req);
        var now = _clock();
        var rows = await _repo.ListBotsForTenantUserAsync(tenantGuid, userGuid, botTypeKey);
        var typeRows = await _repo.CountBotsByTypeAsync(tenantGuid, userGuid, null);
        var bots = rows.Select(r => ToListItem(r.Bot, r.State, r.LastKeepCheck, string.Empty, now)).ToList();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new
        {
            tenantId = tenantGuid,
            userId = userGuid,
            typeCounts = MapTypeCounts(typeRows),
            bots,
        });
    }

    [Function("BotMonitorOpsBots")]
    public async Task<HttpResponseData> OpsListBots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/ops/bots")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        Guid? filter = null;
        if (Guid.TryParse(QueryParam(req.Url, "tenantId"), out var tid) && tid != Guid.Empty) filter = tid;
        var botTypeKey = ParseBotTypeKeyQuery(req);

        var now = _clock();
        var rows = await _repo.ListAllBotsAsync(filter, botTypeKey);
        var typeRows = await _repo.CountBotsByTypeAsync(filter, null, botTypeKey);
        var dto = rows.Select(r => ToListItem(r.Bot, r.State, r.LastKeepCheck, r.TenantName, now)).ToList();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new { typeCounts = MapTypeCounts(typeRows), bots = dto });
    }

    [Function("BotMonitorPortalMetrics")]
    public async Task<HttpResponseData> PortalMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/portal/metrics")] HttpRequestData req,
        FunctionContext context)
    {
        var auth = await context.RequireAuthenticatedUserAsync(req);
        if (auth != null) return auth;
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.ReadOnly);
        if (forbid != null) return forbid;
        var botGate = await context.RequireBotMonitoringEntitlementAsync(req);
        if (botGate != null) return botGate;

        var tenantId = context.GetTenantId();
        if (tenantId == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_WORKSPACE", "Select an organization first."));

        var userId = context.GetUserId();
        if (!userId.HasValue)
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("UNAUTHENTICATED", "User identity is required."));

        var now = _clock();
        Guid? ownerFilter = context.CanViewAllTenantBots() ? null : userId.Value;
        var rows = await _repo.ListMetricsForTenantAsync(tenantId, ownerFilter, now);
        var bots = rows.Select(r => new BotMonitorBotMetricsItemDto
        {
            BotId = r.BotId,
            DisplayName = r.DisplayName,
            Metrics = MapMetrics(r.Snapshot),
        }).ToList();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new { bots });
    }

    [Function("BotMonitorOpsMetrics")]
    public async Task<HttpResponseData> OpsMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/ops/metrics")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (!Guid.TryParse(QueryParam(req.Url, "tenantId"), out var tenantGuid) || tenantGuid == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_TENANT", "tenantId query parameter is required."));
        if (!Guid.TryParse(QueryParam(req.Url, "userId"), out var userGuid) || userGuid == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_USER", "userId query parameter is required."));

        if (await _repo.GetTenantAsync(tenantGuid) is null)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Tenant not found."));

        var now = _clock();
        var rows = await _repo.ListMetricsForTenantUserAsync(tenantGuid, userGuid, now);
        var bots = rows.Select(r => new BotMonitorBotMetricsItemDto
        {
            BotId = r.BotId,
            DisplayName = r.DisplayName,
            Metrics = MapMetrics(r.Snapshot),
        }).ToList();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new { tenantId = tenantGuid, userId = userGuid, bots });
    }

    [Function("BotMonitorPortalBots")]
    public async Task<HttpResponseData> PortalListBots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/portal/bots")] HttpRequestData req,
        FunctionContext context)
    {
        var auth = await context.RequireAuthenticatedUserAsync(req);
        if (auth != null) return auth;
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.ReadOnly);
        if (forbid != null) return forbid;
        var botGate = await context.RequireBotMonitoringEntitlementAsync(req);
        if (botGate != null) return botGate;

        var tenantId = context.GetTenantId();
        if (tenantId == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_WORKSPACE", "Select an organization before opening the customer bot view."));

        var forged = qTenantOverride(req);
        if (forged.HasValue && forged != tenantId)
            return await WriteJsonAsync(req, HttpStatusCode.Forbidden, Error("TENANT_FORBIDDEN", "Tenant cannot be overridden on this route."));

        var userId = context.GetUserId();
        if (!userId.HasValue)
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("UNAUTHENTICATED", "User identity is required."));

        var botTypeKey = ParseBotTypeKeyQuery(req);
        var now = _clock();
        var organizationView = context.CanViewAllTenantBots();
        Guid? ownerFilter = organizationView ? null : userId.Value;
        var rows = await _repo.ListBotsForTenantAsync(tenantId, ownerFilter, botTypeKey);
        var typeCountRows = await _repo.CountBotsByTypeAsync(tenantId, ownerFilter, botTypeKey);
        var userBotCounts = organizationView
            ? await BuildPortalUserBotCountsAsync(tenantId, botTypeKey)
            : Array.Empty<BotMonitorPortalUserBotCountDto>();
        var bots = new List<BotMonitorPortalBotDto>();
        var online = 0;
        var stale = 0;
        var offline = 0;
        var uptimeSum = 0.0;
        foreach (var row in rows)
        {
            var history = await _repo.GetWeeklyHistoryAsync(tenantId, row.Bot.BotId, 8, now);
            var checkins = await _repo.GetRecentCheckinsAsync(tenantId, row.Bot.BotId, 48, now);
            var metricsSnap = await _repo.GetBotMetricsSnapshotAsync(tenantId, row.Bot.BotId, now);
            var presence = BotMonitorStaleEvaluator.Evaluate(row.State?.LastSeenAt, row.Bot.CheckinIntervalMinutes, now);
            if (presence == BotMonitorPresence.Online) online++;
            else if (presence == BotMonitorPresence.Stale) stale++;
            else offline++;
            if (history.Count > 0) uptimeSum += history.Average(h => h.UptimePercent);

            bots.Add(new BotMonitorPortalBotDto
            {
                BotId = row.Bot.BotId,
                OwnerUserId = row.Bot.OwnerUserId,
                OwnerDisplayName = row.Bot.OwnerDisplayName,
                DisplayName = row.Bot.DisplayName,
                Platform = row.Bot.Platform.ToString(),
                BotTypeKey = row.Bot.BotTypeKey,
                BotTypeDisplayName = row.Bot.BotTypeDisplayName ?? row.Bot.BotTypeKey,
                Presence = presence.ToString(),
                LastSeenAt = row.State?.LastSeenAt,
                WeeklyHistory = history.Select(h => new BotMonitorWeeklyBucketDto
                {
                    IsoYear = h.IsoYear,
                    IsoWeek = h.IsoWeek,
                    UptimePercent = Math.Round(h.UptimePercent, 1),
                    KeepPassCount = h.KeepPassCount,
                    KeepWarnCount = h.KeepWarnCount,
                    KeepFailCount = h.KeepFailCount,
                    ErrorCount = h.ErrorCount,
                }).ToList(),
                RecentCheckins = checkins.Select(c => new BotMonitorCheckinPointDto
                {
                    ReceivedAt = c.ReceivedAt,
                    Status = c.Status,
                }).ToList(),
                HealthMetrics = metricsSnap is null ? null : MapMetrics(metricsSnap),
            });
        }

        var dashboard = new BotMonitorPortalDashboardDto
        {
            ViewScope = organizationView ? "organization" : "user",
            TotalBots = bots.Count,
            OnlineCount = online,
            StaleCount = stale,
            OfflineCount = offline,
            AverageUptimePercent = bots.Count == 0 ? 0 : Math.Round(uptimeSum / bots.Count, 1),
            UserBotCounts = userBotCounts,
            TypeCounts = MapTypeCounts(typeCountRows),
            Bots = bots,
        };

        return await WriteJsonAsync(req, HttpStatusCode.OK, new { dashboard });
    }

    [Function("BotMonitorPortalBotTypes")]
    public async Task<HttpResponseData> PortalBotTypes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/portal/bot-types")] HttpRequestData req,
        FunctionContext context)
    {
        var auth = await context.RequireAuthenticatedUserAsync(req);
        if (auth != null) return auth;
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.ReadOnly);
        if (forbid != null) return forbid;
        var botGate = await context.RequireBotMonitoringEntitlementAsync(req);
        if (botGate != null) return botGate;

        if (context.GetTenantId() == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_WORKSPACE", "Select an organization first."));

        var types = await _repo.ListBotTypesAsync(activeOnly: true);
        return await WriteJsonAsync(req, HttpStatusCode.OK, new
        {
            types = types.Select(MapBotType).ToList(),
        });
    }

    [Function("BotMonitorPortalBotById")]
    public async Task<HttpResponseData> PortalGetBot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bot-monitor/portal/bots/{botId:guid}")] HttpRequestData req,
        string botId,
        FunctionContext context)
    {
        var auth = await context.RequireAuthenticatedUserAsync(req);
        if (auth != null) return auth;
        var forbid = await context.RequireOrgRoleAsync(req, OrgRole.ReadOnly);
        if (forbid != null) return forbid;
        var botGate = await context.RequireBotMonitoringEntitlementAsync(req);
        if (botGate != null) return botGate;

        if (!Guid.TryParse(botId, out var botGuid))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid bot id."));

        var tenantId = context.GetTenantId();
        var forged = qTenantOverride(req);
        if (forged.HasValue && forged != tenantId)
            return await WriteJsonAsync(req, HttpStatusCode.Forbidden, Error("TENANT_FORBIDDEN", "Tenant cannot be overridden on this route."));

        var userId = context.GetUserId();
        var bot = await _repo.GetBotByIdAsync(tenantId, botGuid);
        if (bot is null)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Bot not found in this organization."));
        if (userId.HasValue && bot.OwnerUserId.HasValue && bot.OwnerUserId != userId
            && !context.CanViewAllTenantBots() && !context.IsSystemAdmin())
            return await WriteJsonAsync(req, HttpStatusCode.Forbidden, Error("USER_FORBIDDEN", "This bot belongs to another user in the organization."));

        return await WriteJsonAsync(req, HttpStatusCode.OK, new { botId = bot.BotId, displayName = bot.DisplayName });
    }

    [Function("BotMonitorRegistryTenants")]
    public async Task<HttpResponseData> RegistryTenants(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "bot-monitor/registry/tenants")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var tenants = await _repo.ListTenantsAsync();
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { tenants });
        }

        CreateBotMonitorTenantRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CreateBotMonitorTenantRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            body = null;
        }

        if (body is null || body.OrganizationId == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_BODY", "organizationId is required."));

        var org = await _organizations.GetByIdAsync(body.OrganizationId);
        if (org is null)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("UNKNOWN_ORGANIZATION", "Organization not found."));

        var tenant = await _repo.LinkOrganizationAsync(org.OrgId, org.Name);
        if (tenant is null)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("LINK_FAILED", "Could not link organization to bot monitor."));

        return await WriteJsonAsync(req, HttpStatusCode.Created, tenant);
    }

    [Function("BotMonitorRegistryBots")]
    public async Task<HttpResponseData> RegistryBots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "bot-monitor/registry/bots")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var now = _clock();
            var rows = await _repo.ListAllBotsAsync(null);
            var dto = rows.Select(r => ToListItem(r.Bot, r.State, r.LastKeepCheck, r.TenantName, now)).ToList();
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { bots = dto });
        }

        CreateBotMonitorBotRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CreateBotMonitorBotRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            body = null;
        }

        if (body is null || body.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(body.DisplayName))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_BODY", "TenantId and displayName are required."));

        if (await _organizations.GetByIdAsync(body.TenantId) is null)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("UNKNOWN_ORGANIZATION", "Organization not found."));

        if (await _repo.GetTenantAsync(body.TenantId) is null)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("UNKNOWN_TENANT", "Link the organization via bot-monitor registry before creating bots."));

        var typeKey = string.IsNullOrWhiteSpace(body.BotTypeKey) ? "other_plugin" : body.BotTypeKey.Trim();
        var botType = await _repo.GetBotTypeByKeyAsync(typeKey);
        if (botType is null || !botType.IsActive)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("UNKNOWN_BOT_TYPE", "botTypeKey must reference an active row in bot_monitor_bot_types."));

        var mint = _credentials.MintCredentials();
        var bot = await _repo.InsertBotAsync(new BotMonitorBot
        {
            TenantId = body.TenantId,
            OwnerUserId = body.OwnerUserId,
            DisplayName = body.DisplayName,
            Platform = body.Platform,
            BotTypeKey = typeKey,
            CheckinIntervalMinutes = body.CheckinIntervalMinutes,
            TokenHash = mint.TokenHash,
            HmacSecretEnc = mint.HmacSecretEnc,
        });

        _logger.LogInformation("Created bot monitor bot {BotId} for tenant {TenantId}", bot.BotId, bot.TenantId);

        return await WriteJsonAsync(req, HttpStatusCode.Created, new CreateBotMonitorBotResponse
        {
            BotId = bot.BotId,
            TenantId = bot.TenantId,
            DisplayName = bot.DisplayName,
            Token = mint.Token,
            HmacSecret = mint.HmacSecret,
        });
    }

    [Function("BotMonitorRegistryBotTypes")]
    public async Task<HttpResponseData> RegistryBotTypes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "bot-monitor/registry/bot-types")] HttpRequestData req,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var types = await _repo.ListBotTypesAsync(activeOnly: false);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { types = types.Select(MapBotType).ToList() });
        }

        CreateBotMonitorBotTypeRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<CreateBotMonitorBotTypeRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            body = null;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.TypeKey) || string.IsNullOrWhiteSpace(body.DisplayName))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_BODY", "typeKey and displayName are required."));

        var existing = await _repo.GetBotTypeByKeyAsync(body.TypeKey);
        if (existing is not null)
            return await WriteJsonAsync(req, HttpStatusCode.Conflict, Error("DUPLICATE_TYPE", "A bot type with this typeKey already exists."));

        try
        {
            var created = await _repo.InsertBotTypeAsync(body.TypeKey, body.DisplayName);
            return await WriteJsonAsync(req, HttpStatusCode.Created, MapBotType(created));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_TYPE_KEY", ex.Message));
        }
    }

    [Function("BotMonitorRevokeBot")]
    public async Task<HttpResponseData> RevokeBot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bot-monitor/registry/bots/{botId:guid}/revoke")] HttpRequestData req,
        string botId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (!Guid.TryParse(botId, out var botGuid))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid bot id."));

        if (!Guid.TryParse(QueryParam(req.Url, "tenantId"), out var tenantId))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_TENANT", "tenantId query parameter is required."));

        var ok = await _repo.RevokeBotAsync(tenantId, botGuid);
        return ok
            ? await WriteJsonAsync(req, HttpStatusCode.OK, new { revoked = true })
            : await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Bot not found."));
    }

    [Function("BotMonitorRotateBot")]
    public async Task<HttpResponseData> RotateBot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bot-monitor/registry/bots/{botId:guid}/rotate")] HttpRequestData req,
        string botId,
        FunctionContext context)
    {
        var forbid = await context.RequireSystemAdminAsync(req);
        if (forbid != null) return forbid;

        if (!Guid.TryParse(botId, out var botGuid))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid bot id."));

        if (!Guid.TryParse(QueryParam(req.Url, "tenantId"), out var tenantId))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_TENANT", "tenantId query parameter is required."));

        var mint = _credentials.MintCredentials();
        var ok = await _repo.UpdateBotCredentialsAsync(tenantId, botGuid, mint.TokenHash, mint.HmacSecretEnc);
        if (!ok)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Bot not found."));

        return await WriteJsonAsync(req, HttpStatusCode.OK, new CreateBotMonitorBotResponse
        {
            BotId = botGuid,
            TenantId = tenantId,
            Token = mint.Token,
            HmacSecret = mint.HmacSecret,
        });
    }

    [Function("BotMonitorMagicLinkStub")]
    public async Task<HttpResponseData> MagicLinkStub(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bot-monitor/portal/magic-link")] HttpRequestData req,
        FunctionContext context)
    {
        if (!bool.TryParse(_config["BotMonitor:PortalMagicLinkEnabled"], out var enabled) || !enabled)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("DISABLED", "Portal magic link is not enabled on this instance."));

        BotMonitorMagicLinkRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<BotMonitorMagicLinkRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            body = null;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Email))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_BODY", "Email is required."));

        var tenantHeader = context.TryGetTenantId();
        if (tenantHeader is not Guid tenantGuid || tenantGuid == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_WORKSPACE", "X-Tenant-Id is required."));

        var token = BotMonitorCrypto.GenerateToken();
        var tokenHash = _credentials.HashToken(token);
        var expires = _clock().AddMinutes(15);
        await _repo.StoreMagicLinkAsync(context.GetTenantId(), tokenHash, expires);

        return await WriteJsonAsync(req, HttpStatusCode.Accepted, new BotMonitorMagicLinkStubResponse
        {
            Message = "If this address is registered, we will send a sign-in link shortly.",
            DevRedeemPath = $"/portal/bots?magic={token}",
        });
    }

    private static BotMonitorHealthMetricsDto MapMetrics(BotMonitorHealthMetricsSnapshot snap) => new()
    {
        AsOfUtc = snap.AsOfUtc,
        Derived = new BotMonitorDerivedMetricsDto
        {
            ConsecutiveFailureStreak = snap.Derived.ConsecutiveFailureStreak,
            RoutineMissed = snap.Derived.RoutineMissed,
            CheckInLatenessMinutes = snap.Derived.CheckInLatenessMinutes,
            RunsPerDay = snap.Derived.RunsPerDay,
            ActionErrorsReported = snap.Derived.ActionErrorsReported,
        },
        Reported = snap.Reported is null ? null : new BotMonitorReportedMetricsDto
        {
            Routine = snap.Reported.Routine is null ? null : new BotMonitorRoutineReportDto
            {
                Id = snap.Reported.Routine.Id,
                ExpectedAt = snap.Reported.Routine.ExpectedAt,
                RanAt = snap.Reported.Routine.RanAt,
                Result = snap.Reported.Routine.Result,
            },
            Connectors = snap.Reported.Connectors?.Select(c => new BotMonitorConnectorReportDto
            {
                Name = c.Name,
                State = c.State,
            }).ToList() ?? new List<BotMonitorConnectorReportDto>(),
            Actions = snap.Reported.Actions is null ? null : new BotMonitorActionCountsDto
            {
                Attempted = snap.Reported.Actions.Attempted,
                Failed = snap.Reported.Actions.Failed,
            },
            Inbox = snap.Reported.Inbox is null ? null : new BotMonitorInboxReportDto
            {
                Unanswered = snap.Reported.Inbox.Unanswered,
                OldestMinutes = snap.Reported.Inbox.OldestMinutes,
            },
            Approvals = snap.Reported.Approvals is null ? null : new BotMonitorApprovalReportDto
            {
                Pending = snap.Reported.Approvals.Pending,
                OldestMinutes = snap.Reported.Approvals.OldestMinutes,
            },
            Queue = snap.Reported.Queue is null ? null : new BotMonitorQueueReportDto
            {
                Open = snap.Reported.Queue.Open,
                OldestMinutes = snap.Reported.Queue.OldestMinutes,
            },
            Escalations = snap.Reported.Escalations,
        },
    };

    private BotMonitorBotListItemDto ToListItem(
        BotMonitorBot bot, BotMonitorBotState? state, string? lastKeep, string tenantName, DateTime now) =>
        new()
        {
            BotId = bot.BotId,
            TenantId = bot.TenantId,
            OwnerUserId = bot.OwnerUserId,
            TenantName = tenantName,
            DisplayName = bot.DisplayName,
            Platform = bot.Platform.ToString(),
            BotTypeKey = bot.BotTypeKey,
            BotTypeDisplayName = bot.BotTypeDisplayName ?? bot.BotTypeKey,
            Presence = BotMonitorStaleEvaluator.Evaluate(state?.LastSeenAt, bot.CheckinIntervalMinutes, now).ToString(),
            LastSeenAt = state?.LastSeenAt,
            LastError = state?.LastError,
            LastKeepCheck = lastKeep,
            RevokedAt = bot.RevokedAt,
        };

    private async Task<IReadOnlyList<BotMonitorPortalUserBotCountDto>> BuildPortalUserBotCountsAsync(
        Guid tenantId, string? botTypeKey)
    {
        var users = await _repo.ListUserBotCountsForTenantAsync(tenantId);
        var result = new List<BotMonitorPortalUserBotCountDto>();
        foreach (var u in users)
        {
            var typeRows = await _repo.CountBotsByTypeAsync(tenantId, u.UserId, botTypeKey);
            var botCount = typeRows.Sum(x => x.Count);
            if (botTypeKey is not null && botCount == 0) continue;
            result.Add(new BotMonitorPortalUserBotCountDto
            {
                UserId = u.UserId,
                DisplayName = u.DisplayName,
                Email = u.Email,
                BotCount = botTypeKey is null ? u.BotCount : botCount,
                TypeCounts = MapTypeCounts(typeRows),
            });
        }

        return result;
    }

    private static BotMonitorBotTypeDto MapBotType(BotMonitorBotType t) => new()
    {
        TypeKey = t.TypeKey,
        DisplayName = t.DisplayName,
        IsActive = t.IsActive,
    };

    private static IReadOnlyList<BotMonitorBotTypeCountDto> MapTypeCounts(
        IEnumerable<(string TypeKey, string DisplayName, int Count)> rows) =>
        rows.Select(r => new BotMonitorBotTypeCountDto
        {
            TypeKey = r.TypeKey,
            DisplayName = r.DisplayName,
            Count = r.Count,
        }).ToList();

    private static string? ParseBotTypeKeyQuery(HttpRequestData req)
    {
        var raw = QueryParam(req.Url, "botTypeKey");
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static Guid? qTenantOverride(HttpRequestData req) =>
        Guid.TryParse(QueryParam(req.Url, "tenantId"), out var t) ? t : null;

    private static string? QueryParam(Uri url, string key)
    {
        var query = url.Query;
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }

        return null;
    }

    private static string? ExtractBearer(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("Authorization", out var values)) return null;
        var raw = values.FirstOrDefault();
        if (raw is null) return null;
        const string prefix = "Bearer ";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? raw[prefix.Length..].Trim() : null;
    }

    private static object Error(string code, string message) => new { code, message };

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
    {
        var res = req.CreateCorsResponse(status);
        await res.WriteAsJsonAsync(body);
        return res;
    }
}
