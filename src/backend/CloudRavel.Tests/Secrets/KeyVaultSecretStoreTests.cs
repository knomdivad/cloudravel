using Xunit;

using CloudRavel.Infrastructure.Secrets;

namespace CloudRavel.Tests.Secrets;

public sealed class KeyVaultSecretStoreTests
{
    [Theory]
    [InlineData("cloudaccount-abc", "cloudaccount-abc")]
    [InlineData("org/guid/sso-secret", "org-guid-sso-secret")]
    [InlineData("path_with.dots", "path-with-dots")]
    [InlineData("--trim--", "trim")]
    public void Sanitize_maps_to_key_vault_safe_names(string input, string expected)
    {
        Assert.Equal(expected, KeyVaultSecretStore.Sanitize(input));
    }

    [Fact]
    public void Sanitize_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => KeyVaultSecretStore.Sanitize("  "));
    }
}
