using Xunit;

using CloudRavel.Api.Auth;

namespace CloudRavel.Tests.Auth;

public sealed class LoginRateLimiterTests
{
    [Fact]
    public void Allows_under_limit_then_blocks()
    {
        var limiter = new LoginRateLimiter(maxAttempts: 3, window: TimeSpan.FromMinutes(1));
        Assert.True(limiter.IsAllowed("ip|user"));
        Assert.True(limiter.IsAllowed("ip|user"));
        Assert.True(limiter.IsAllowed("ip|user"));
        Assert.False(limiter.IsAllowed("ip|user"));
    }

    [Fact]
    public void Reset_clears_window()
    {
        var limiter = new LoginRateLimiter(maxAttempts: 1, window: TimeSpan.FromMinutes(1));
        Assert.True(limiter.IsAllowed("a|b"));
        Assert.False(limiter.IsAllowed("a|b"));
        limiter.Reset("a|b");
        Assert.True(limiter.IsAllowed("a|b"));
    }

    [Fact]
    public void Keys_are_independent()
    {
        var limiter = new LoginRateLimiter(maxAttempts: 1, window: TimeSpan.FromMinutes(1));
        Assert.True(limiter.IsAllowed("ip1|alice"));
        Assert.False(limiter.IsAllowed("ip1|alice"));
        Assert.True(limiter.IsAllowed("ip1|bob"));
    }
}
