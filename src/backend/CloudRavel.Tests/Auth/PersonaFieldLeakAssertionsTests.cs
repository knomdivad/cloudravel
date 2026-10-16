using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class PersonaFieldLeakAssertionsTests
{
    [Fact]
    public void Bot_only_rejects_planted_top_level_cloud_property()
    {
        const string json = """{"orgId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","awsOrgCount":2}""";
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            PersonaFieldLeakAssertions.AssertNoLeak("bot-only", "organizations", "GET", json));
        Assert.Contains("cloud-leak:awsOrgCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cloud_only_rejects_planted_top_level_bot_property()
    {
        const string json = """{"code":"OK","botCount":3}""";
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            PersonaFieldLeakAssertions.AssertNoLeak("cloud-only", "inventory/resources", "GET", json));
        Assert.Contains("bot-leak:botCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bot_only_rejects_nested_cloud_property_inside_array()
    {
        const string json = """
            {"organizations":[{"orgId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","name":"Acme","resourceCount":9}]}
            """;
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            PersonaFieldLeakAssertions.AssertNoLeak("bot-only", "organizations", "GET", json));
        Assert.Contains("cloud-leak:resourceCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_common_error_shape_passes_both_personas()
    {
        const string json = """{"code":"FEATURE_CLOUD_MONITORING_REQUIRED","message":"denied"}""";
        PersonaFieldLeakAssertions.AssertNoLeak("bot-only", "inventory/resources", "GET", json);
        PersonaFieldLeakAssertions.AssertNoLeak("cloud-only", "bot-monitor/portal/bots", "GET", json);
    }
}
