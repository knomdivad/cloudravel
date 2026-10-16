using CloudRavel.Core.BotMonitor;

namespace CloudRavel.Core.Interfaces;

public interface IBotMonitorRepository
{
    Task<BotMonitorTenant?> GetTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<BotMonitorTenant>> ListTenantsAsync(CancellationToken ct = default);
    /// <summary>Registers bot monitor for an existing organization id (same as portal tenant id).</summary>
    Task<BotMonitorTenant?> LinkOrganizationAsync(Guid organizationId, string organizationName, CancellationToken ct = default);

    Task<BotMonitorBot?> GetBotByIdAsync(Guid tenantId, Guid botId, CancellationToken ct = default);
    Task<BotMonitorBot?> GetBotByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantAsync(
        Guid tenantId, Guid? ownerUserId = null, CancellationToken ct = default);
    Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck, string TenantName)>> ListAllBotsAsync(
        Guid? filterTenantId, CancellationToken ct = default);

    Task<BotMonitorBot> InsertBotAsync(BotMonitorBot bot, CancellationToken ct = default);

    Task<bool> RevokeBotAsync(Guid tenantId, Guid botId, CancellationToken ct = default);
    Task<bool> UpdateBotCredentialsAsync(
        Guid tenantId, Guid botId, string newTokenHash, string newHmacSecretEnc, CancellationToken ct = default);

    Task<bool> TryRecordNonceAsync(Guid botId, long ts, string sig, CancellationToken ct = default);
    Task PurgeExpiredNoncesAsync(DateTime olderThanUtc, CancellationToken ct = default);

    Task<bool> TryIncrementHeartbeatRateAsync(Guid botId, DateTime nowUtc, int maxPerMinute, CancellationToken ct = default);

    Task IngestHeartbeatAsync(
        Guid tenantId, Guid botId, BotMonitorHeartbeatPayload payload, DateTime receivedAtUtc, CancellationToken ct = default);

    Task<IReadOnlyList<BotMonitorWeeklyBucket>> GetWeeklyHistoryAsync(
        Guid tenantId, Guid botId, int weeks, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<(DateTime ReceivedAt, string Status)>> GetRecentCheckinsAsync(
        Guid tenantId, Guid botId, int hours, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid TenantId, string TenantName, int BotCount, int OnlineCount)>> ListFleetTenantSummariesAsync(
        DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid UserId, string DisplayName, string Email, int BotCount)>> ListFleetUsersForTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<(BotMonitorBot Bot, BotMonitorBotState? State, string? LastKeepCheck)>> ListBotsForTenantUserAsync(
        Guid tenantId, Guid userId, CancellationToken ct = default);

    Task<string?> StoreMagicLinkAsync(Guid tenantId, string tokenHash, DateTime expiresAtUtc, CancellationToken ct = default);
    Task<Guid?> RedeemMagicLinkAsync(string tokenHash, DateTime nowUtc, CancellationToken ct = default);
}
