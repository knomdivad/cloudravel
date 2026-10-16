using CloudRavel.Core.BotMonitor;
using CloudRavel.Api.BotMonitor;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorConfigurationTests
{
    [Fact]
    public void Production_without_data_protection_key_throws_on_startup_validation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var platform = new FakePlatformInfo("Production");
        Assert.Throws<InvalidOperationException>(() =>
            BotMonitorStartupValidation.ValidateBotMonitorConfiguration(config, platform));
    }

    [Fact]
    public void Production_with_valid_key_passes()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BotMonitor:DataProtectionKeyBase64"] = key })
            .Build();
        BotMonitorStartupValidation.ValidateBotMonitorConfiguration(config, new FakePlatformInfo("Production"));
    }

    [Fact]
    public void Development_allows_local_auth_signing_key_fallback()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LOCAL_AUTH_JWT_SIGNING_KEY"] = "dev-key" })
            .Build();
        BotMonitorStartupValidation.ValidateBotMonitorConfiguration(config, new FakePlatformInfo("Development"));
    }
}
