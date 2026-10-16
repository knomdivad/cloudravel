using System.Text;
using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorCryptoTests
{
    [Fact]
    public void Token_hash_never_equals_plaintext()
    {
        var token = BotMonitorCrypto.GenerateToken();
        var hash = BotMonitorCrypto.HashToken(token, pepper: "test-pepper");
        Assert.NotEqual(token, hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Hmac_valid_for_intact_body()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var body = """{"status":"ok"}"""u8.ToArray();
        const long ts = 1_700_000_000;
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);
        Assert.True(BotMonitorCrypto.VerifySignature(secret, ts, body, sig));
    }

    [Fact]
    public void Hmac_invalid_when_body_tampered()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var body = """{"status":"ok"}"""u8.ToArray();
        const long ts = 1_700_000_000;
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);
        var tampered = """{"status":"error"}"""u8.ToArray();
        Assert.False(BotMonitorCrypto.VerifySignature(secret, ts, tampered, sig));
    }

    [Fact]
    public void Hmac_invalid_for_wrong_secret()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var other = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var body = Encoding.UTF8.GetBytes("payload");
        const long ts = 100;
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);
        Assert.False(BotMonitorCrypto.VerifySignature(other, ts, body, sig));
    }

    [Fact]
    public void Protect_roundtrip()
    {
        var key = BotMonitorCrypto.DeriveProtectionKey(null, "unit-test-seed");
        var enc = BotMonitorCrypto.ProtectSecret("secret-value", key);
        Assert.DoesNotContain("secret-value", enc);
        Assert.Equal("secret-value", BotMonitorCrypto.UnprotectSecret(enc, key));
    }
}
