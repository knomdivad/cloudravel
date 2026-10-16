namespace CloudRavel.Core.BotMonitor;

public static class BotMonitorTypeCounts
{
    public static IReadOnlyList<(string TypeKey, string DisplayName, int Count)> FromBots(
        IEnumerable<BotMonitorBot> bots)
    {
        return bots
            .GroupBy(b => b.BotTypeKey ?? "other_plugin")
            .Select(g =>
            {
                var sample = g.First();
                var name = sample.BotTypeDisplayName ?? g.Key;
                return (g.Key, name, g.Count());
            })
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.Item2)
            .ToList();
    }
}
