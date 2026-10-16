using CloudRavel.Api.Middleware;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class AnonymousApiPathsTests
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/API/HEALTH/")]
    [InlineData("/api/health/ready")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/bot-monitor/heartbeat")]
    public void Exact_public_paths_bypass(string path)
    {
        Assert.True(AnonymousApiPaths.IsAnonymousBypass(path));
    }

    [Theory]
    [InlineData("/api/health/evil")]
    [InlineData("/api/x/auth/login")]
    [InlineData("/api/bot-monitor/heartbeat/extra")]
    [InlineData("/api/bot-monitor/heartbeats")]
    [InlineData("/api/health/ready/extra")]
    public void Path_suffix_tricks_do_not_bypass(string path)
    {
        Assert.False(AnonymousApiPaths.IsAnonymousBypass(path));
    }

    [Fact]
    public void Encoded_segment_does_not_bypass_unless_exact()
    {
        Assert.True(AnonymousApiPaths.IsAnonymousBypass("/api/health%2fready"));
        Assert.False(AnonymousApiPaths.IsAnonymousBypass("/api/health%2fevil"));
    }
}
