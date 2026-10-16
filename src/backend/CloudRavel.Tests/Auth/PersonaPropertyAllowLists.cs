using System.Reflection;
using System.Text.Json;
using CloudRavel.Core.DTOs;
using CloudRavel.Core.Models;

namespace CloudRavel.Tests.Auth;

/// <summary>
/// JSON property names (camelCase) each persona may see. Anything else is a leak.
/// </summary>
internal static class PersonaPropertyAllowLists
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "code", "message", "error", "errors", "detail", "details",
        "traceId", "buildFeatures", "timestamp", "configuration",
        "healthy", "checks", "components", "database", "version", "commit",
        "status", "environment",
        "offset", "limit", "total", "hasMore", "pagination", "page", "pageSize",
        "token", "accessToken", "expiresIn", "expiresAt", "loginIdentity",
        "password", "success", "systemRole",
        "orgId", "name", "callerRole", "createdAt", "userId", "email", "displayName",
        "globalRole", "isActive", "role", "organizations", "users", "tenants",
    };

    private static readonly HashSet<string> CloudOnly = new(StringComparer.Ordinal)
    {
        "awsOrgCount", "gcpOrgCount",
    };

    private static readonly HashSet<string> BotOnly = new(StringComparer.Ordinal)
    {
        "dashboard", "types",
    };

    private static readonly HashSet<string> SharedDtoTypeNames = new(StringComparer.Ordinal)
    {
        nameof(ErrorResponse),
        nameof(PaginationDto),
        nameof(LoginRequestDto),
        nameof(LoginResponseDto),
        nameof(AuthUserDto),
        nameof(OrganizationListResponse),
        nameof(CreateOrganizationRequest),
    };

    private static readonly HashSet<string> OrganizationCommonProps = new(StringComparer.Ordinal)
    {
        "orgId", "name", "status", "callerRole", "environment", "createdAt",
    };

    static PersonaPropertyAllowLists()
    {
        foreach (var type in typeof(TenantSummaryDto).Assembly.GetTypes())
        {
            if (type.Namespace != "CloudRavel.Core.DTOs" || !type.IsClass || type.IsAbstract)
                continue;

            if (type.Name.Contains("BotMonitor", StringComparison.Ordinal))
            {
                RegisterTypeProperties(type, BotOnly);
                continue;
            }

            if (type.Name == nameof(OrganizationDto))
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var camel = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    if (OrganizationCommonProps.Contains(camel))
                        Common.Add(camel);
                    else
                        CloudOnly.Add(camel);
                }

                continue;
            }

            if (SharedDtoTypeNames.Contains(type.Name))
            {
                RegisterTypeProperties(type, Common);
                continue;
            }

            RegisterTypeProperties(type, CloudOnly);
        }

        foreach (var type in typeof(Tenant).Assembly.GetTypes())
        {
            if (type.Namespace != "CloudRavel.Core.Models" || !type.IsClass || type.IsAbstract)
                continue;
            if (type.Namespace?.Contains("BotMonitor") == true)
                RegisterTypeProperties(type, BotOnly);
            else
                RegisterTypeProperties(type, CloudOnly);
        }

        foreach (var extra in new[]
                 {
                     "windowHours", "from", "to", "buckets", "findings", "records", "snapshots",
                     "resourceTypes", "byProvider", "skuCapacity",
                 })
            CloudOnly.Add(extra);
    }

    private static void RegisterTypeProperties(Type type, HashSet<string> target)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            target.Add(name);
        }
    }

    internal static HashSet<string> AllowedFor(string personaName)
    {
        var allowed = new HashSet<string>(Common, StringComparer.Ordinal);
        if (personaName is "bot-only" or "both")
            allowed.UnionWith(BotOnly);
        if (personaName is "cloud-only" or "both")
            allowed.UnionWith(CloudOnly);
        return allowed;
    }

    internal static bool IsCloudProperty(string propertyName) =>
        CloudOnly.Contains(propertyName);

    internal static bool IsBotProperty(string propertyName) =>
        BotOnly.Contains(propertyName);
}
