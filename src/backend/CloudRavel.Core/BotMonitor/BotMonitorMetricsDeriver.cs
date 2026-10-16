namespace CloudRavel.Core.BotMonitor;

public static class BotMonitorMetricsDeriver
{
    public static BotMonitorDerivedMetrics Derive(
        BotMonitorReportedMetrics? reported,
        IReadOnlyList<(DateTime ReceivedAt, BotMonitorStatus Status, BotMonitorKeepCheck KeepCheck)> recentNewestFirst,
        int checkinIntervalMinutes,
        DateTime nowUtc)
    {
        var derived = new BotMonitorDerivedMetrics
        {
            ActionErrorsReported = reported?.Actions?.Failed ?? 0,
            RunsPerDay = recentNewestFirst.Count(h => h.ReceivedAt >= nowUtc.AddDays(-1)),
        };

        foreach (var row in recentNewestFirst)
        {
            if (row.Status == BotMonitorStatus.Error || row.KeepCheck == BotMonitorKeepCheck.Fail)
                derived.ConsecutiveFailureStreak++;
            else
                break;
        }

        if (reported?.Routine?.ExpectedAt is { } expected)
        {
            var grace = TimeSpan.FromMinutes(Math.Max(5, checkinIntervalMinutes));
            var missed = nowUtc > expected.Add(grace)
                         && (reported.Routine.RanAt is null
                             || !string.Equals(reported.Routine.Result, "ok", StringComparison.OrdinalIgnoreCase));
            derived.RoutineMissed = missed;
        }

        if (recentNewestFirst.Count > 0)
        {
            var last = recentNewestFirst[0].ReceivedAt;
            var expectedAt = last.AddMinutes(-checkinIntervalMinutes);
            var lateness = (int)Math.Max(0, (last - expectedAt).TotalMinutes);
            derived.CheckInLatenessMinutes = lateness;
        }

        return derived;
    }
}
