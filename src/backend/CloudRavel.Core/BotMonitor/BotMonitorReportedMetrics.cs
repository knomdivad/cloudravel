namespace CloudRavel.Core.BotMonitor;

public sealed class BotMonitorRoutineReport
{
    public string? Id { get; set; }
    public DateTime? ExpectedAt { get; set; }
    public DateTime? RanAt { get; set; }
    public string? Result { get; set; }
}

public sealed class BotMonitorConnectorReport
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "ok";
}

public sealed class BotMonitorActionCounts
{
    public int Attempted { get; set; }
    public int Failed { get; set; }
}

public sealed class BotMonitorInboxReport
{
    public int Unanswered { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorApprovalReport
{
    public int Pending { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorQueueReport
{
    public int Open { get; set; }
    public int OldestMinutes { get; set; }
}

public sealed class BotMonitorReportedMetrics
{
    public BotMonitorRoutineReport? Routine { get; set; }
    public List<BotMonitorConnectorReport>? Connectors { get; set; }
    public BotMonitorActionCounts? Actions { get; set; }
    public BotMonitorInboxReport? Inbox { get; set; }
    public BotMonitorApprovalReport? Approvals { get; set; }
    public BotMonitorQueueReport? Queue { get; set; }
    public int? Escalations { get; set; }
}

public sealed class BotMonitorDerivedMetrics
{
    public int ConsecutiveFailureStreak { get; set; }
    public bool RoutineMissed { get; set; }
    public int CheckInLatenessMinutes { get; set; }
    public int RunsPerDay { get; set; }
    public int ActionErrorsReported { get; set; }
}

public sealed class BotMonitorHealthMetricsSnapshot
{
    public BotMonitorReportedMetrics? Reported { get; set; }
    public BotMonitorDerivedMetrics Derived { get; set; } = new();
    public DateTime? AsOfUtc { get; set; }
}
