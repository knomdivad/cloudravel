using System.Text.RegularExpressions;
using CloudRavel.Api.Middleware;

namespace CloudRavel.Tests.Auth;

internal static class HttpRouteDiscovery
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD",
    };

    public static IReadOnlyList<(string Method, string RouteTemplate)> DiscoverAll()
    {
        var functionsDir = LocateFunctionsDirectory();
        var triggerPattern = new Regex(
            @"HttpTrigger\((.*?)\)\]\s*HttpRequestData",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var list = new List<(string, string)>();
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
                    list.Add((token.ToUpperInvariant(), route));
                }
            }
        }

        return list
            .Distinct()
            .OrderBy(r => r.Item2)
            .ThenBy(r => r.Item1)
            .ToList();
    }

    /// <summary>One row per catalog entry and HTTP method (source of truth for entitlement sweeps).</summary>
    public static IReadOnlyList<(string Method, string RouteTemplate)> DiscoverSweepRoutes()
    {
        var fromSource = DiscoverAll().ToLookup(r => r.Item2, r => r.Item1);
        var list = new List<(string, string)>();
        foreach (var (template, httpMethod, _) in RouteEntitlementCatalog.AllRouteMappings)
        {
            if (httpMethod != null)
            {
                list.Add((httpMethod.ToUpperInvariant(), template));
                continue;
            }

            if (fromSource.Contains(template))
            {
                foreach (var method in fromSource[template].Distinct(StringComparer.OrdinalIgnoreCase))
                    list.Add((method, template));
            }
            else
            {
                list.Add(("GET", template));
            }
        }

        return list
            .Distinct()
            .OrderBy(r => r.Item2)
            .ThenBy(r => r.Item1)
            .ToList();
    }

    public static string MaterializePath(string routeTemplate)
    {
        var path = routeTemplate.ToLowerInvariant();
        path = path.Replace("{tenantid:guid}", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        path = path.Replace("{orgid:guid}", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        path = path.Replace("{userid:guid}", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        path = path.Replace("{botid:guid}", "cccccccc-cccc-cccc-cccc-cccccccccccc");
        path = path.Replace("{id:guid}", "dddddddd-dddd-dddd-dddd-dddddddddddd");
        path = path.Replace("{recommendationid}", "1");
        path = path.Replace("{id:long}", "1");
        path = path.Replace("{*resourceid}", "subscriptions/sub/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm");
        return "/api/" + path;
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

        throw new InvalidOperationException("Could not locate CloudRavel.Api/Functions.");
    }
}
