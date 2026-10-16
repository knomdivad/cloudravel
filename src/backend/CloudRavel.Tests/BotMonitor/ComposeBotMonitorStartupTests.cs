using CloudRavel.Api.BotMonitor;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

/// <summary>
/// Mirrors docker-compose.yml api environment: double-underscore keys become nested config.
/// </summary>
public sealed class ComposeBotMonitorStartupTests
{
    private static IConfiguration ComposeStyleConfig(IReadOnlyDictionary<string, string?> env)
    {
        var pairs = env.ToDictionary(
            static kv => kv.Key.Replace("__", ":", StringComparison.Ordinal),
            static kv => kv.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    [Fact]
    public void Production_with_compose_production_key_passes_startup_validation()
    {
        var prodKey = Convert.ToBase64String(new byte[32]);
        var config = ComposeStyleConfig(new Dictionary<string, string?>
        {
            ["Platform__Environment"] = "Production",
            ["BotMonitor__DataProtectionKeyBase64"] = prodKey,
            ["BotMonitor__AllowDevKey"] = "",
            ["BotMonitor__DevDataProtectionKeyBase64"] = "",
        });

        BotMonitorStartupValidation.ValidateBotMonitorConfiguration(
            config,
            new FakePlatformInfo("Production"));
    }

    [Fact]
    public void Development_with_compose_dev_key_pair_passes_startup_validation()
    {
        var config = ComposeStyleConfig(new Dictionary<string, string?>
        {
            ["Platform__Environment"] = "Development",
            ["BotMonitor__DataProtectionKeyBase64"] = "",
            ["BotMonitor__AllowDevKey"] = "true",
            ["BotMonitor__DevDataProtectionKeyBase64"] = BotMonitorTestConfig.DevKeyBase64,
        });

        BotMonitorStartupValidation.ValidateBotMonitorConfiguration(
            config,
            new FakePlatformInfo("Development"));
    }

    [Fact]
    public void Production_with_empty_compose_botmonitor_vars_throws()
    {
        var config = ComposeStyleConfig(new Dictionary<string, string?>
        {
            ["Platform__Environment"] = "Production",
            ["BotMonitor__DataProtectionKeyBase64"] = "",
            ["BotMonitor__AllowDevKey"] = "",
            ["BotMonitor__DevDataProtectionKeyBase64"] = "",
        });

        Assert.Throws<InvalidOperationException>(() =>
            BotMonitorStartupValidation.ValidateBotMonitorConfiguration(
                config,
                new FakePlatformInfo("Production")));
    }
}
