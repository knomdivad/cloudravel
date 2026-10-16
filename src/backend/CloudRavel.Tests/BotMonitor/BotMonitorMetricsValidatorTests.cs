using CloudRavel.Core.BotMonitor;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorMetricsValidatorTests
{
    [Fact]
    public void Oversized_connector_list_throws()
    {
        var input = new BotMonitorReportedMetrics
        {
            Connectors = Enumerable.Range(0, 20).Select(i => new BotMonitorConnectorReport { Name = $"c{i}", State = "ok" }).ToList(),
        };
        var sanitized = BotMonitorMetricsValidator.Sanitize(input);
        Assert.NotNull(sanitized);
        Assert.Equal(10, sanitized!.Connectors!.Count);
    }

    [Fact]
    public void Serialized_metrics_over_cap_throws()
    {
        var input = new BotMonitorReportedMetrics
        {
            Connectors = Enumerable.Range(0, 10)
                .Select(i => new BotMonitorConnectorReport { Name = new string('x', 400), State = "ok" })
                .ToList(),
        };
        Assert.Throws<InvalidOperationException>(() => BotMonitorMetricsValidator.Sanitize(input));
    }

    [Fact]
    public void Negative_counts_clamped_to_zero()
    {
        var input = new BotMonitorReportedMetrics
        {
            Inbox = new BotMonitorInboxReport { Unanswered = -3, OldestMinutes = -1 },
            Escalations = -5,
        };
        var sanitized = BotMonitorMetricsValidator.Sanitize(input)!;
        Assert.Equal(0, sanitized.Inbox!.Unanswered);
        Assert.Equal(0, sanitized.Inbox.OldestMinutes);
        Assert.Equal(0, sanitized.Escalations);
    }
}
