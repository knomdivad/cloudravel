using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorFleetCountTests
{
    [Fact]
    public void Tenant_online_count_matches_per_bot_online_evaluation()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var bots = new[]
        {
            (LastSeen: now.AddMinutes(-5), Interval: 60),
            (LastSeen: now.AddHours(-5), Interval: 60),
            (LastSeen: (DateTime?)null, Interval: 60),
        };

        var online = bots.Count(b => BotMonitorStaleEvaluator.Evaluate(b.LastSeen, b.Interval, now) == BotMonitorPresence.Online);
        Assert.Equal(1, online);
        Assert.Equal(3, bots.Length);
    }
}
