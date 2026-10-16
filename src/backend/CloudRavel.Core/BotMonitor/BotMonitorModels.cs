namespace CloudRavel.Core.BotMonitor;

public enum BotMonitorPlatform
{
    GrokBot,
    Email,
    Other,
}

public enum BotMonitorStatus
{
    Ok,
    Busy,
    Error,
    Offline,
}

public enum BotMonitorKeepCheck
{
    Pass,
    Warn,
    Fail,
    None,
}

public enum BotMonitorPresence
{
    Online,
    Stale,
    Offline,
}

public sealed class BotMonitorTenant
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class BotMonitorBot
{
    public Guid BotId { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public BotMonitorPlatform Platform { get; set; }
    public string BotTypeKey { get; set; } = "other_plugin";
    public string? BotTypeDisplayName { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string HmacSecretEnc { get; set; } = string.Empty;
    public int CheckinIntervalMinutes { get; set; } = 60;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? OwnerUserId { get; set; }
    /// <summary>Hydrated on list queries when joined to users.</summary>
    public string? OwnerDisplayName { get; set; }
}

public sealed class BotMonitorBotState
{
    public Guid BotId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public BotMonitorStatus? LastStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
}

public sealed class BotMonitorHeartbeatPayload
{
    public BotMonitorStatus Status { get; set; }
    public string? LastTask { get; set; }
    public List<string>? Errors { get; set; }
    public BotMonitorKeepCheck KeepCheck { get; set; } = BotMonitorKeepCheck.None;
    public string? KeepCheckDetail { get; set; }
    public BotMonitorReportedMetrics? Metrics { get; set; }
}

public sealed class BotMonitorWeeklyBucket
{
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public double UptimePercent { get; set; }
    public int KeepPassCount { get; set; }
    public int KeepWarnCount { get; set; }
    public int KeepFailCount { get; set; }
    public int ErrorCount { get; set; }
}
