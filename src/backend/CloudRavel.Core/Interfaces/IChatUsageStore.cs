namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Durable, deployment-shared counters for customer-chat abuse caps (rate
/// windows and daily token usage). Implementations back onto a store shared by
/// every API instance — the SQL database — so per-user/per-tenant limits and
/// the daily token cap hold on scaled deployments, not just single-instance.
/// Every method is atomic per <paramref name="counterKey"/>.
/// </summary>
public interface IChatUsageStore
{
    /// <summary>
    /// Atomically increments the window counter for <paramref name="counterKey"/>,
    /// resetting it to 1 when the stored window started before
    /// <paramref name="nowUtc"/> - <paramref name="window"/>. Returns the new count.
    /// </summary>
    Task<long> IncrementWindowAsync(string counterKey, DateTime nowUtc, TimeSpan window);

    /// <summary>Current counter value (0 when absent).</summary>
    Task<long> ReadCountAsync(string counterKey);

    /// <summary>Atomically adds <paramref name="delta"/> to the counter.</summary>
    Task AddCountAsync(string counterKey, DateTime nowUtc, long delta);

    /// <summary>Deletes every counter whose key starts with <paramref name="keyPrefix"/> (ops/test hook).</summary>
    Task ClearAsync(string keyPrefix);
}
