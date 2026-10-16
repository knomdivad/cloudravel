using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorStaleEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Never_seen_is_offline()
    {
        Assert.Equal(BotMonitorPresence.Offline, BotMonitorStaleEvaluator.Evaluate(null, 60, Now));
    }

    [Fact]
    public void Exactly_at_stale_boundary_is_still_online()
    {
        var lastSeen = Now - TimeSpan.FromMinutes(120);
        Assert.Equal(BotMonitorPresence.Online, BotMonitorStaleEvaluator.Evaluate(lastSeen, 60, Now));
    }

    [Fact]
    public void Just_past_stale_boundary_is_stale()
    {
        var lastSeen = Now - TimeSpan.FromMinutes(120) - TimeSpan.FromSeconds(1);
        Assert.Equal(BotMonitorPresence.Stale, BotMonitorStaleEvaluator.Evaluate(lastSeen, 60, Now));
    }

    [Fact]
    public void Recent_checkin_is_online()
    {
        var lastSeen = Now - TimeSpan.FromMinutes(30);
        Assert.Equal(BotMonitorPresence.Online, BotMonitorStaleEvaluator.Evaluate(lastSeen, 60, Now));
    }
}
