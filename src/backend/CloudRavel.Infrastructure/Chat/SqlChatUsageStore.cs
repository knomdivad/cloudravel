using System.Data.Common;

using CloudRavel.Core.Interfaces;

using Dapper;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// SQL-backed <see cref="IChatUsageStore"/> for customer-chat abuse caps.
///
/// Deliberately unglamorous: one table, one atomic upsert per check, the same
/// tradeoff the rest of this codebase makes (SQL Queue over Service Bus, SQL
/// rate limiting over Redis). Costs one round-trip per chat request against a
/// connection that already exists in the deployment; correct on any number of
/// API instances. If chat volume ever outgrows it, the interface is the
/// swap-point (Redis/et al. implement the same four methods).
///
/// The table is NOT tenant-scoped (keys embed tenant ids as opaque strings, no
/// tenant data rows), so it lives outside the RLS policy like system_settings
/// and is accessed through the admin (system) connection — abuse accounting is
/// instance-wide infrastructure state, not workspace data.
/// </summary>
public sealed class SqlChatUsageStore : IChatUsageStore
{
    private readonly Func<Task<DbConnection>> _adminConnection;

    /// <param name="adminConnectionFactory">
    /// The shared <see cref="ITenantDbConnectionFactory"/>; only its admin
    /// connection is used (system counters, never tenant rows).
    /// </param>
    public SqlChatUsageStore(ITenantDbConnectionFactory adminConnectionFactory)
    {
        _adminConnection = adminConnectionFactory.CreateAdminConnectionAsync;
    }

    public async Task<long> IncrementWindowAsync(string counterKey, DateTime nowUtc, TimeSpan window)
    {
        const string sql = """
            UPDATE chat_usage_counters
               SET counter_count = CASE WHEN window_start < @Cutoff THEN 1 ELSE counter_count + 1 END,
                   window_start  = CASE WHEN window_start < @Cutoff THEN @Now ELSE window_start END,
                   updated_at    = @Now
             WHERE counter_key = @Key;

            IF @@ROWCOUNT = 0
                INSERT INTO chat_usage_counters (counter_key, window_start, counter_count, updated_at)
                VALUES (@Key, @Now, 1, @Now);

            SELECT counter_count FROM chat_usage_counters WHERE counter_key = @Key;
            """;

        await using var conn = await _adminConnection();
        return await conn.QuerySingleAsync<long>(new CommandDefinition(sql, new
        {
            Key = counterKey,
            Now = nowUtc,
            Cutoff = nowUtc - window,
        }));
    }

    public async Task<long> ReadCountAsync(string counterKey)
    {
        const string sql = """
            SELECT ISNULL((SELECT counter_count FROM chat_usage_counters
                WHERE counter_key = @Key AND window_start >= @Cutoff), 0);
            """;

        await using var conn = await _adminConnection();
        return await conn.QuerySingleAsync<long>(new CommandDefinition(sql, new
        {
            Key = counterKey,
            Cutoff = DateTime.UtcNow - WindowFallback,
        }));
    }

    public async Task AddCountAsync(string counterKey, DateTime nowUtc, long delta)
    {
        const string sql = """
            UPDATE chat_usage_counters
               SET counter_count = counter_count + @Delta,
                   updated_at    = @Now
             WHERE counter_key = @Key;

            IF @@ROWCOUNT = 0
                INSERT INTO chat_usage_counters (counter_key, window_start, counter_count, updated_at)
                VALUES (@Key, @Now, @Delta, @Now);
            """;

        await using var conn = await _adminConnection();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Key = counterKey,
            Now = nowUtc,
            Delta = delta,
        }));
    }

    public async Task ClearAsync(string keyPrefix)
    {
        const string sql = "DELETE FROM chat_usage_counters WHERE counter_key LIKE @Pattern + '%';";

        await using var conn = await _adminConnection();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Pattern = keyPrefix }));
    }

    /// <summary>ReadCountAsync has no window of its own; accept any recent row.</summary>
    private static readonly TimeSpan WindowFallback = TimeSpan.FromDays(1);
}
