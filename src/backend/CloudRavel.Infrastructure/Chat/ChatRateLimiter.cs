using CloudRavel.Core.Interfaces;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// Customer-chat abuse caps (per-user window, per-tenant window, per-tenant
/// daily token cap) backed by a shared <see cref="IChatUsageStore"/> (SQL in
/// production) so the caps hold on scaled deployments — on N instances the
/// limits are NOT multiplied by N.
///
/// Token accounting (DoD: a single request cannot exceed the cap):
///   - BEFORE the provider call, the pre-check reserves the estimated input
///     PLUS a minimum-output headroom (default 4k tokens, config
///     <c>Chat:MaxOutputTokens</c>) — a request that could push the tenant
///     past the cap with its completion is refused up front.
///   - AFTER the call, actual input+output tokens are recorded; usage beyond
///     the cap blocks all subsequent requests for the UTC day.
///
/// Semantics mirror <c>LoginRateLimiter</c>; counters are opaque strings in
/// the store, keyed by tenant/user/window.
/// </summary>
public sealed class ChatRateLimiter
{
    private readonly IChatUsageStore _store;
    private readonly int _perUserMax;
    private readonly int _perTenantMax;
    private readonly int _dailyTokenCap;
    private readonly int _minOutputHeadroom;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;

    public ChatRateLimiter(
        IChatUsageStore store,
        int perUserMax = 20,
        int perTenantMax = 100,
        int dailyTokenCap = 200_000,
        int minOutputTokenHeadroom = 4_000,
        TimeSpan? window = null,
        Func<DateTime>? utcNow = null)
    {
        _store = store;
        _perUserMax = perUserMax;
        _perTenantMax = perTenantMax;
        _dailyTokenCap = dailyTokenCap;
        _minOutputHeadroom = Math.Max(0, minOutputTokenHeadroom);
        _window = window ?? TimeSpan.FromHours(1);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Reason the request is denied, or null when allowed.</summary>
    public async Task<string?> CheckAndIncrementAsync(Guid tenantId, Guid userId)
    {
        var now = _utcNow();

        if (!await TryIncrementAsync(WindowKey("u", tenantId, userId), now, _perUserMax))
            return "user_rate_limit";

        if (!await TryIncrementAsync(WindowKey("t", tenantId, Guid.Empty), now, _perTenantMax))
        {
            // Roll back the user count so denied tenant-wide requests don't
            // consume the caller's personal budget.
            await _store.AddCountAsync(WindowKey("u", tenantId, userId), now, -1);
            return "tenant_rate_limit";
        }

        return null;
    }

    /// <summary>
    /// Daily token cap per tenant. Call BEFORE sending to the provider with the
    /// estimated input cost; this method adds the configured minimum-output
    /// reservation so one request cannot push the tenant past the cap with its
    /// completion. Call <see cref="RecordTokensAsync"/> after with actual
    /// input+output usage. Returns false when the tenant would exceed the cap
    /// for the current UTC day.
    /// </summary>
    public async Task<bool> IsUnderTokenCapAsync(Guid tenantId, int estimatedTokens = 0)
    {
        var used = await _store.ReadCountAsync(DailyKey(tenantId));
        return used + Math.Max(0, estimatedTokens) + _minOutputHeadroom <= _dailyTokenCap;
    }

    public async Task RecordTokensAsync(Guid tenantId, int tokens)
    {
        if (tokens <= 0) return;
        await _store.AddCountAsync(DailyKey(tenantId), _utcNow(), tokens);
    }

    /// <summary>Test/ops hook: clears every chat window and token counter.</summary>
    public async Task ResetAsync() =>
        await _store.ClearAsync("chat|");

    private async Task<bool> TryIncrementAsync(string key, DateTime now, int max) =>
        await _store.IncrementWindowAsync(key, now, _window) <= max;

    private string WindowKey(string kind, Guid tenantId, Guid subjectId) =>
        $"chat|{kind}|{tenantId:N}|{subjectId:N}";

    private string DailyKey(Guid tenantId) =>
        $"chat|tok|{tenantId:N}|{_utcNow():yyyyMMdd}";
}
