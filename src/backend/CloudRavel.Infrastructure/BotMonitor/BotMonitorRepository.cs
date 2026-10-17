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

    public async Task<BotMonitorTenant?> LinkOrganizationAsync(Guid organizationId, string organizationName, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var orgExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM organizations WHERE org_id = @organizationId",
            new { organizationId });
        if (orgExists == 0) return null;

        var existing = await GetTenantAsync(organizationId, ct);
        if (existing != null) return existing;

        var tenant = new BotMonitorTenant
        {
            TenantId = organizationId,
            Name = organizationName.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        await conn.ExecuteAsync(
            "INSERT INTO bot_monitor_tenants (tenant_id, name) VALUES (@TenantId, @Name)",
            tenant);
        return tenant;
    }

    public async Task<BotMonitorBot?> GetBotByIdAsync(Guid tenantId, Guid botId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var row = await conn.QuerySingleOrDefaultAsync<BotMonitorBotOnlyRow>(
            BotSelectSql + " AND bot_id = @botId AND tenant_id = @tenantId",
            new { botId, tenantId });
        return row is null ? null : MapBotOnly(row);
    }

    public async Task<BotMonitorBot?> GetBotByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<BotMonitorBotOnlyRow>(
            BotSelectSql + " AND token_hash = @tokenHash AND revoked_at IS NULL",
            new { tokenHash });
        return row is null ? null : MapBotOnly(row);
    }

    public async Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantAsync(
        Guid tenantId, Guid? ownerUserId = null, string? botTypeKey = null, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        const string sql = """
            SELECT b.bot_id AS BotId, b.tenant_id AS TenantId, b.display_name AS DisplayName,
                   b.platform AS Platform, b.bot_type_key AS BotTypeKey, bt.display_name AS BotTypeDisplayName,
                   b.token_hash AS TokenHash, b.hmac_secret_enc AS HmacSecretEnc,
                   b.checkin_interval_minutes AS CheckinIntervalMinutes, b.created_at AS CreatedAt, b.revoked_at AS RevokedAt,
                   b.owner_user_id AS OwnerUserId,
                   u.display_name AS OwnerDisplayName,
                   s.last_seen_at AS LastSeenAt, s.last_status AS LastStatus, s.last_error AS LastError, s.last_error_at AS LastErrorAt,
                   (SELECT TOP 1 keep_check FROM bot_monitor_heartbeats h
                    WHERE h.bot_id = b.bot_id AND h.tenant_id = b.tenant_id ORDER BY received_at DESC) AS LastKeepCheck
            FROM bot_monitor_bots b
            INNER JOIN bot_monitor_bot_types bt ON bt.type_key = b.bot_type_key
            LEFT JOIN users u ON u.user_id = b.owner_user_id
            LEFT JOIN bot_monitor_bot_state s ON s.bot_id = b.bot_id AND s.tenant_id = b.tenant_id
            WHERE b.tenant_id = @tenantId
              AND b.revoked_at IS NULL
              AND (@ownerUserId IS NULL OR b.owner_user_id = @ownerUserId)
              AND (@botTypeKey IS NULL OR b.bot_type_key = @botTypeKey)
            ORDER BY b.display_name
            """;
        var rows = await conn.QueryAsync<BotMonitorBotListRow>(sql, new { tenantId, ownerUserId, botTypeKey });
        return rows.Select(r => MapBotRow(r)).ToList();
    }

    public async Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>> ListAllBotsAsync(
        Guid? filterTenantId, string? botTypeKey = null, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var sql = """
            SELECT b.bot_id AS BotId, b.tenant_id AS TenantId, b.display_name AS DisplayName,
                   b.platform AS Platform, b.bot_type_key AS BotTypeKey, bt.display_name AS BotTypeDisplayName,
                   b.token_hash AS TokenHash, b.hmac_secret_enc AS HmacSecretEnc,
                   b.checkin_interval_minutes AS CheckinIntervalMinutes, b.created_at AS CreatedAt, b.revoked_at AS RevokedAt,
                   b.owner_user_id AS OwnerUserId,
                   s.last_seen_at AS LastSeenAt, s.last_status AS LastStatus, s.last_error AS LastError, s.last_error_at AS LastErrorAt,
                   t.name AS TenantName,
                   (SELECT TOP 1 keep_check FROM bot_monitor_heartbeats h
                    WHERE h.bot_id = b.bot_id AND h.tenant_id = b.tenant_id ORDER BY received_at DESC) AS LastKeepCheck
            FROM bot_monitor_bots b
            INNER JOIN bot_monitor_tenants t ON t.tenant_id = b.tenant_id
            INNER JOIN bot_monitor_bot_types bt ON bt.type_key = b.bot_type_key
            LEFT JOIN bot_monitor_bot_state s ON s.bot_id = b.bot_id AND s.tenant_id = b.tenant_id
            WHERE b.revoked_at IS NULL
              AND (@filterTenantId IS NULL OR b.tenant_id = @filterTenantId)
              AND (@botTypeKey IS NULL OR b.bot_type_key = @botTypeKey)
            ORDER BY t.name, b.display_name
            """;
        var rows = await conn.QueryAsync<BotMonitorBotListRow>(sql, new { filterTenantId, botTypeKey });
        var list = new List<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>();
        foreach (var row in rows)
        {
            var mapped = MapBotRow(row);
            list.Add((mapped.Bot, mapped.State, mapped.LastKeepCheck, row.TenantName ?? string.Empty));
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
                (bot_id, tenant_id, display_name, platform, bot_type_key, token_hash, hmac_secret_enc, checkin_interval_minutes, owner_user_id)
            VALUES (@BotId, @TenantId, @DisplayName, @PlatformDb, @BotTypeKey, @TokenHash, @HmacSecretEnc, @CheckinIntervalMinutes, @OwnerUserId)
            """, new
        {
            bot.BotId,
            bot.TenantId,
            bot.DisplayName,
            PlatformDb = PlatformToDb(bot.Platform),
            BotTypeKey = string.IsNullOrWhiteSpace(bot.BotTypeKey) ? "other_plugin" : bot.BotTypeKey.Trim(),
            bot.TokenHash,
            bot.HmacSecretEnc,
            bot.CheckinIntervalMinutes,
            bot.OwnerUserId,
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
        string? metricsJson = payload.Metrics is null
            ? null
            : JsonSerializer.Serialize(payload.Metrics);
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        await conn.ExecuteAsync("""
            INSERT INTO bot_monitor_heartbeats
                (bot_id, tenant_id, received_at, status, last_task, errors, keep_check, keep_check_detail, reported_metrics)
            VALUES (@botId, @tenantId, @receivedAtUtc, @statusDb, @lastTask, @errorsJson, @keepDb, @keepDetail, @metricsJson)
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
            metricsJson,
        });

        await conn.ExecuteAsync("""
            MERGE bot_monitor_bot_state AS t
            USING (SELECT @botId AS bot_id, @tenantId AS tenant_id) AS s
            ON t.bot_id = s.bot_id AND t.tenant_id = s.tenant_id
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
            "SELECT checkin_interval_minutes FROM bot_monitor_bots WHERE bot_id = @botId AND tenant_id = @tenantId",
            new { botId, tenantId }) ?? 60;

        var buckets = BotMonitorWeeklyAggregator.BuildEmptyWeeks(weeks, nowUtc).ToDictionary(
            b => (b.IsoYear, b.IsoWeek));

        var since = nowUtc.AddDays(-(double)weeks * 7.0);
        var rows = await conn.QueryAsync<(DateTime ReceivedAt, string Status, string KeepCheck)>(
            "SELECT received_at AS ReceivedAt, status AS Status, keep_check AS KeepCheck "
            + "FROM bot_monitor_heartbeats WHERE bot_id = @botId AND tenant_id = @tenantId AND received_at >= @since",
            new { botId, tenantId, since });

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

    public async Task<IReadOnlyList<(DateTime ReceivedAt, string Status)>> GetRecentCheckinsAsync(
        Guid tenantId, Guid botId, int hours, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var since = nowUtc.AddHours(-Math.Clamp(hours, 1, 168));
        var rows = await conn.QueryAsync<(DateTime ReceivedAt, string Status)>(
            "SELECT received_at AS ReceivedAt, status AS Status FROM bot_monitor_heartbeats "
            + "WHERE tenant_id = @tenantId AND bot_id = @botId AND received_at >= @since ORDER BY received_at ASC",
            new { tenantId, botId, since });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<(Guid TenantId, string TenantName, int BotCount, int OnlineCount)>> ListFleetTenantSummariesAsync(
        DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var rows = await conn.QueryAsync<(Guid TenantId, string TenantName, Guid? BotId, int CheckinIntervalMinutes, DateTime? LastSeenAt)>(
            """
            SELECT t.tenant_id AS TenantId, t.name AS TenantName,
                   b.bot_id AS BotId,
                   b.checkin_interval_minutes AS CheckinIntervalMinutes,
                   s.last_seen_at AS LastSeenAt
            FROM bot_monitor_tenants t
            LEFT JOIN bot_monitor_bots b ON b.tenant_id = t.tenant_id AND b.revoked_at IS NULL
            LEFT JOIN bot_monitor_bot_state s ON s.bot_id = b.bot_id AND s.tenant_id = b.tenant_id
            ORDER BY t.name, b.display_name
            """);
        var grouped = new Dictionary<Guid, (string Name, int BotCount, int OnlineCount)>();
        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.TenantId, out var acc))
                acc = (row.TenantName, 0, 0);

            if (row.BotId.HasValue)
            {
                acc.BotCount++;
                var presence = BotMonitorStaleEvaluator.Evaluate(
                    row.LastSeenAt,
                    row.CheckinIntervalMinutes > 0 ? row.CheckinIntervalMinutes : 60,
                    nowUtc);
                if (presence == BotMonitorPresence.Online) acc.OnlineCount++;
            }

            grouped[row.TenantId] = acc;
        }

        return grouped.Select(kv => (kv.Key, kv.Value.Name, kv.Value.BotCount, kv.Value.OnlineCount)).OrderBy(x => x.Name).ToList();
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string Email, int BotCount)>> ListFleetUsersForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var rows = await conn.QueryAsync<(Guid UserId, string DisplayName, string Email, int BotCount)>(
            """
            SELECT u.user_id AS UserId, u.display_name AS DisplayName, u.email AS Email,
                   COUNT(b.bot_id) AS BotCount
            FROM users u
            INNER JOIN user_tenant_access uta ON uta.user_id = u.user_id AND uta.tenant_id = @tenantId
            LEFT JOIN bot_monitor_bots b ON b.tenant_id = @tenantId AND b.owner_user_id = u.user_id AND b.revoked_at IS NULL
            WHERE uta.entitlement_bot_monitor = 1
            GROUP BY u.user_id, u.display_name, u.email
            ORDER BY u.display_name
            """,
            new { tenantId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantUserAsync(
        Guid tenantId, Guid userId, string? botTypeKey = null, CancellationToken ct = default)
        => await ListBotsForTenantAsync(tenantId, userId, botTypeKey, ct);

    public async Task<IReadOnlyList<BotMonitorBotType>> ListBotTypesAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        var sql = activeOnly
            ? "SELECT type_key AS TypeKey, display_name AS DisplayName, is_active AS IsActive, created_at AS CreatedAt FROM bot_monitor_bot_types WHERE is_active = 1 ORDER BY display_name"
            : "SELECT type_key AS TypeKey, display_name AS DisplayName, is_active AS IsActive, created_at AS CreatedAt FROM bot_monitor_bot_types ORDER BY display_name";
        var rows = await conn.QueryAsync<BotMonitorBotType>(sql);
        return rows.ToList();
    }

    public async Task<BotMonitorBotType?> GetBotTypeByKeyAsync(string typeKey, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateAdminConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<BotMonitorBotType>(
            "SELECT type_key AS TypeKey, display_name AS DisplayName, is_active AS IsActive, created_at AS CreatedAt FROM bot_monitor_bot_types WHERE type_key = @typeKey",
            new { typeKey = typeKey.Trim() });
    }

    public async Task<BotMonitorBotType> InsertBotTypeAsync(string typeKey, string displayName, CancellationToken ct = default)
    {
        var key = NormalizeTypeKey(typeKey);
        var name = displayName.Trim();
        if (name.Length > 128) name = name[..128];
        await using var conn = await _db.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO bot_monitor_bot_types (type_key, display_name, is_active) VALUES (@key, @name, 1)",
            new { key, name });
        return new BotMonitorBotType { TypeKey = key, DisplayName = name, IsActive = true, CreatedAt = DateTime.UtcNow };
    }

    public async Task<IReadOnlyList<(string TypeKey, string DisplayName, int Count)>> CountBotsByTypeAsync(
        Guid? tenantId, Guid? ownerUserId, string? botTypeKeyFilter, CancellationToken ct = default)
    {
        var conn = tenantId.HasValue
            ? await _db.CreateConnectionAsync(tenantId.Value)
            : await _db.CreateAdminConnectionAsync();
        await using var _ = conn;
        const string sql = """
            SELECT b.bot_type_key AS TypeKey, bt.display_name AS DisplayName, COUNT(*) AS Count
            FROM bot_monitor_bots b
            INNER JOIN bot_monitor_bot_types bt ON bt.type_key = b.bot_type_key
            WHERE b.revoked_at IS NULL
              AND (@tenantId IS NULL OR b.tenant_id = @tenantId)
              AND (@ownerUserId IS NULL OR b.owner_user_id = @ownerUserId)
              AND (@botTypeKeyFilter IS NULL OR b.bot_type_key = @botTypeKeyFilter)
            GROUP BY b.bot_type_key, bt.display_name
            ORDER BY Count DESC, bt.display_name
            """;
        var rows = await conn.QueryAsync<(string TypeKey, string DisplayName, int Count)>(sql,
            new { tenantId, ownerUserId, botTypeKeyFilter });
        return rows.ToList();
    }

    private static string NormalizeTypeKey(string raw)
    {
        var key = raw.Trim().ToLowerInvariant();
        if (key.Length > 64) key = key[..64];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("typeKey is required.");
        return key;
    }

    public async Task<BotMonitorHealthMetricsSnapshot?> GetBotMetricsSnapshotAsync(
        Guid tenantId, Guid botId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var interval = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT checkin_interval_minutes FROM bot_monitor_bots WHERE bot_id = @botId AND tenant_id = @tenantId",
            new { botId, tenantId }) ?? 60;

        var rows = await conn.QueryAsync<(DateTime ReceivedAt, string Status, string KeepCheck, string? MetricsJson)>(
            "SELECT TOP 50 received_at AS ReceivedAt, status AS Status, keep_check AS KeepCheck, reported_metrics AS MetricsJson "
            + "FROM bot_monitor_heartbeats WHERE bot_id = @botId AND tenant_id = @tenantId ORDER BY received_at DESC",
            new { botId, tenantId });

        var list = rows.ToList();
        if (list.Count == 0) return null;

        BotMonitorReportedMetrics? reported = null;
        if (!string.IsNullOrWhiteSpace(list[0].MetricsJson))
        {
            try
            {
                reported = JsonSerializer.Deserialize<BotMonitorReportedMetrics>(list[0].MetricsJson!);
            }
            catch
            {
                reported = null;
            }
        }

        var recent = list.Select(r => (
            r.ReceivedAt,
            ParseStatus(r.Status),
            ParseKeep(r.KeepCheck))).ToList();

        return new BotMonitorHealthMetricsSnapshot
        {
            Reported = reported,
            Derived = BotMonitorMetricsDeriver.Derive(reported, recent, interval, nowUtc),
            AsOfUtc = list[0].ReceivedAt,
        };
    }

    public Task<IReadOnlyList<(Guid BotId, string DisplayName, BotMonitorHealthMetricsSnapshot Snapshot)>> ListMetricsForTenantUserAsync(
        Guid tenantId, Guid ownerUserId, DateTime nowUtc, CancellationToken ct = default)
        => ListMetricsForTenantAsync(tenantId, ownerUserId, nowUtc, ct);

    public async Task<IReadOnlyList<(Guid BotId, string DisplayName, BotMonitorHealthMetricsSnapshot Snapshot)>> ListMetricsForTenantAsync(
        Guid tenantId, Guid? ownerUserId, DateTime nowUtc, CancellationToken ct = default)
    {
        var bots = await ListBotsForTenantAsync(tenantId, ownerUserId, botTypeKey: null, ct);
        var result = new List<(Guid, string, BotMonitorHealthMetricsSnapshot)>();
        foreach (var row in bots)
        {
            var snap = await GetBotMetricsSnapshotAsync(tenantId, row.Bot.BotId, nowUtc, ct)
                       ?? new BotMonitorHealthMetricsSnapshot { AsOfUtc = nowUtc };
            result.Add((row.Bot.BotId, row.Bot.DisplayName, snap));
        }

        return result;
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string Email, int BotCount)>> ListUserBotCountsForTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _db.CreateConnectionAsync(tenantId);
        var rows = await conn.QueryAsync<(Guid UserId, string DisplayName, string Email, int BotCount)>(
            """
            SELECT u.user_id AS UserId, u.display_name AS DisplayName, u.email AS Email,
                   COUNT(b.bot_id) AS BotCount
            FROM users u
            INNER JOIN user_tenant_access uta ON uta.user_id = u.user_id AND uta.tenant_id = @tenantId
            LEFT JOIN bot_monitor_bots b ON b.tenant_id = @tenantId AND b.owner_user_id = u.user_id AND b.revoked_at IS NULL
            WHERE uta.entitlement_bot_monitor = 1
            GROUP BY u.user_id, u.display_name, u.email
            ORDER BY u.display_name
            """,
            new { tenantId });
        return rows.ToList();
    }

    private const string BotSelectSql = """
        SELECT bot_id AS BotId, tenant_id AS TenantId, display_name AS DisplayName,
               platform AS Platform, bot_type_key AS BotTypeKey, token_hash AS TokenHash, hmac_secret_enc AS HmacSecretEnc,
               checkin_interval_minutes AS CheckinIntervalMinutes, created_at AS CreatedAt, revoked_at AS RevokedAt,
               owner_user_id AS OwnerUserId
        FROM bot_monitor_bots
        WHERE 1=1
        """;

    private static BotMonitorBot MapBotOnly(BotMonitorBotOnlyRow r) => new()
    {
        BotId = r.BotId,
        TenantId = r.TenantId,
        DisplayName = r.DisplayName,
        Platform = ParsePlatform(r.Platform),
        TokenHash = r.TokenHash,
        HmacSecretEnc = r.HmacSecretEnc,
        CheckinIntervalMinutes = r.CheckinIntervalMinutes,
        CreatedAt = r.CreatedAt,
        RevokedAt = r.RevokedAt,
        OwnerUserId = r.OwnerUserId,
        BotTypeKey = string.IsNullOrWhiteSpace(r.BotTypeKey) ? "other_plugin" : r.BotTypeKey,
    };

    private sealed class BotMonitorBotOnlyRow
    {
        public Guid BotId { get; set; }
        public Guid TenantId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string? BotTypeKey { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public string HmacSecretEnc { get; set; } = string.Empty;
        public int CheckinIntervalMinutes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public Guid? OwnerUserId { get; set; }
    }

    private static (BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck) MapBotRow(BotMonitorBotListRow r)
    {
        var bot = new BotMonitorBot
        {
            BotId = r.BotId,
            TenantId = r.TenantId,
            DisplayName = r.DisplayName,
            Platform = ParsePlatform(r.Platform),
            TokenHash = r.TokenHash,
            HmacSecretEnc = r.HmacSecretEnc,
            CheckinIntervalMinutes = r.CheckinIntervalMinutes,
            CreatedAt = r.CreatedAt,
            RevokedAt = r.RevokedAt,
            OwnerUserId = r.OwnerUserId,
            OwnerDisplayName = r.OwnerDisplayName,
            BotTypeKey = string.IsNullOrWhiteSpace(r.BotTypeKey) ? "other_plugin" : r.BotTypeKey,
            BotTypeDisplayName = r.BotTypeDisplayName,
        };
        BotMonitorBotState? state = r.LastSeenAt is null && r.LastStatus is null
            ? null
            : new BotMonitorBotState
            {
                BotId = bot.BotId,
                TenantId = bot.TenantId,
                LastSeenAt = r.LastSeenAt,
                LastStatus = r.LastStatus is null ? null : ParseStatus(r.LastStatus),
                LastError = r.LastError,
                LastErrorAt = r.LastErrorAt,
            };
        return (bot, state, r.LastKeepCheck);
    }

    private sealed class BotMonitorBotListRow
    {
        public Guid BotId { get; set; }
        public Guid TenantId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string? BotTypeKey { get; set; }
        public string? BotTypeDisplayName { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public string HmacSecretEnc { get; set; } = string.Empty;
        public int CheckinIntervalMinutes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public Guid? OwnerUserId { get; set; }
        public string? OwnerDisplayName { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public string? LastStatus { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorAt { get; set; }
        public string? LastKeepCheck { get; set; }
        public string? TenantName { get; set; }
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
