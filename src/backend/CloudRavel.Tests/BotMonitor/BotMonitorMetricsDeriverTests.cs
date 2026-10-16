using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorMetricsDeriverTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Consecutive_failure_streak_counts_error_and_keep_fail()
    {
        var recent = new List<(DateTime, BotMonitorStatus, BotMonitorKeepCheck)>
        {
            (Now, BotMonitorStatus.Error, BotMonitorKeepCheck.Pass),
            (Now.AddMinutes(-5), BotMonitorStatus.Ok, BotMonitorKeepCheck.Fail),
            (Now.AddMinutes(-10), BotMonitorStatus.Ok, BotMonitorKeepCheck.Pass),
        };
        var derived = BotMonitorMetricsDeriver.Derive(null, recent, 60, Now);
        Assert.Equal(2, derived.ConsecutiveFailureStreak);
    }

    [Fact]
    public void Routine_missed_when_expected_past_grace_without_ok_run()
    {
        var reported = new BotMonitorReportedMetrics
        {
            Routine = new BotMonitorRoutineReport
            {
                Id = "weekly-keep",
                ExpectedAt = Now.AddHours(-2),
                Result = "missed",
            },
        };
        var derived = BotMonitorMetricsDeriver.Derive(reported, Array.Empty<(DateTime, BotMonitorStatus, BotMonitorKeepCheck)>(), 60, Now);
        Assert.True(derived.RoutineMissed);
    }

    [Fact]
    public void Runs_per_day_counts_last_24h()
    {
        var recent = new List<(DateTime, BotMonitorStatus, BotMonitorKeepCheck)>
        {
            (Now.AddHours(-1), BotMonitorStatus.Ok, BotMonitorKeepCheck.Pass),
            (Now.AddHours(-25), BotMonitorStatus.Ok, BotMonitorKeepCheck.Pass),
        };
        var derived = BotMonitorMetricsDeriver.Derive(null, recent, 60, Now);
        Assert.Equal(1, derived.RunsPerDay);
    }
}
