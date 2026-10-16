using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;

namespace CloudRavel.Tests.BotMonitor;

/// <summary>In-memory bot store for credential lifecycle tests (rotate/revoke).</summary>
internal sealed class InMemoryBotMonitorCredentialStore : IBotMonitorRepository
{
    private readonly Dictionary<Guid, BotMonitorBot> _bots = new();
    private readonly HashSet<string> _tokenHashes = new();

    public Task<BotMonitorBot?> GetBotByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var bot = _bots.Values.FirstOrDefault(b => b.TokenHash == tokenHash && b.RevokedAt is null);
        return Task.FromResult(bot);
    }

    public Task<bool> UpdateBotCredentialsAsync(
        Guid tenantId, Guid botId, string newTokenHash, string newHmacSecretEnc, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(botId, out var bot) || bot.TenantId != tenantId || bot.RevokedAt != null)
            return Task.FromResult(false);
        _tokenHashes.Remove(bot.TokenHash);
        bot.TokenHash = newTokenHash;
        bot.HmacSecretEnc = newHmacSecretEnc;
        _tokenHashes.Add(newTokenHash);
        return Task.FromResult(true);
    }

    public Task<bool> RevokeBotAsync(Guid tenantId, Guid botId, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(botId, out var bot) || bot.TenantId != tenantId) return Task.FromResult(false);
        bot.RevokedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public void Seed(BotMonitorBot bot) => _bots[bot.BotId] = bot;

    public Task<bool> TryRecordNonceAsync(Guid botId, long ts, string sig, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> TryIncrementHeartbeatRateAsync(Guid botId, DateTime nowUtc, int maxPerMinute, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task IngestHeartbeatAsync(
        Guid tenantId, Guid botId, BotMonitorHeartbeatPayload payload, DateTime receivedAtUtc, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task PurgeExpiredNoncesAsync(DateTime olderThanUtc, CancellationToken ct = default) => Task.CompletedTask;

    public Task<BotMonitorTenant?> GetTenantAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<BotMonitorTenant?>(null);
    public Task<IReadOnlyList<BotMonitorTenant>> ListTenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BotMonitorTenant>>(Array.Empty<BotMonitorTenant>());
    public Task<BotMonitorTenant?> LinkOrganizationAsync(Guid organizationId, string organizationName, CancellationToken ct = default) => Task.FromResult<BotMonitorTenant?>(null);
    public Task<BotMonitorBot?> GetBotByIdAsync(Guid tenantId, Guid botId, CancellationToken ct = default) => Task.FromResult<BotMonitorBot?>(null);
    public Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(BotMonitorBot, BotMonitorBotState?, string?)>>(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?)>());
    public Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>> ListAllBotsAsync(Guid? filterTenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(BotMonitorBot, BotMonitorBotState?, string?, string)>>(Array.Empty<(BotMonitorBot, BotMonitorBotState?, string?, string)>());
    public Task<BotMonitorBot> InsertBotAsync(BotMonitorBot bot, CancellationToken ct = default) => Task.FromResult(bot);
    public Task<IReadOnlyList<BotMonitorWeeklyBucket>> GetWeeklyHistoryAsync(Guid tenantId, Guid botId, int weeks, DateTime nowUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BotMonitorWeeklyBucket>>(Array.Empty<BotMonitorWeeklyBucket>());
    public Task<string?> StoreMagicLinkAsync(Guid tenantId, string tokenHash, DateTime expiresAtUtc, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<Guid?> RedeemMagicLinkAsync(string tokenHash, DateTime nowUtc, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
}
