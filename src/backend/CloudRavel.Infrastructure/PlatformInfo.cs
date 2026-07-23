using CloudRavel.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Infrastructure;

/// <summary>
/// Reads the instance environment from configuration (Platform:Environment).
/// Defaults to Development so a fresh/demo instance never auto-collects
/// inventory against real (or fake seed) clouds until explicitly set to
/// Production.
/// </summary>
public sealed class PlatformInfo : IPlatformInfo
{
    public PlatformInfo(IConfiguration configuration)
    {
        Environment = configuration["Platform:Environment"] ?? "Development";
    }

    public string Environment { get; }

    public bool IsProduction => Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
}
