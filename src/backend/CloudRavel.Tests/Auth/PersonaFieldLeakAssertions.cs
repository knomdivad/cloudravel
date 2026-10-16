namespace CloudRavel.Tests.Auth;

internal static class PersonaFieldLeakAssertions
{
    public static string Evaluate(string personaName, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "empty";

        if (personaName == "bot-only" && CloudFieldLeakDetector.ContainsCloudField(json))
            return "cloud-leak";

        if (personaName == "cloud-only" && BotFieldLeakDetector.ContainsBotField(json))
            return "bot-leak";

        return "ok";
    }

    internal static void AssertNoLeak(string personaName, string routeTemplate, string method, string json)
    {
        var verdict = Evaluate(personaName, json);
        if (verdict is "cloud-leak" or "bot-leak")
        {
            throw new Xunit.Sdk.XunitException(
                $"{personaName} {method} {routeTemplate}: {verdict} in body: {Truncate(json)}");
        }
    }

    private static string Truncate(string json) =>
        json.Length <= 240 ? json : json[..240] + "...";
}

internal static class CloudFieldLeakDetector
{
    private static readonly string[] CloudTokens =
    [
        "resourceCount", "cloudCount", "azureConnected", "azureTenant", "inventory",
        "recommendation", "subscription", "changes24h", "lastSnapshotAt",
    ];

    public static bool ContainsCloudField(string json)
    {
        var lower = json.ToLowerInvariant();
        return CloudTokens.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }
}

internal static class BotFieldLeakDetector
{
    private static readonly string[] BotTokens =
    [
        "botid", "botcount", "onlinecount", "bottypekey", "magiclink", "heartbeat",
        "fleettypecounts", "owneruserid", "keepcheck", "botmonitor", "lastheartbeat",
        "\"bots\":[", "bottypes", "bot-monitor",
    ];

    public static bool ContainsBotField(string json)
    {
        var lower = json.ToLowerInvariant();
        return BotTokens.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }
}
