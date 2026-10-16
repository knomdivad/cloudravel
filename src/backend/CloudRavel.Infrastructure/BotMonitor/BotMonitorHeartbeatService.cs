using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Infrastructure.BotMonitor;

public sealed class BotMonitorHeartbeatService
{
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxSkewSeconds = 300;
    private const int MaxPerMinute = 12;

    private readonly IBotMonitorRepository _repo;
    private readonly BotMonitorCredentialService _credentials;
    private readonly Func<DateTime> _clock;

    public BotMonitorHeartbeatService(
        IBotMonitorRepository repo,
        BotMonitorCredentialService credentials,
        IConfiguration configuration,
        Func<DateTime>? clock = null)
    {
        _repo = repo;
        _credentials = credentials;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public enum HeartbeatRejectReason
    {
        None,
        Unauthorized,
        BadSignature,
        TimestampSkew,
        Replay,
        PayloadTooLarge,
        RateLimited,
    }

    public sealed class HeartbeatResult
    {
        public bool Success { get; init; }
        public HeartbeatRejectReason Reason { get; init; }
    }

    public async Task<HeartbeatResult> ProcessAsync(
        string bearerToken,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return Fail(HeartbeatRejectReason.Unauthorized);

        var tokenHash = _credentials.HashToken(bearerToken.Trim());
        var bot = await _repo.GetBotByTokenHashAsync(tokenHash, ct);
        if (bot is null || bot.RevokedAt != null) return Fail(HeartbeatRejectReason.Unauthorized);

        if (body.Length > MaxBodyBytes) return Fail(HeartbeatRejectReason.PayloadTooLarge);

        if (!long.TryParse(timestampHeader, out var ts)) return Fail(HeartbeatRejectReason.TimestampSkew);
        var now = _clock();
        var skew = Math.Abs(now.ToUnixTimeSeconds() - ts);
        if (skew > MaxSkewSeconds) return Fail(HeartbeatRejectReason.TimestampSkew);

        string hmacSecret;
        try
        {
            hmacSecret = _credentials.UnprotectHmacSecret(bot.HmacSecretEnc);
        }
        catch
        {
            return Fail(HeartbeatRejectReason.Unauthorized);
        }

        if (!BotMonitorCrypto.VerifySignature(hmacSecret, ts, body.Span, signatureHeader ?? string.Empty))
            return Fail(HeartbeatRejectReason.BadSignature);

        var sig = (signatureHeader ?? string.Empty).Trim().ToLowerInvariant();
        if (!await _repo.TryRecordNonceAsync(bot.BotId, ts, sig, ct))
            return Fail(HeartbeatRejectReason.Replay);

        if (!await _repo.TryIncrementHeartbeatRateAsync(bot.BotId, now, MaxPerMinute, ct))
            return Fail(HeartbeatRejectReason.RateLimited);

        BotMonitorHeartbeatPayload payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<BotMonitorHeartbeatPayload>(body.Span,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new BotMonitorHeartbeatPayload();
        }
        catch
        {
            payload = new BotMonitorHeartbeatPayload();
        }

        await _repo.IngestHeartbeatAsync(bot.TenantId, bot.BotId, payload, now, ct);
        await _repo.PurgeExpiredNoncesAsync(now.AddMinutes(-10), ct);
        return new HeartbeatResult { Success = true, Reason = HeartbeatRejectReason.None };
    }

    private static HeartbeatResult Fail(HeartbeatRejectReason reason) =>
        new() { Success = false, Reason = reason };
}

internal static class DateTimeUnixExtensions
{
    public static long ToUnixTimeSeconds(this DateTime utc)
        => new DateTimeOffset(utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()).ToUnixTimeSeconds();
}
