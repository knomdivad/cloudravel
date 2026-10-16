using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorDemoSeedGuardTests
{
    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("development", false, true)]
    [InlineData("Staging", false, false)]
    [InlineData("Production", false, false)]
    [InlineData("Development", true, false)]
    public void Demo_seed_guard_blocks_non_development_and_production_orgs(
        string platformEnvironment, bool hasProductionOrganizations, bool expectedAllow)
    {
        Assert.Equal(expectedAllow, BotMonitorDemoSeedGuard.AllowsDemoSeed(platformEnvironment, hasProductionOrganizations));
    }
}
