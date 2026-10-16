using System.Text.RegularExpressions;
using CloudRavel.Api.Middleware;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class RouteEntitlementCatalogTests
{
    [Fact]
    public void Every_registered_http_route_is_mapped_in_catalog()
    {
        var discovered = DiscoverHttpRoutesFromSource().OrderBy(r => r).ToList();
        Assert.NotEmpty(discovered);

        var unmapped = new List<string>();
        foreach (var (method, route) in discovered)
        {
            var requirement = RouteEntitlementCatalog.ResolveRequirement("/api/" + route, method);
            if (requirement is null)
                unmapped.Add($"{method} {route}");
        }

        Assert.True(unmapped.Count == 0, "Unmapped routes: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void Get_tenants_is_cloud_route()
    {
        var requirement = RouteEntitlementCatalog.ResolveRequirement("/api/tenants", "GET");
        Assert.Equal(RouteEntitlementCatalog.RequirementCloudMonitoring, requirement);
    }

    [Fact]
    public void Post_tenants_is_cloud_route()
    {
        var requirement = RouteEntitlementCatalog.ResolveRequirement("/api/tenants", "POST");
        Assert.Equal(RouteEntitlementCatalog.RequirementCloudMonitoring, requirement);
    }

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD",
    };

    internal static IEnumerable<(string Method, string Route)> DiscoverHttpRoutesFromSource()
    {
        var functionsDir = LocateFunctionsDirectory();
        var triggerPattern = new Regex(
            @"HttpTrigger\((.*?)\)\]\s*HttpRequestData",
            RegexOptions.Compiled | RegexOptions.Singleline);

        foreach (var file in Directory.EnumerateFiles(functionsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in triggerPattern.Matches(text))
            {
                var inner = match.Groups[1].Value;
                var routeMatch = Regex.Match(inner, @"Route\s*=\s*""([^""]+)""");
                if (!routeMatch.Success) continue;

                var route = routeMatch.Groups[1].Value.Trim().TrimStart('/').ToLowerInvariant();
                if (route.Contains("{*route}", StringComparison.Ordinal)
                    || route.Equals("bot-monitor/heartbeat", StringComparison.Ordinal))
                    continue;

                foreach (Match methodMatch in Regex.Matches(inner, @"""([^""]+)"""))
                {
                    var token = methodMatch.Groups[1].Value;
                    if (!HttpMethods.Contains(token)) continue;
                    yield return (token.ToUpperInvariant(), route);
                }
            }
        }
    }

    private static string LocateFunctionsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "backend", "CloudRavel.Api", "Functions");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate CloudRavel.Api/Functions for route audit.");
    }
}
