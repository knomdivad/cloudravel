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
    /// <summary>Lookup key from bot_monitor_bot_types (separate from platform).</summary>
    public string BotTypeKey { get; set; } = "other_plugin";
    public int CheckinIntervalMinutes { get; set; } = 60;
}

public sealed class CreateBotMonitorBotTypeRequest
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class BotMonitorBotTypeDto
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class BotMonitorBotTypeCountDto
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Count { get; set; }
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
    public IReadOnlyList<BotMonitorBotTypeCountDto> TypeCounts { get; set; } = Array.Empty<BotMonitorBotTypeCountDto>();
}

public sealed class BotMonitorFleetUserDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public IReadOnlyList<BotMonitorBotTypeCountDto> TypeCounts { get; set; } = Array.Empty<BotMonitorBotTypeCountDto>();
}

public sealed class BotMonitorBotListItemDto
{
    public Guid BotId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string BotTypeKey { get; set; } = string.Empty;
    public string BotTypeDisplayName { get; set; } = string.Empty;
    public string Presence { get; set; } = string.Empty;
    public DateTime? LastSeenAt { get; set; }
    public string? LastError { get; set; }
    public string? LastKeepCheck { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class BotMonitorPortalUserBotCountDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public IReadOnlyList<BotMonitorBotTypeCountDto> TypeCounts { get; set; } = Array.Empty<BotMonitorBotTypeCountDto>();
}

public sealed class BotMonitorPortalBotDto
{
    public Guid BotId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? OwnerDisplayName { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string BotTypeKey { get; set; } = string.Empty;
    public string BotTypeDisplayName { get; set; } = string.Empty;
    public string Presence { get; set; } = string.Empty;
    public DateTime? LastSeenAt { get; set; }
    public IReadOnlyList<BotMonitorWeeklyBucketDto> WeeklyHistory { get; set; } = Array.Empty<BotMonitorWeeklyBucketDto>();
    public IReadOnlyList<BotMonitorCheckinPointDto> RecentCheckins { get; set; } = Array.Empty<BotMonitorCheckinPointDto>();
    public BotMonitorHealthMetricsDto? HealthMetrics { get; set; }
}

public sealed class BotMonitorPortalDashboardDto
{
    /// <summary>user = only the signed in user's bots; organization = tenant administrator view.</summary>
    public string ViewScope { get; set; } = "user";
    public int TotalBots { get; set; }
    public int OnlineCount { get; set; }
    public int StaleCount { get; set; }
    public int OfflineCount { get; set; }
    public double AverageUptimePercent { get; set; }
    public IReadOnlyList<BotMonitorPortalUserBotCountDto> UserBotCounts { get; set; } = Array.Empty<BotMonitorPortalUserBotCountDto>();
    public IReadOnlyList<BotMonitorBotTypeCountDto> TypeCounts { get; set; } = Array.Empty<BotMonitorBotTypeCountDto>();
    public IReadOnlyList<BotMonitorPortalBotDto> Bots { get; set; } = Array.Empty<BotMonitorPortalBotDto>();
}

public sealed class BotMonitorCheckinPointDto
{
    public DateTime ReceivedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class BotMonitorHealthMetricsDto
{
    public BotMonitorReportedMetricsDto? Reported { get; set; }
    public BotMonitorDerivedMetricsDto Derived { get; set; } = new();
    public DateTime? AsOfUtc { get; set; }
}

public sealed class BotMonitorReportedMetricsDto
{
    public BotMonitorRoutineReportDto? Routine { get; set; }
    public IReadOnlyList<BotMonitorConnectorReportDto> Connectors { get; set; } = Array.Empty<BotMonitorConnectorReportDto>();
    public BotMonitorActionCountsDto? Actions { get; set; }
    public BotMonitorInboxReportDto? Inbox { get; set; }
    public BotMonitorApprovalReportDto? Approvals { get; set; }
    public BotMonitorQueueReportDto? Queue { get; set; }
    public int? Escalations { get; set; }
}

public sealed class BotMonitorRoutineReportDto
{
    public string? Id { get; set; }
    public DateTime? ExpectedAt { get; set; }
    public DateTime? RanAt { get; set; }
    public string? Result { get; set; }
}

public sealed class BotMonitorConnectorReportDto
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class BotMonitorActionCountsDto
{
    public int Attempted { get; set; }
    public int Failed { get; set; }
}

public sealed class BotMonitorInboxReportDto
{
    public int Unanswered { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorApprovalReportDto
{
    public int Pending { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorQueueReportDto
{
    public int Open { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorDerivedMetricsDto
{
    public int ConsecutiveFailureStreak { get; set; }
    public bool RoutineMissed { get; set; }
    public int CheckInLatenessMinutes { get; set; }
    public int RunsPerDay { get; set; }
    public int ActionErrorsReported { get; set; }
}

public sealed class BotMonitorBotMetricsItemDto
{
    public Guid BotId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public BotMonitorHealthMetricsDto Metrics { get; set; } = new();
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
