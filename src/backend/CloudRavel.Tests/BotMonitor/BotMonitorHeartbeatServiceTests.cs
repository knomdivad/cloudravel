using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorHeartbeatServiceTests
{
    private readonly IBotMonitorRepository _repo = Substitute.For<IBotMonitorRepository>();
    private readonly BotMonitorCredentialService _credentials;
    private readonly DateTime _now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    public BotMonitorHeartbeatServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LOCAL_AUTH_JWT_SIGNING_KEY"] = "test-signing-key" })
            .Build();
        _credentials = new BotMonitorCredentialService(config);
    }

    [Fact]
    public async Task Timestamp_skew_future_rejected()
    {
        var (token, secret, hash, enc) = _credentials.MintCredentials();
        var bot = new BotMonitorBot { BotId = Guid.NewGuid(), TenantId = Guid.NewGuid(), TokenHash = hash, HmacSecretEnc = enc, CheckinIntervalMinutes = 60 };
        _repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(bot);

        var svc = new BotMonitorHeartbeatService(_repo, _credentials, new ConfigurationBuilder().Build(), () => _now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = _now.AddMinutes(10).ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);

        var result = await svc.ProcessAsync(token, ts.ToString(), sig, body);
        Assert.False(result.Success);
        Assert.Equal(BotMonitorHeartbeatService.HeartbeatRejectReason.TimestampSkew, result.Reason);
    }

    [Fact]
    public async Task Timestamp_skew_past_rejected()
    {
        var (token, secret, hash, enc) = _credentials.MintCredentials();
        var bot = new BotMonitorBot { BotId = Guid.NewGuid(), TenantId = Guid.NewGuid(), TokenHash = hash, HmacSecretEnc = enc, CheckinIntervalMinutes = 60 };
        _repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(bot);

        var svc = new BotMonitorHeartbeatService(_repo, _credentials, new ConfigurationBuilder().Build(), () => _now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = _now.AddMinutes(-10).ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);

        var result = await svc.ProcessAsync(token, ts.ToString(), sig, body);
        Assert.False(result.Success);
        Assert.Equal(BotMonitorHeartbeatService.HeartbeatRejectReason.TimestampSkew, result.Reason);
    }

    [Fact]
    public async Task Replay_of_identical_request_rejected()
    {
        var (token, secret, hash, enc) = _credentials.MintCredentials();
        var botId = Guid.NewGuid();
        var bot = new BotMonitorBot { BotId = botId, TenantId = Guid.NewGuid(), TokenHash = hash, HmacSecretEnc = enc, CheckinIntervalMinutes = 60 };
        _repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(bot);
        _repo.TryRecordNonceAsync(botId, Arg.Any<long>(), Arg.Any<string>()).Returns(true, false);
        _repo.TryIncrementHeartbeatRateAsync(botId, _now, Arg.Any<int>()).Returns(true);

        var svc = new BotMonitorHeartbeatService(_repo, _credentials, new ConfigurationBuilder().Build(), () => _now);
        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = _now.ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);

        Assert.True((await svc.ProcessAsync(token, ts.ToString(), sig, body)).Success);
        var replay = await svc.ProcessAsync(token, ts.ToString(), sig, body);
        Assert.False(replay.Success);
        Assert.Equal(BotMonitorHeartbeatService.HeartbeatRejectReason.Replay, replay.Reason);
    }

    [Fact]
    public async Task Valid_request_ingests_for_token_bot_only()
    {
        var tenantA = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var (token, secret, hash, enc) = _credentials.MintCredentials();
        var bot = new BotMonitorBot { BotId = botId, TenantId = tenantA, TokenHash = hash, HmacSecretEnc = enc, CheckinIntervalMinutes = 60 };
        _repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(bot);
        _repo.TryRecordNonceAsync(botId, Arg.Any<long>(), Arg.Any<string>()).Returns(true);
        _repo.TryIncrementHeartbeatRateAsync(botId, _now, Arg.Any<int>()).Returns(true);

        var svc = new BotMonitorHeartbeatService(_repo, _credentials, new ConfigurationBuilder().Build(), () => _now);
        var body = """{"status":"ok","botId":"00000000-0000-0000-0000-000000000099"}"""u8.ToArray();
        var ts = _now.ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(secret, ts, body);

        Assert.True((await svc.ProcessAsync(token, ts.ToString(), sig, body)).Success);
        await _repo.Received(1).IngestHeartbeatAsync(tenantA, botId, Arg.Any<BotMonitorHeartbeatPayload>(), _now, Arg.Any<CancellationToken>());
    }
}

internal static class DateTimeUnixExtensionsTests
{
    public static long ToUnixTimeSeconds(this DateTime utc)
        => new DateTimeOffset(utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()).ToUnixTimeSeconds();
}
