using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Api.BotMonitor;

public static class BotMonitorStartupValidation
{
    public static void ValidateBotMonitorConfiguration(IConfiguration configuration, IPlatformInfo platform)
    {
        var allowDevKey = bool.TryParse(configuration["BotMonitor:AllowDevKey"], out var devFlag) && devFlag;
        _ = BotMonitorCrypto.ResolveProtectionKey(
            configuration["BotMonitor:DataProtectionKeyBase64"],
            platform.Environment,
            allowDevKey,
            configuration["BotMonitor:DevDataProtectionKeyBase64"]);
    }
}
