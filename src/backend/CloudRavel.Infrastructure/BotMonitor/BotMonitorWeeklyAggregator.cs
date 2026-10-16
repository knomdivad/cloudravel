using System.Globalization;
using CloudRavel.Core.BotMonitor;

namespace CloudRavel.Infrastructure.BotMonitor;

public static class BotMonitorWeeklyAggregator
{
    public static IReadOnlyList<BotMonitorWeeklyBucket> BuildEmptyWeeks(int weekCount, DateTime nowUtc)
    {
        var list = new List<BotMonitorWeeklyBucket>(weekCount);
        var cal = ISOWeek.GetYear(nowUtc);
        var cw = ISOWeek.GetWeekOfYear(nowUtc);
        for (var i = 0; i < weekCount; i++)
        {
            var (y, w) = AddIsoWeeks(cal, cw, -i);
            list.Add(new BotMonitorWeeklyBucket
            {
                IsoYear = y,
                IsoWeek = w,
                UptimePercent = 0,
            });
        }

        return list;
    }

    public static void ApplyHeartbeat(
        BotMonitorWeeklyBucket bucket,
        DateTime receivedAt,
        BotMonitorStatus status,
        BotMonitorKeepCheck keepCheck,
        int checkinIntervalMinutes)
    {
        if (status == BotMonitorStatus.Error) bucket.ErrorCount++;
        switch (keepCheck)
        {
            case BotMonitorKeepCheck.Pass: bucket.KeepPassCount++; break;
            case BotMonitorKeepCheck.Warn: bucket.KeepWarnCount++; break;
            case BotMonitorKeepCheck.Fail: bucket.KeepFailCount++; break;
        }

        var expected = ExpectedCheckinsForWeek(checkinIntervalMinutes);
        if (expected <= 0) return;
        bucket.UptimePercent = Math.Min(100, bucket.UptimePercent + (100.0 / expected));
    }

    public static int ExpectedCheckinsForWeek(int checkinIntervalMinutes)
    {
        var perWeek = (7 * 24 * 60) / Math.Max(1, checkinIntervalMinutes);
        return Math.Max(1, perWeek);
    }

    public static (int Year, int Week) IsoWeekOf(DateTime utc)
        => (ISOWeek.GetYear(utc), ISOWeek.GetWeekOfYear(utc));

    private static (int Year, int Week) AddIsoWeeks(int year, int week, int delta)
    {
        var jan4 = ISOWeek.ToDateTime(year, 1, DayOfWeek.Monday);
        var target = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays((double)delta * 7.0);
        return (ISOWeek.GetYear(target), ISOWeek.GetWeekOfYear(target));
    }
}
