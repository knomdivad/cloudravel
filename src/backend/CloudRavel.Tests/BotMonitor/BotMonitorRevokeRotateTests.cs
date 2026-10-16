using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorRevokeRotateTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Revoked_bot_token_is_rejected()
    {
        var cred = BotMonitorTestConfig.Credentials();
        var (token, secret, hash, enc) = cred.MintCredentials();
        var botId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(new BotMonitorBot
        {
            BotId = botId,
            TenantId = tenantId,
            TokenHash = hash,
            HmacSecretEnc = enc,
            RevokedAt = Now,
            CheckinIntervalMinutes = 60,
        });

        var svc = new BotMonitorHeartbeatService(repo, cred, new ConfigurationBuilder().Build(), () => Now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = Now.ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);

        var result = await svc.ProcessAsync(token, ts.ToString(), sig, body);
        Assert.False(result.Success);
        Assert.Equal(BotMonitorHeartbeatService.HeartbeatRejectReason.Unauthorized, result.Reason);
    }

    [Fact]
    public async Task Rotated_credentials_reject_old_token_and_accept_new()
    {
        var cred = BotMonitorTestConfig.Credentials();
        var oldMint = cred.MintCredentials();
        var newMint = cred.MintCredentials();
        var botId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByTokenHashAsync(cred.HashToken(oldMint.Token)).Returns((BotMonitorBot?)null);
        repo.GetBotByTokenHashAsync(cred.HashToken(newMint.Token)).Returns(new BotMonitorBot
        {
            BotId = botId,
            TenantId = tenantId,
            TokenHash = newMint.TokenHash,
            HmacSecretEnc = newMint.HmacSecretEnc,
            CheckinIntervalMinutes = 60,
        });
        repo.TryRecordNonceAsync(botId, Arg.Any<long>(), Arg.Any<string>()).Returns(true);
        repo.TryIncrementHeartbeatRateAsync(botId, Now, Arg.Any<int>()).Returns(true);

        var svc = new BotMonitorHeartbeatService(repo, cred, new ConfigurationBuilder().Build(), () => Now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = Now.ToUnixTimeSeconds();

        var oldSig = BotMonitorCrypto.ComputeSignatureHex(oldMint.HmacSecret, ts, body);
        var oldResult = await svc.ProcessAsync(oldMint.Token, ts.ToString(), oldSig, body);
        Assert.False(oldResult.Success);

        var newSig = BotMonitorCrypto.ComputeSignatureHex(newMint.HmacSecret, ts, body);
        var newResult = await svc.ProcessAsync(newMint.Token, ts.ToString(), newSig, body);
        Assert.True(newResult.Success);
    }
}
