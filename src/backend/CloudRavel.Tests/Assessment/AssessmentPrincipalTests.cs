using CloudRavel.Api.Middleware;
using Xunit;

namespace CloudRavel.Tests.Assessment;

/// <summary>
/// The read-only service-credential path used by the standalone assessment
/// tool: rate limiting, and (in the middleware itself) single-tenant scope,
/// mutation refusal, and audit logging. The credential is provisioned by the
/// operator; nothing here handles secrets.
/// </summary>
public sealed class AssessmentPrincipalRateLimiterTests
{
    [Fact]
    public void Allows_up_to_limit_then_blocks()
    {
        var limiter = new AssessmentPrincipalRateLimiter(maxRequests: 3);
        Assert.True(limiter.IsAllowed("cred-1"));
        Assert.True(limiter.IsAllowed("cred-1"));
        Assert.True(limiter.IsAllowed("cred-1"));
        Assert.False(limiter.IsAllowed("cred-1"));
    }

    [Fact]
    public void Windows_are_per_credential()
    {
        var limiter = new AssessmentPrincipalRateLimiter(maxRequests: 1);
        Assert.True(limiter.IsAllowed("cred-a"));
        Assert.True(limiter.IsAllowed("cred-b")); // separate window
        Assert.False(limiter.IsAllowed("cred-a"));
    }
}
