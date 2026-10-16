namespace CloudRavel.Api.Middleware;

/// <summary>
/// Maps every HTTP route template to a feature entitlement requirement. Unlisted routes are denied.
/// </summary>
public static class RouteEntitlementCatalog
{
    public const string RequirementAuthenticated = "authenticated";
    public const string RequirementCloudMonitoring = "cloud_monitoring";
    public const string RequirementBotMonitoring = "bot_monitoring";

    /// <summary>Every registered Functions route and its requirement (source of truth for audits).</summary>
    public static IReadOnlyList<(string RouteTemplate, string Requirement)> AllRouteMappings { get; } =
        new (string, string)[]
        {
            ("health", RequirementAuthenticated),
            ("health/ready", RequirementAuthenticated),
            ("auth/login", RequirementAuthenticated),
            ("auth/me", RequirementAuthenticated),

            ("tenants", RequirementAuthenticated),
            ("tenants/{tenantid:guid}", RequirementAuthenticated),
            ("tenants/{tenantid:guid}/status", RequirementAuthenticated),
            ("dashboard", RequirementCloudMonitoring),

            ("organizations", RequirementAuthenticated),
            ("organizations/{orgid:guid}", RequirementAuthenticated),
            ("organizations/{orgid:guid}/azure", RequirementAuthenticated),
            ("organizations/{orgid:guid}/users", RequirementAuthenticated),
            ("organizations/{orgid:guid}/users/{userid:guid}", RequirementAuthenticated),
            ("organizations/{orgid:guid}/sso", RequirementAuthenticated),

            ("admin/users", RequirementAuthenticated),
            ("admin/users/create", RequirementAuthenticated),
            ("system/settings", RequirementAuthenticated),
            ("system/users", RequirementAuthenticated),
            ("system/users/{userid:guid}", RequirementAuthenticated),
            ("system/ai/test", RequirementAuthenticated),

            ("inventory/resources", RequirementCloudMonitoring),
            ("inventory/resource/{*resourceid}", RequirementCloudMonitoring),
            ("inventory/summary", RequirementCloudMonitoring),
            ("inventory/snapshots", RequirementCloudMonitoring),
            ("inventory/snapshots/trigger", RequirementCloudMonitoring),

            ("recommendations/advisor", RequirementCloudMonitoring),
            ("recommendations/policy", RequirementCloudMonitoring),
            ("recommendations/defender", RequirementCloudMonitoring),
            ("recommendations/advisor/{recommendationid}/lifecycle", RequirementCloudMonitoring),

            ("changes", RequirementCloudMonitoring),
            ("changes/recent", RequirementCloudMonitoring),
            ("changes/timeline", RequirementCloudMonitoring),

            ("ai/query", RequirementCloudMonitoring),
            ("ai/chat", RequirementCloudMonitoring),

            ("anomalies", RequirementCloudMonitoring),
            ("anomalies/{id:long}/status", RequirementCloudMonitoring),
            ("incidents", RequirementCloudMonitoring),
            ("incidents/{id:long}", RequirementCloudMonitoring),
            ("remediations", RequirementCloudMonitoring),
            ("remediations/{id:long}/approve", RequirementCloudMonitoring),
            ("remediations/{id:long}/reject", RequirementCloudMonitoring),
            ("remediations/playbooks", RequirementCloudMonitoring),

            ("cloud-orgs", RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}/status", RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}", RequirementCloudMonitoring),
            ("cloud-orgs/{orgid:guid}/credentials", RequirementCloudMonitoring),
            ("cloud-orgs/governance/sync", RequirementCloudMonitoring),
            ("cloud-accounts", RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}/collect", RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}", RequirementCloudMonitoring),
            ("cloud-accounts/{id:guid}/credentials", RequirementCloudMonitoring),
            ("operations/summary", RequirementCloudMonitoring),

            ("bot-monitor/portal/metrics", RequirementBotMonitoring),
            ("bot-monitor/portal/bots", RequirementBotMonitoring),
            ("bot-monitor/portal/bot-types", RequirementBotMonitoring),
            ("bot-monitor/portal/bots/{botid:guid}", RequirementBotMonitoring),
            ("bot-monitor/portal/magic-link", RequirementBotMonitoring),

            ("bot-monitor/ops/fleet", RequirementBotMonitoring),
            ("bot-monitor/ops/fleet/tenants/{tenantid:guid}/users", RequirementBotMonitoring),
            ("bot-monitor/ops/fleet/tenants/{tenantid:guid}/users/{userid:guid}/bots", RequirementBotMonitoring),
            ("bot-monitor/ops/bots", RequirementBotMonitoring),
            ("bot-monitor/ops/metrics", RequirementBotMonitoring),

            ("bot-monitor/registry/tenants", RequirementBotMonitoring),
            ("bot-monitor/registry/bots", RequirementBotMonitoring),
            ("bot-monitor/registry/bot-types", RequirementBotMonitoring),
            ("bot-monitor/registry/bots/{botid:guid}/revoke", RequirementBotMonitoring),
            ("bot-monitor/registry/bots/{botid:guid}/rotate", RequirementBotMonitoring),
        };

    public static string? ResolveRequirement(string absolutePath)
    {
        var path = NormalizePath(absolutePath);
        if (path.Length == 0) return null;

        string? best = null;
        var bestScore = -1;
        foreach (var (template, requirement) in AllRouteMappings)
        {
            if (!RouteMatches(template, path)) continue;
            var score = template.Length;
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
        if (templateParts.Length != pathParts.Length) return false;

        for (var i = 0; i < templateParts.Length; i++)
        {
            var t = templateParts[i];
            if (t is "{*route}" or "{*resourceid}") return true;
            if (t.StartsWith('{') && t.EndsWith('}')) continue;
            if (!string.Equals(t, pathParts[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
