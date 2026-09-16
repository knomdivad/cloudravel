using System.Collections.Concurrent;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// In-process sliding-window rate limiter for customer chat: one window per
/// user, one per tenant, plus a per-tenant daily token cap. Mirrors
/// <c>LoginRateLimiter</c> semantics. Multi-instance deployments need a shared
/// store (e.g. Redis) — same caveat as login limiting.
/// </summary>
public sealed class ChatRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly ConcurrentDictionary<string, int> _dailyTokens = new();
    private readonly int _perUserMax;
    private readonly int _perTenantMax;
    private readonly int _dailyTokenCap;
    private readonly TimeSpan _window;

    public ChatRateLimiter(int perUserMax = 20, int perTenantMax = 100, int dailyTokenCap = 200_000, TimeSpan? window = null)
    {
        _perUserMax = perUserMax;
        _perTenantMax = perTenantMax;
        _dailyTokenCap = dailyTokenCap;
        _window = window ?? TimeSpan.FromHours(1);
    }

    /// <summary>Reason the request is denied, or null when allowed.</summary>
    public string? CheckAndIncrement(Guid tenantId, Guid userId)
    {
        var now = DateTime.UtcNow;

        if (!TryIncrement(WindowKey("u", tenantId, userId), now, _perUserMax))
            return "user_rate_limit";

        if (!TryIncrement(WindowKey("t", tenantId, Guid.Empty), now, _perTenantMax))
        {
            // Roll back the user count so denied tenant-wide requests don't
            // consume the caller's personal budget.
            TryDecrement(WindowKey("u", tenantId, userId));
            return "tenant_rate_limit";
        }

        return null;
    }

    /// <summary>
    /// Daily token cap per tenant. Call BEFORE sending to the provider with the
    /// estimated request cost; call <see cref="RecordTokensAsync"/> after with
    /// actual usage. Returns false when the tenant is over the cap for the
    /// current UTC day.
    /// </summary>
    public bool IsUnderTokenCap(Guid tenantId, int estimatedTokens = 0)
    {
        var used = _dailyTokens.TryGetValue(DailyKey(tenantId), out var v) ? v : 0;
        return used + estimatedTokens <= _dailyTokenCap;
    }

    public void RecordTokens(Guid tenantId, int tokens)
    {
        if (tokens <= 0) return;
        _dailyTokens.AddOrUpdate(DailyKey(tenantId), tokens, (_, existing) => existing + tokens);
    }

    /// <summary>Test/ops hook: clears all windows and counters.</summary>
    public void Reset()
    {
        _windows.Clear();
        _dailyTokens.Clear();
    }

    private static string WindowKey(string kind, Guid tenantId, Guid subjectId) =>
        $"{kind}|{tenantId:N}|{subjectId:N}";

    private static string DailyKey(Guid tenantId) =>
        $"tok|{tenantId:N}|{DateTime.UtcNow:yyyyMMdd}";

    private bool TryIncrement(string key, DateTime now, int max)
    {
        var window = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) =>
            {
                if (now - existing.StartedAt >= _window)
                    return new Window(now, 1);
                return existing with { Count = existing.Count + 1 };
            });

        return window.Count <= max;
    }

    private void TryDecrement(string key)
    {
        if (_windows.TryGetValue(key, out var existing) && existing.Count > 1)
            _windows[key] = existing with { Count = existing.Count - 1 };
        else
            _windows.TryRemove(key, out _);
    }

    private sealed record Window(DateTime StartedAt, int Count);
}
