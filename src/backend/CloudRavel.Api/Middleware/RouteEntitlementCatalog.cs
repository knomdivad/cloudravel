namespace CloudRavel.Api.Middleware;

/// <summary>
/// Maps every HTTP route template and method to a feature entitlement requirement. Unlisted routes are denied.
/// </summary>
public static class RouteEntitlementCatalog
{
    public const string RequirementAuthenticated = "authenticated";
    public const string RequirementCloudMonitoring = "cloud_monitoring";
    public const string RequirementBotMonitoring = "bot_monitoring";

    /// <summary>Every registered Functions route and its requirement (source of truth for audits).</summary>
    public static IReadOnlyList<(string RouteTemplate, string? HttpMethod, string Requirement)> AllRouteMappings { get; } =
        new (string, string?, string)[]
        {
            ("health", null, RequirementAuthenticated),
            ("health/ready", null, RequirementAuthenticated),
            ("auth/login", null, RequirementAuthenticated),
            ("auth/me", null, RequirementAuthenticated),

            ("tenants", "GET", RequirementCloudMonitoring),
            ("tenants", "POST", RequirementCloudMonitoring),
            ("tenants/{tenantid:guid}", "GET", RequirementCloudMonitoring),
            ("tenants/{tenantid:guid}/status", "PATCH", RequirementCloudMonitoring),
            ("dashboard", null, RequirementCloudMonitoring),

            ("organizations", "GET", RequirementAuthenticated),
            ("organizations", "POST", RequirementAuthenticated),
            ("organizations/{orgid:guid}", "DELETE", RequirementAuthenticated),
            ("organizations/{orgid:guid}/azure", "POST", RequirementCloudMonitoring),
            ("organizations/{orgid:guid}/users", null, RequirementAuthenticated),
            ("organizations/{orgid:guid}/users/{userid:guid}", null, RequirementAuthenticated),
            ("organizations/{orgid:guid}/sso", null, RequirementAuthenticated),

            ("admin/users", null, RequirementAuthenticated),
            ("admin/users/create", null, RequirementAuthenticated),
            ("system/settings", null, RequirementAuthenticated),
            ("system/users", null, RequirementAuthenticated),
            ("system/users/{userid:guid}", null, RequirementAuthenticated),
            ("system/ai/test", null, RequirementAuthenticated),
            ("system/organizations/{orgid:guid}/entitlements", "PATCH", RequirementAuthenticated),

            ("inventory/resources", null, RequirementCloudMonitoring),
            ("inventory/resource/{*resourceid}", null, RequirementCloudMonitoring),
            ("inventory/summary", null, RequirementCloudMonitoring),
            ("inventory/snapshots", null, RequirementCloudMonitoring),
            ("inventory/snapshots/trigger", null, RequirementCloudMonitoring),

            ("recommendations/advisor", null, RequirementCloudMonitoring),
            ("recommendations/policy", null, RequirementCloudMonitoring),
            ("recommendations/defender", null, RequirementCloudMonitoring),
            ("recommendations/advisor/{recommendationid}/lifecycle", null, RequirementCloudMonitoring),

            ("changes", null, RequirementCloudMonitoring),
            ("changes/recent", null, RequirementCloudMonitoring),
            ("changes/timeline", null, RequirementCloudMonitoring),

            ("ai/query", null, RequirementCloudMonitoring),
            ("ai/chat", null, RequirementCloudMonitoring),

            ("anomalies", null, RequirementCloudMonitoring),
            ("anomalies/{id:long}/status", null, RequirementCloudMonitoring),
            ("incidents", null, RequirementCloudMonitoring),
            ("incidents/{id:long}", null, RequirementCloudMonitoring),
            ("remediations", null, RequirementCloudMonitoring),
            ("remediations/{id:long}/approve", null, RequirementCloudMonitoring),
            ("remediations/{id:long}/reject", null, RequirementCloudMonitoring),
            ("remediations/playbooks", null, RequirementCloudMonitoring),

            ("cloud-orgs", null, RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}/status", null, RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}", null, RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}/credentials", null, RequirementCloudMonitoring),
            ("cloud-orgs/governance/sync", null, RequirementCloudMonitoring),
            ("cloud-accounts", null, RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}/collect", null, RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}", null, RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}/credentials", null, RequirementCloudMonitoring),
            ("operations/summary", null, RequirementCloudMonitoring),

            ("bot-monitor/portal/metrics", null, RequirementBotMonitoring),
            ("bot-monitor/portal/bots", null, RequirementBotMonitoring),
            ("bot-monitor/portal/bot-types", null, RequirementBotMonitoring),
            ("bot-monitor/portal/bots/{botid:guid}", null, RequirementBotMonitoring),
            ("bot-monitor/portal/magic-link", null, RequirementBotMonitoring),

            ("bot-monitor/ops/fleet", null, RequirementBotMonitoring),
            ("bot-monitor/ops/fleet/tenants/{tenantid:guid}/users", null, RequirementBotMonitoring),
            ("bot-monitor/ops/fleet/tenants/{tenantid:guid}/users/{userid:guid}/bots", null, RequirementBotMonitoring),
            ("bot-monitor/ops/bots", null, RequirementBotMonitoring),
            ("bot-monitor/ops/metrics", null, RequirementBotMonitoring),

            ("bot-monitor/registry/tenants", null, RequirementBotMonitoring),
            ("bot-monitor/registry/bots", null, RequirementBotMonitoring),
            ("bot-monitor/registry/bot-types", null, RequirementBotMonitoring),
            ("bot-monitor/registry/bots/{botid:guid}/revoke", null, RequirementBotMonitoring),
            ("bot-monitor/registry/bots/{botid:guid}/rotate", null, RequirementBotMonitoring),
        };

    public static string? ResolveRequirement(string absolutePath, string httpMethod)
    {
        var path = NormalizePath(absolutePath);
        if (path.Length == 0) return null;

        var method = httpMethod.Trim().ToUpperInvariant();
        string? best = null;
        var bestScore = -1;
        foreach (var (template, routeMethod, requirement) in AllRouteMappings)
        {
            if (routeMethod != null
                && !string.Equals(routeMethod, method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!RouteMatches(template, path)) continue;
            var score = template.Length + (routeMethod != null ? 1000 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = requirement;
            }
        }

        return best;
    }

    internal static string NormalizePath(string absolutePath)
    {
        var path = absolutePath.Trim().ToLowerInvariant();
        if (path.StartsWith("/api/", StringComparison.Ordinal))
            path = path["/api/".Length..];
        else if (path.StartsWith('/'))
            path = path[1..];
        return path.TrimEnd('/');
    }

    internal static bool RouteMatches(string routeTemplate, string normalizedPath)
    {
        var templateParts = routeTemplate.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < templateParts.Length; i++)
        {
            var t = templateParts[i];
            if (t is "{*route}" or "{*resourceid}")
                return pathParts.Length >= i;
            if (t.StartsWith('{') && t.EndsWith('}'))
            {
                if (i >= pathParts.Length) return false;
                continue;
            }

            if (i >= pathParts.Length || !string.Equals(t, pathParts[i], StringComparison.Ordinal))
                return false;
        }

        return pathParts.Length == templateParts.Length;
    }
}
