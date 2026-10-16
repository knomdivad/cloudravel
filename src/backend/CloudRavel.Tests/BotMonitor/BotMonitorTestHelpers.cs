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
    internal static readonly string DevKeyBase64 = Convert.ToBase64String(new byte[32]);

    public static IConfiguration DevelopmentConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotMonitor:AllowDevKey"] = "true",
                ["BotMonitor:DevDataProtectionKeyBase64"] = DevKeyBase64,
            })
            .Build();

    public static BotMonitorCredentialService Credentials(string environment = "Development") =>
        new(DevelopmentConfig(), new FakePlatformInfo(environment));
}
