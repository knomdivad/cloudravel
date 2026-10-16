using CloudRavel.Core.BotMonitor;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorRotateEndToEndTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task After_rotate_old_credentials_return_unauthorized_and_new_succeed()
    {
        var cred = BotMonitorTestConfig.Credentials();
        var oldMint = cred.MintCredentials();
        var newMint = cred.MintCredentials();
        var botId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var store = new InMemoryBotMonitorCredentialStore();
        store.Seed(new BotMonitorBot
        {
            BotId = botId,
            TenantId = tenantId,
            TokenHash = oldMint.TokenHash,
            HmacSecretEnc = oldMint.HmacSecretEnc,
            CheckinIntervalMinutes = 60,
        });

        var svc = new BotMonitorHeartbeatService(store, cred, new ConfigurationBuilder().Build(), () => Now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = Now.ToUnixTimeSeconds();

        var oldSig = BotMonitorCrypto.ComputeSignatureHex(oldMint.HmacSecret, ts, body);
        Assert.True((await svc.ProcessAsync(oldMint.Token, ts.ToString(), oldSig, body)).Success);

        Assert.True(await store.UpdateBotCredentialsAsync(tenantId, botId, newMint.TokenHash, newMint.HmacSecretEnc));

        var oldAfter = await svc.ProcessAsync(oldMint.Token, ts.ToString(), oldSig, body);
        Assert.False(oldAfter.Success);
        Assert.Equal(BotMonitorHeartbeatService.HeartbeatRejectReason.Unauthorized, oldAfter.Reason);

        var newTs = ts + 1;
        var newSig = BotMonitorCrypto.ComputeSignatureHex(newMint.HmacSecret, newTs, body);
        var newResult = await svc.ProcessAsync(newMint.Token, newTs.ToString(), newSig, body);
        Assert.True(newResult.Success);
    }
}
