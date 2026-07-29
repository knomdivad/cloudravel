using Xunit;

using CloudRavel.Core.Auth;

namespace CloudRavel.Tests.Auth;

public sealed class LocalAuthConstantsTests
{
    [Fact]
    public void DeriveSigningKey_is_deterministic_and_256_bits()
    {
        var a = LocalAuthConstants.DeriveSigningKey("dev-only-change-me-in-production");
        var b = LocalAuthConstants.DeriveSigningKey("dev-only-change-me-in-production");
        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(
            LocalAuthConstants.DeriveSigningKey("other"),
            a);
    }
}
