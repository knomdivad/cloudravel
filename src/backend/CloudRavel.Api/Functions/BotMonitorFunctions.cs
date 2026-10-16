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
    private readonly BotMonitorCredentialService _credentials;
    private readonly BotMonitorHeartbeatService _heartbeat;
    private readonly IConfiguration _config;
    private readonly ILogger<BotMonitorFunctions> _logger;
    private readonly Func<DateTime> _clock;

    public BotMonitorFunctions(
        IBotMonitorRepository repo,
        BotMonitorCredentialService credentials,
        BotMonitorHeartbeatService heartbeat,
        IConfiguration config,
        ILogger<BotMonitorFunctions> logger)
    {
        _repo = repo;
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
            BotMonitorHeartbeatService.HeartbeatRejectReason.TimestampSkew =>
                await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("TIMESTAMP_SKEW", "Timestamp outside the allowed window.")),
            BotMonitorHeartbeatService.HeartbeatRejectReason.BadSignature =>
                await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("BAD_SIGNATURE", "Signature verification failed.")),
            _ => await WriteJsonAsync(req, HttpStatusCode.Unauthorized, Error("UNAUTHORIZED", "Invalid or revoked bot token.")),
        };
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

        var now = _clock();
        var rows = await _repo.ListAllBotsAsync(filter);
        var dto = rows.Select(r => ToListItem(r.Bot, r.State, r.LastKeepCheck, r.TenantName, now)).ToList();
        return await WriteJsonAsync(req, HttpStatusCode.OK, new { bots = dto });
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

        var tenantId = context.GetTenantId();
        if (tenantId == Guid.Empty)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("MISSING_WORKSPACE", "Select an organization before opening the customer bot view."));

        var forged = qTenantOverride(req);
        if (forged.HasValue && forged != tenantId)
            return await WriteJsonAsync(req, HttpStatusCode.Forbidden, Error("TENANT_FORBIDDEN", "Tenant cannot be overridden on this route."));

        var now = _clock();
        var rows = await _repo.ListBotsForTenantAsync(tenantId);
        var bots = new List<BotMonitorPortalBotDto>();
        foreach (var row in rows)
        {
            var history = await _repo.GetWeeklyHistoryAsync(tenantId, row.Bot.BotId, 8, now);
            bots.Add(new BotMonitorPortalBotDto
            {
                BotId = row.Bot.BotId,
                DisplayName = row.Bot.DisplayName,
                Platform = row.Bot.Platform.ToString(),
                Presence = BotMonitorStaleEvaluator.Evaluate(row.State?.LastSeenAt, row.Bot.CheckinIntervalMinutes, now).ToString(),
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
            });
        }

        return await WriteJsonAsync(req, HttpStatusCode.OK, new { bots });
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

        if (!Guid.TryParse(botId, out var botGuid))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_ID", "Invalid bot id."));

        var tenantId = context.GetTenantId();
        var forged = qTenantOverride(req);
        if (forged.HasValue && forged != tenantId)
            return await WriteJsonAsync(req, HttpStatusCode.Forbidden, Error("TENANT_FORBIDDEN", "Tenant cannot be overridden on this route."));

        var bot = await _repo.GetBotByIdAsync(tenantId, botGuid);
        if (bot is null)
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, Error("NOT_FOUND", "Bot not found in this organization."));

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

        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("INVALID_BODY", "Name is required."));

        var tenant = await _repo.CreateTenantAsync(body.Name);
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

        if (await _repo.GetTenantAsync(body.TenantId) is null)
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, Error("UNKNOWN_TENANT", "Bot monitor tenant not found."));

        var mint = _credentials.MintCredentials();
        var bot = await _repo.InsertBotAsync(new BotMonitorBot
        {
            TenantId = body.TenantId,
            DisplayName = body.DisplayName,
            Platform = body.Platform,
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

    private BotMonitorBotListItemDto ToListItem(
        BotMonitorBot bot, BotMonitorBotState? state, string? lastKeep, string tenantName, DateTime now) =>
        new()
        {
            BotId = bot.BotId,
            TenantId = bot.TenantId,
            TenantName = tenantName,
            DisplayName = bot.DisplayName,
            Platform = bot.Platform.ToString(),
            Presence = BotMonitorStaleEvaluator.Evaluate(state?.LastSeenAt, bot.CheckinIntervalMinutes, now).ToString(),
            LastSeenAt = state?.LastSeenAt,
            LastError = state?.LastError,
            LastKeepCheck = lastKeep,
            RevokedAt = bot.RevokedAt,
        };

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
