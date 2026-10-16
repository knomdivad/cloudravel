using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Infrastructure.BotMonitor;

public sealed class BotMonitorCredentialService
{
    private readonly byte[] _protectionKey;
    private readonly string? _tokenPepper;

    public BotMonitorCredentialService(IConfiguration configuration, IPlatformInfo platform)
    {
        var isDevelopment = platform.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase);
        _protectionKey = BotMonitorCrypto.ResolveProtectionKey(
            configuration["BotMonitor:DataProtectionKeyBase64"],
            isDevelopment,
            configuration["LOCAL_AUTH_JWT_SIGNING_KEY"]);
        _tokenPepper = configuration["BotMonitor:TokenPepper"];
    }

    public string HashToken(string plaintextToken) => BotMonitorCrypto.HashToken(plaintextToken, _tokenPepper);

    public string ProtectHmacSecret(string plaintextSecretBase64) =>
        BotMonitorCrypto.ProtectSecret(plaintextSecretBase64, _protectionKey);

    public string UnprotectHmacSecret(string protectedForm) =>
        BotMonitorCrypto.UnprotectSecret(protectedForm, _protectionKey);

    public (string Token, string HmacSecret, string TokenHash, string HmacSecretEnc) MintCredentials()
    {
        var token = BotMonitorCrypto.GenerateToken();
        var secret = BotMonitorCrypto.GenerateHmacSecret();
        return (token, secret, HashToken(token), ProtectHmacSecret(secret));
    }
}
