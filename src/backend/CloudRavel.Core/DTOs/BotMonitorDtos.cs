using CloudRavel.Core.BotMonitor;

namespace CloudRavel.Core.DTOs;

public sealed class CreateBotMonitorTenantRequest
{
    /// <summary>Must match an existing organization (portal workspace) id.</summary>
    public Guid OrganizationId { get; set; }
}

public sealed class CreateBotMonitorBotRequest
{
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public BotMonitorPlatform Platform { get; set; } = BotMonitorPlatform.Other;
    public int CheckinIntervalMinutes { get; set; } = 60;
}

public sealed class CreateBotMonitorBotResponse
{
    public Guid BotId { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string HmacSecret { get; set; } = string.Empty;
}

public sealed class BotMonitorFleetTenantDto
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public int OnlineCount { get; set; }
}

public sealed class BotMonitorFleetUserDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BotCount { get; set; }
}

public sealed class BotMonitorBotListItemDto
{
    public Guid BotId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Presence { get; set; } = string.Empty;
    public DateTime? LastSeenAt { get; set; }
    public string? LastError { get; set; }
    public string? LastKeepCheck { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class BotMonitorPortalBotDto
{
    public Guid BotId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Presence { get; set; } = string.Empty;
    public DateTime? LastSeenAt { get; set; }
    public IReadOnlyList<BotMonitorWeeklyBucketDto> WeeklyHistory { get; set; } = Array.Empty<BotMonitorWeeklyBucketDto>();
    public IReadOnlyList<BotMonitorCheckinPointDto> RecentCheckins { get; set; } = Array.Empty<BotMonitorCheckinPointDto>();
}

public sealed class BotMonitorPortalDashboardDto
{
    public int TotalBots { get; set; }
    public int OnlineCount { get; set; }
    public int StaleCount { get; set; }
    public int OfflineCount { get; set; }
    public double AverageUptimePercent { get; set; }
    public IReadOnlyList<BotMonitorPortalBotDto> Bots { get; set; } = Array.Empty<BotMonitorPortalBotDto>();
}

public sealed class BotMonitorCheckinPointDto
{
    public DateTime ReceivedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class BotMonitorWeeklyBucketDto
{
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public double UptimePercent { get; set; }
    public int KeepPassCount { get; set; }
    public int KeepWarnCount { get; set; }
    public int KeepFailCount { get; set; }
    public int ErrorCount { get; set; }
}

public sealed class BotMonitorMagicLinkRequest
{
    public string Email { get; set; } = string.Empty;
}

public sealed class BotMonitorMagicLinkStubResponse
{
    public string Message { get; set; } = string.Empty;
    public string? DevRedeemPath { get; set; }
}
