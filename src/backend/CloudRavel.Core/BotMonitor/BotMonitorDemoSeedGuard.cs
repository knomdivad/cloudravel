namespace CloudRavel.Core.BotMonitor;

/// <summary>Mirrors the local-only guard in database/seed-bot-monitor-demo.sql.</summary>
public static class BotMonitorDemoSeedGuard
{
    public static bool AllowsDemoSeed(string platformEnvironment, bool hasProductionOrganizations)
    {
        if (hasProductionOrganizations) return false;
        return platformEnvironment.Equals("Development", StringComparison.OrdinalIgnoreCase);
    }
}
