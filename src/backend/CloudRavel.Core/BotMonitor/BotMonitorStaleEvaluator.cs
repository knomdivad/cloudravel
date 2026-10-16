namespace CloudRavel.Core.BotMonitor;

/// <summary>
/// Pure presence evaluation: stale when now minus last_seen exceeds twice the check-in interval;
/// offline when never seen.
/// </summary>
public static class BotMonitorStaleEvaluator
{
    public static BotMonitorPresence Evaluate(
        DateTime? lastSeenAt,
        int checkinIntervalMinutes,
        DateTime nowUtc)
    {
        if (lastSeenAt is null) return BotMonitorPresence.Offline;

        var interval = TimeSpan.FromMinutes(Math.Max(1, checkinIntervalMinutes));
        var staleAfter = interval + interval;
        var elapsed = nowUtc - lastSeenAt.Value;
        if (elapsed > staleAfter) return BotMonitorPresence.Stale;
        return BotMonitorPresence.Online;
    }
}
