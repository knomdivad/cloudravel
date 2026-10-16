using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Api.BotMonitor;

public static class BotMonitorStartupValidation
{
    public static void ValidateBotMonitorConfiguration(IConfiguration configuration, IPlatformInfo platform)
    {
        var isDevelopment = platform.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase);
        _ = BotMonitorCrypto.ResolveProtectionKey(
            configuration["BotMonitor:DataProtectionKeyBase64"],
            isDevelopment,
            configuration["LOCAL_AUTH_JWT_SIGNING_KEY"]);
    }
}
