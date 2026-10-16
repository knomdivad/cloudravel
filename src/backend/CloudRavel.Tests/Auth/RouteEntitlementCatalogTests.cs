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
        foreach (var route in discovered)
        {
            var requirement = RouteEntitlementCatalog.ResolveRequirement("/api/" + route);
            if (requirement is null)
                unmapped.Add(route);
        }

        Assert.True(unmapped.Count == 0, "Unmapped routes: " + string.Join(", ", unmapped));
    }

    internal static IEnumerable<string> DiscoverHttpRoutesFromSource()
    {
        var functionsDir = LocateFunctionsDirectory();
        var pattern = new Regex(@"Route\s*=\s*""([^""]+)""", RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(functionsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var route = match.Groups[1].Value.Trim().TrimStart('/').ToLowerInvariant();
                if (route.Contains("{*route}", StringComparison.Ordinal)
                    || route.Equals("bot-monitor/heartbeat", StringComparison.Ordinal))
                    continue;
                yield return route;
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
