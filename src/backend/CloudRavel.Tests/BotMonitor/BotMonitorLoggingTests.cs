using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorLoggingTests
{
    [Fact]
    public async Task Heartbeat_processing_does_not_log_token_or_hmac_secret()
    {
        var cred = BotMonitorTestConfig.Credentials();
        var mint = cred.MintCredentials();
        var botId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(new BotMonitorBot
        {
            BotId = botId,
            TenantId = tenantId,
            TokenHash = mint.TokenHash,
            HmacSecretEnc = mint.HmacSecretEnc,
            CheckinIntervalMinutes = 60,
        });
        repo.TryRecordNonceAsync(botId, Arg.Any<long>(), Arg.Any<string>()).Returns(true);
        repo.TryIncrementHeartbeatRateAsync(botId, Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);

        var capture = new CaptureLogger<BotMonitorHeartbeatService>();
        var svc = new BotMonitorHeartbeatService(
            repo,
            cred,
            new ConfigurationBuilder().Build(),
            () => now,
            capture);

        var body = """{"status":"ok"}"""u8.ToArray();
        var ts = new DateTimeOffset(now).ToUnixTimeSeconds();
        var sig = BotMonitorCrypto.ComputeSignatureHex(mint.HmacSecret, ts, body);

        await svc.ProcessAsync(mint.Token, ts.ToString(), sig, body);

        var combined = string.Join('\n', capture.Messages);
        Assert.DoesNotContain(mint.Token, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(mint.HmacSecret, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(sig, combined, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
