using System.Reflection;
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

        // InformationalVersion is "{Directory.Build.props Version}" locally, or
        // "{Version}+{SourceRevisionId}" when CI stamps the commit at build time
        // (see build.yml's -p:SourceRevisionId). Split it so callers get a clean
        // semver plus an optional commit SHA rather than parsing this themselves.
        var infoVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(infoVersion))
        {
            Version = "1.0.0";
            Commit = null;
        }
        else
        {
            var plusIndex = infoVersion.IndexOf('+');
            if (plusIndex >= 0)
            {
                Version = infoVersion[..plusIndex];
                Commit = infoVersion[(plusIndex + 1)..];
            }
            else
            {
                Version = infoVersion;
                Commit = null;
            }
        }
    }

    public string Environment { get; }

    public bool IsProduction => Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);

    public string Version { get; }

    public string? Commit { get; }
}
