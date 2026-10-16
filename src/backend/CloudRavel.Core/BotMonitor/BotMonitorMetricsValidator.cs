using System.Text.Json;

namespace CloudRavel.Core.BotMonitor;

/// <summary>Sanitizes optional heartbeat metrics (size caps, bounded counts).</summary>
public static class BotMonitorMetricsValidator
{
    public const int MaxSerializedBytes = 3500;
    public const int MaxConnectors = 10;
    public const int MaxCount = 100_000;
    public const int MaxOldestMinutes = 525_600;

    public static BotMonitorReportedMetrics? Sanitize(BotMonitorReportedMetrics? input)
    {
        if (input is null) return null;
        var rawJson = JsonSerializer.Serialize(input);
        if (rawJson.Length > MaxSerializedBytes)
            throw new InvalidOperationException("Reported metrics exceed the size cap.");

        var m = new BotMonitorReportedMetrics
        {
            Routine = SanitizeRoutine(input.Routine),
            Connectors = SanitizeConnectors(input.Connectors),
            Actions = SanitizeActions(input.Actions),
            Inbox = SanitizeInbox(input.Inbox),
            Approvals = SanitizeApprovals(input.Approvals),
            Queue = SanitizeQueue(input.Queue),
            Escalations = ClampCount(input.Escalations),
        };

        if (m.Routine is null && m.Connectors is null && m.Actions is null && m.Inbox is null
            && m.Approvals is null && m.Queue is null && m.Escalations is null)
            return null;

        var json = JsonSerializer.Serialize(m);
        if (json.Length > MaxSerializedBytes)
            throw new InvalidOperationException("Reported metrics exceed the size cap after sanitization.");

        return m;
    }

    private static BotMonitorRoutineReport? SanitizeRoutine(BotMonitorRoutineReport? r)
    {
        if (r is null) return null;
        if (string.IsNullOrWhiteSpace(r.Id) && r.ExpectedAt is null && r.RanAt is null && string.IsNullOrWhiteSpace(r.Result))
            return null;
        return new BotMonitorRoutineReport
        {
            Id = Trim(r.Id, 128),
            ExpectedAt = r.ExpectedAt,
            RanAt = r.RanAt,
            Result = Trim(r.Result, 32),
        };
    }

    private static List<BotMonitorConnectorReport>? SanitizeConnectors(List<BotMonitorConnectorReport>? list)
    {
        if (list is null || list.Count == 0) return null;
        return list.Take(MaxConnectors).Select(c => new BotMonitorConnectorReport
        {
            Name = Trim(c.Name, 64) ?? "connector",
            State = NormalizeConnectorState(c.State),
        }).ToList();
    }

    private static string NormalizeConnectorState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "ok" => "ok",
        "needsauth" or "needs_auth" => "needsAuth",
        "error" => "error",
        _ => "ok",
    };

    private static BotMonitorActionCounts? SanitizeActions(BotMonitorActionCounts? a)
    {
        if (a is null) return null;
        return new BotMonitorActionCounts
        {
            Attempted = ClampCount(a.Attempted) ?? 0,
            Failed = ClampCount(a.Failed) ?? 0,
        };
    }

    private static BotMonitorInboxReport? SanitizeInbox(BotMonitorInboxReport? i)
    {
        if (i is null) return null;
        return new BotMonitorInboxReport
        {
            Unanswered = ClampCount(i.Unanswered) ?? 0,
            OldestMinutes = ClampOldest(i.OldestMinutes),
        };
    }

    private static BotMonitorApprovalReport? SanitizeApprovals(BotMonitorApprovalReport? a)
    {
        if (a is null) return null;
        return new BotMonitorApprovalReport
        {
            Pending = ClampCount(a.Pending) ?? 0,
            OldestMinutes = ClampOldest(a.OldestMinutes),
        };
    }

    private static BotMonitorQueueReport? SanitizeQueue(BotMonitorQueueReport? q)
    {
        if (q is null) return null;
        return new BotMonitorQueueReport
        {
            Open = ClampCount(q.Open) ?? 0,
            OldestMinutes = ClampOldest(q.OldestMinutes),
        };
    }

    private static int? ClampCount(int? v)
    {
        if (v is null) return null;
        return Math.Clamp(v.Value, 0, MaxCount);
    }

    private static int ClampOldest(int v) => Math.Clamp(v, 0, MaxOldestMinutes);

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
