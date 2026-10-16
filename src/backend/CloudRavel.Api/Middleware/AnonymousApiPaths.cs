namespace CloudRavel.Api.Middleware;

/// <summary>
/// Exact normalized API paths that skip authentication and tenant gates.
/// </summary>
public static class AnonymousApiPaths
{
    private static readonly HashSet<string> ExactNormalizedPaths = new(StringComparer.Ordinal)
    {
        "health",
        "health/ready",
        "auth/login",
        "bot-monitor/heartbeat",
    };

    public static string NormalizeForBypassCheck(string absolutePath)
    {
        var path = RouteEntitlementCatalog.NormalizePath(absolutePath);
        if (path.Length == 0) return path;

        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }

        return path.Trim().TrimEnd('/');
    }

    public static bool IsAnonymousBypass(string absolutePath)
    {
        var normalized = NormalizeForBypassCheck(absolutePath);
        return normalized.Length > 0 && ExactNormalizedPaths.Contains(normalized);
    }
}
