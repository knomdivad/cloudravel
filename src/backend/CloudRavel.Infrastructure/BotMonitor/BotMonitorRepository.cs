using System.Text.Json;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using Dapper;

namespace CloudRavel.Infrastructure.BotMonitor;

public sealed class BotMonitorRepository : IBotMonitorRepository
{
    private readonly ITenantDbConnectionFactory _db;

    public BotMonitorRepository(ITenantDbConnectionFactory db) => _db = db;

    public async Task<BotMonitorTenant?> GetTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<BotMonitorTenant>(
            "SELECT tenant_id AS TenantId, name AS Name, created_at AS CreatedAt FROM bot_monitor_tenants WHERE tenant_id = @tenantId",
            new { tenantId });
    }

    public async Task<IReadOnlyList<BotMonitorTenant>> ListTenantsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var rows = await conn.QueryAsync<BotMonitorTenant>(
            "SELECT tenant_id AS TenantId, name AS Name, created_at AS CreatedAt FROM bot_monitor_tenants ORDER BY name");
        return rows.ToList();
    }

    public async Task<BotMonitorTenant> CreateTenantAsync(string name, CancellationToken ct = default)
    {
        var tenant = new BotMonitorTenant { TenantId = Guid.NewGuid(), Name = name.Trim(), CreatedAt = DateTime.UtcNow };
        await using var conn = await _db.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO bot_monitor_tenants (tenant_id, name) VALUES (@TenantId, @Name)",
            tenant);
        return tenant;
    }

    public async Task<BotMonitorBot?> GetBotByIdAsync(Guid tenantId, Guid botId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(BotSelectSql + " AND bot_id = @botId", new { botId });
        return row is null ? null : MapBotOnly(row);
    }

    public async Task<BotMonitorBot?> GetBotByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            BotSelectSql + " AND token_hash = @tokenHash AND revoked_at IS NULL",
            new { tokenHash });
        return row is null ? null : MapBotOnly(row);
    }

    public async Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        const string sql = """
            SELECT b.bot_id AS BotId, b.tenant_id AS TenantId, b.display_name AS DisplayName,
                   b.platform AS Platform, b.token_hash AS TokenHash, b.hmac_secret_enc AS HmacSecretEnc,
                   b.checkin_interval_minutes AS CheckinIntervalMinutes, b.created_at AS CreatedAt, b.revoked_at AS RevokedAt,
                   s.last_seen_at AS LastSeenAt, s.last_status AS LastStatus, s.last_error AS LastError, s.last_error_at AS LastErrorAt,
                   (SELECT TOP 1 keep_check FROM bot_monitor_heartbeats h WHERE h.bot_id = b.bot_id ORDER BY received_at DESC) AS LastKeepCheck
            FROM bot_monitor_bots b
            LEFT JOIN bot_monitor_bot_state s ON s.bot_id = b.bot_id
            WHERE b.tenant_id = @tenantId
            ORDER BY b.display_name
            """;
        var rows = await conn.QueryAsync<dynamic>(sql, new { tenantId });
        return rows.Select(MapBotRow).ToList();
    }

    public async Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>> ListAllBotsAsync(
        Guid? filterTenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var sql = """
            SELECT b.bot_id AS BotId, b.tenant_id AS TenantId, b.display_name AS DisplayName,
                   b.platform AS Platform, b.token_hash AS TokenHash, b.hmac_secret_enc AS HmacSecretEnc,
                   b.checkin_interval_minutes AS CheckinIntervalMinutes, b.created_at AS CreatedAt, b.revoked_at AS RevokedAt,
                   s.last_seen_at AS LastSeenAt, s.last_status AS LastStatus, s.last_error AS LastError, s.last_error_at AS LastErrorAt,
                   t.name AS TenantName,
                   (SELECT TOP 1 keep_check FROM bot_monitor_heartbeats h WHERE h.bot_id = b.bot_id ORDER BY received_at DESC) AS LastKeepCheck
            FROM bot_monitor_bots b
            INNER JOIN bot_monitor_tenants t ON t.tenant_id = b.tenant_id
            LEFT JOIN bot_monitor_bot_state s ON s.bot_id = b.bot_id
            WHERE (@filterTenantId IS NULL OR b.tenant_id = @filterTenantId)
            ORDER BY t.name, b.display_name
            """;
        var rows = await conn.QueryAsync<dynamic>(sql, new { filterTenantId });
        var list = new List<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>();
        foreach (var r in rows)
        {
            var mapped = MapBotRow(r);
            list.Add((mapped.Bot, mapped.State, mapped.LastKeepCheck, (string)r.TenantName));
        }

        return list;
    }

    public async Task<BotMonitorBot> InsertBotAsync(BotMonitorBot bot, CancellationToken ct = default)
    {
        bot.BotId = bot.BotId == Guid.Empty ? Guid.NewGuid() : bot.BotId;
        bot.CheckinIntervalMinutes = Math.Clamp(bot.CheckinIntervalMinutes, 5, 24 * 60);
        await using var conn = await _db.CreateConnectionAsync(bot.TenantId);
        await conn.ExecuteAsync("""
            INSERT INTO bot_monitor_bots
                (bot_id, tenant_id, display_name, platform, token_hash, hmac_secret_enc, checkin_interval_minutes)
            VALUES (@BotId, @TenantId, @DisplayName, @PlatformDb, @TokenHash, @HmacSecretEnc, @CheckinIntervalMinutes)
            """, new
        {
            bot.BotId,
            bot.TenantId,
            bot.DisplayName,
            PlatformDb = PlatformToDb(bot.Platform),
            bot.TokenHash,
            bot.HmacSecretEnc,
            bot.CheckinIntervalMinutes,
        });
        return bot;
    }

    public async Task<bool> RevokeBotAsync(Guid tenantId, Guid botId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var n = await conn.ExecuteAsync(
            "UPDATE bot_monitor_bots SET revoked_at = SYSUTCDATETIME() WHERE bot_id = @botId AND tenant_id = @tenantId AND revoked_at IS NULL",
            new { botId, tenantId });
        return n > 0;
    }

    public async Task<bool> UpdateBotCredentialsAsync(
        Guid tenantId, Guid botId, string newTokenHash, string newHmacSecretEnc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var n = await conn.ExecuteAsync(
            "UPDATE bot_monitor_bots SET token_hash = @newTokenHash, hmac_secret_enc = @newHmacSecretEnc "
            + "WHERE bot_id = @botId AND tenant_id = @tenantId AND revoked_at IS NULL",
            new { botId, tenantId, newTokenHash, newHmacSecretEnc });
        return n > 0;
    }

    public async Task<bool> TryRecordNonceAsync(Guid botId, long ts, string sig, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        try
        {
            var n = await conn.ExecuteAsync(
                "INSERT INTO bot_monitor_nonces (bot_id, ts, sig) VALUES (@botId, @ts, @sig)",
                new { botId, ts, sig });
            return n > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public async Task PurgeExpiredNoncesAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM bot_monitor_nonces WHERE created_at < @olderThanUtc", new { olderThanUtc });
    }

    public async Task<bool> TryIncrementHeartbeatRateAsync(Guid botId, DateTime nowUtc, int maxPerMinute, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(DateTime WindowStart, int RequestCount)>(
            "SELECT window_start AS WindowStart, request_count AS RequestCount FROM bot_monitor_heartbeat_rate WHERE bot_id = @botId",
            new { botId });

        if (row.WindowStart == default || nowUtc - row.WindowStart >= TimeSpan.FromMinutes(1))
        {
            await conn.ExecuteAsync("""
                MERGE bot_monitor_heartbeat_rate AS t
                USING (SELECT @botId AS bot_id) AS s ON t.bot_id = s.bot_id
                WHEN MATCHED THEN UPDATE SET window_start = @nowUtc, request_count = 1
                WHEN NOT MATCHED THEN INSERT (bot_id, window_start, request_count) VALUES (@botId, @nowUtc, 1);
                """, new { botId, nowUtc });
            return true;
        }

        if (row.RequestCount >= maxPerMinute) return false;

        await conn.ExecuteAsync(
            "UPDATE bot_monitor_heartbeat_rate SET request_count = request_count + 1 WHERE bot_id = @botId",
            new { botId });
        return true;
    }

    public async Task IngestHeartbeatAsync(
        Guid tenantId, Guid botId, BotMonitorHeartbeatPayload payload, DateTime receivedAtUtc, CancellationToken ct = default)
    {
        var errorsJson = payload.Errors is { Count: > 0 }
            ? JsonSerializer.Serialize(payload.Errors.Take(20))
            : null;
        var lastError = payload.Errors?.FirstOrDefault();
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        await conn.ExecuteAsync("""
            INSERT INTO bot_monitor_heartbeats
                (bot_id, tenant_id, received_at, status, last_task, errors, keep_check, keep_check_detail)
            VALUES (@botId, @tenantId, @receivedAtUtc, @statusDb, @lastTask, @errorsJson, @keepDb, @keepDetail)
            """, new
        {
            botId,
            tenantId,
            receivedAtUtc,
            statusDb = StatusToDb(payload.Status),
            lastTask = payload.LastTask?.Length > 500 ? payload.LastTask[..500] : payload.LastTask,
            errorsJson,
            keepDb = KeepToDb(payload.KeepCheck),
            keepDetail = payload.KeepCheckDetail?.Length > 1000 ? payload.KeepCheckDetail[..1000] : payload.KeepCheckDetail,
        });

        await conn.ExecuteAsync("""
            MERGE bot_monitor_bot_state AS t
            USING (SELECT @botId AS bot_id) AS s ON t.bot_id = s.bot_id
            WHEN MATCHED THEN UPDATE SET
                last_seen_at = @receivedAtUtc,
                last_status = @statusDb,
                last_error = CASE WHEN @statusDb = 'error' THEN @lastError ELSE last_error END,
                last_error_at = CASE WHEN @statusDb = 'error' THEN @receivedAtUtc ELSE last_error_at END
            WHEN NOT MATCHED THEN INSERT (bot_id, tenant_id, last_seen_at, last_status, last_error, last_error_at)
                VALUES (@botId, @tenantId, @receivedAtUtc, @statusDb, @lastError, CASE WHEN @statusDb = 'error' THEN @receivedAtUtc ELSE NULL END);
            """, new
        {
            botId,
            tenantId,
            receivedAtUtc,
            statusDb = StatusToDb(payload.Status),
            lastError = lastError?.Length > 500 ? lastError[..500] : lastError,
        });
    }

    public async Task<IReadOnlyList<BotMonitorWeeklyBucket>> GetWeeklyHistoryAsync(
        Guid tenantId, Guid botId, int weeks, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var interval = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT checkin_interval_minutes FROM bot_monitor_bots WHERE bot_id = @botId",
            new { botId }) ?? 60;

        var buckets = BotMonitorWeeklyAggregator.BuildEmptyWeeks(weeks, nowUtc).ToDictionary(
            b => (b.IsoYear, b.IsoWeek));

        var since = nowUtc.AddDays(-7 * weeks);
        var rows = await conn.QueryAsync<(DateTime ReceivedAt, string Status, string KeepCheck)>(
            "SELECT received_at AS ReceivedAt, status AS Status, keep_check AS KeepCheck "
            + "FROM bot_monitor_heartbeats WHERE bot_id = @botId AND received_at >= @since",
            new { botId, since });

        foreach (var row in rows)
        {
            var (y, w) = BotMonitorWeeklyAggregator.IsoWeekOf(row.ReceivedAt);
            if (!buckets.TryGetValue((y, w), out var bucket)) continue;
            BotMonitorWeeklyAggregator.ApplyHeartbeat(
                bucket,
                row.ReceivedAt,
                ParseStatus(row.Status),
                ParseKeep(row.KeepCheck),
                interval);
        }

        return buckets.Values.OrderByDescending(b => b.IsoYear).ThenByDescending(b => b.IsoWeek).ToList();
    }

    public async Task<string?> StoreMagicLinkAsync(Guid tenantId, string tokenHash, DateTime expiresAtUtc, CancellationToken ct = default)
    {
        var linkId = Guid.NewGuid();
        await using var conn = await _db.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO bot_monitor_magic_links (link_id, tenant_id, token_hash, expires_at) VALUES (@linkId, @tenantId, @tokenHash, @expiresAtUtc)",
            new { linkId, tenantId, tokenHash, expiresAtUtc });
        return linkId.ToString("N");
    }

    public async Task<Guid?> RedeemMagicLinkAsync(string tokenHash, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid TenantId, DateTime ExpiresAt, DateTime? RedeemedAt)>(
            "SELECT TOP 1 tenant_id AS TenantId, expires_at AS ExpiresAt, redeemed_at AS RedeemedAt "
            + "FROM bot_monitor_magic_links WHERE token_hash = @tokenHash ORDER BY created_at DESC",
            new { tokenHash });
        if (row.TenantId == default || row.RedeemedAt != null || row.ExpiresAt < nowUtc) return null;
        await conn.ExecuteAsync(
            "UPDATE bot_monitor_magic_links SET redeemed_at = @nowUtc WHERE token_hash = @tokenHash AND redeemed_at IS NULL",
            new { tokenHash, nowUtc });
        return row.TenantId;
    }

    private const string BotSelectSql = """
        SELECT bot_id AS BotId, tenant_id AS TenantId, display_name AS DisplayName,
               platform AS Platform, token_hash AS TokenHash, hmac_secret_enc AS HmacSecretEnc,
               checkin_interval_minutes AS CheckinIntervalMinutes, created_at AS CreatedAt, revoked_at AS RevokedAt
        FROM bot_monitor_bots
        WHERE 1=1
        """;

    private static BotMonitorBot MapBotOnly(dynamic r) => new()
    {
        BotId = r.BotId,
        TenantId = r.TenantId,
        DisplayName = r.DisplayName,
        Platform = ParsePlatform((string)r.Platform),
        TokenHash = r.TokenHash,
        HmacSecretEnc = r.HmacSecretEnc,
        CheckinIntervalMinutes = r.CheckinIntervalMinutes,
        CreatedAt = r.CreatedAt,
        RevokedAt = r.RevokedAt,
    };

    private static (BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck) MapBotRow(dynamic r)
    {
        var bot = new BotMonitorBot
        {
            BotId = r.BotId,
            TenantId = r.TenantId,
            DisplayName = r.DisplayName,
            Platform = ParsePlatform((string)r.Platform),
            TokenHash = r.TokenHash,
            HmacSecretEnc = r.HmacSecretEnc,
            CheckinIntervalMinutes = r.CheckinIntervalMinutes,
            CreatedAt = r.CreatedAt,
            RevokedAt = r.RevokedAt,
        };
        BotMonitorBotState? state = r.LastSeenAt is null && r.LastStatus is null
            ? null
            : new BotMonitorBotState
            {
                BotId = bot.BotId,
                TenantId = bot.TenantId,
                LastSeenAt = r.LastSeenAt,
                LastStatus = r.LastStatus is null ? null : ParseStatus((string)r.LastStatus),
                LastError = r.LastError,
                LastErrorAt = r.LastErrorAt,
            };
        return (bot, state, r.LastKeepCheck as string);
    }

    private static string PlatformToDb(BotMonitorPlatform p) => p switch
    {
        BotMonitorPlatform.GrokBot => "grok_bot",
        BotMonitorPlatform.Email => "email",
        _ => "other",
    };

    private static BotMonitorPlatform ParsePlatform(string db) => db switch
    {
        "grok_bot" => BotMonitorPlatform.GrokBot,
        "email" => BotMonitorPlatform.Email,
        _ => BotMonitorPlatform.Other,
    };

    private static string StatusToDb(BotMonitorStatus s) => s switch
    {
        BotMonitorStatus.Busy => "busy",
        BotMonitorStatus.Error => "error",
        BotMonitorStatus.Offline => "offline",
        _ => "ok",
    };

    private static BotMonitorStatus ParseStatus(string db) => db switch
    {
        "busy" => BotMonitorStatus.Busy,
        "error" => BotMonitorStatus.Error,
        "offline" => BotMonitorStatus.Offline,
        _ => BotMonitorStatus.Ok,
    };

    private static string KeepToDb(BotMonitorKeepCheck k) => k switch
    {
        BotMonitorKeepCheck.Pass => "pass",
        BotMonitorKeepCheck.Warn => "warn",
        BotMonitorKeepCheck.Fail => "fail",
        _ => "none",
    };

    private static BotMonitorKeepCheck ParseKeep(string db) => db switch
    {
        "pass" => BotMonitorKeepCheck.Pass,
        "warn" => BotMonitorKeepCheck.Warn,
        "fail" => BotMonitorKeepCheck.Fail,
        _ => BotMonitorKeepCheck.None,
    };
}
