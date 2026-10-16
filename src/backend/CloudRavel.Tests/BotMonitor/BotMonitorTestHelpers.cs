using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Tests.BotMonitor;

internal sealed class FakePlatformInfo : IPlatformInfo
{
    public FakePlatformInfo(string environment = "Development")
    {
        Environment = environment;
        Version = "1.0.0";
        Commit = null;
    }

    public string Environment { get; }
    public bool IsProduction => Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
    public string Version { get; }
    public string? Commit { get; }
}

internal static class BotMonitorTestConfig
{
    public static BotMonitorCredentialService Credentials(string environment = "Development") =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LOCAL_AUTH_JWT_SIGNING_KEY"] = "test-signing-key-for-unit-tests",
                })
                .Build(),
            new FakePlatformInfo(environment));
}
