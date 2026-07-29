using System.Collections.Concurrent;

namespace CloudRavel.Api.Auth;

/// <summary>
/// Simple in-process sliding-window rate limiter for local login attempts.
/// Keyed by client IP + username. Multi-instance deployments need a shared store (e.g. Redis).
/// </summary>
public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;

    public LoginRateLimiter(int maxAttempts = 10, TimeSpan? window = null)
    {
        _maxAttempts = maxAttempts;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public bool IsAllowed(string key)
    {
        var now = DateTime.UtcNow;
        var window = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) =>
            {
                if (now - existing.StartedAt >= _window)
                    return new Window(now, 1);
                return existing with { Count = existing.Count + 1 };
            });

        return window.Count <= _maxAttempts;
    }

    public void Reset(string key) => _windows.TryRemove(key, out _);

    private sealed record Window(DateTime StartedAt, int Count);
}
