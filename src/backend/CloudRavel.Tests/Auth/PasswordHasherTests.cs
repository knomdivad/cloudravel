using CloudRavel.Core.Auth;
using Xunit;

namespace CloudRavel.Tests.Auth;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = PasswordHasher.Hash("ChangeMe123!");
        Assert.True(PasswordHasher.Verify("ChangeMe123!", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Verify_rejects_null_or_malformed()
    {
        Assert.False(PasswordHasher.Verify("x", null));
        Assert.False(PasswordHasher.Verify("x", "not-a-hash"));
    }

    [Fact]
    public void Known_bootstrap_hash_verifies()
    {
        // Seeded in 004-local-auth.sql — must keep working for local DX.
        const string seedHash =
            "pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE=";
        Assert.True(PasswordHasher.Verify("ChangeMe123!", seedHash));
    }
}
