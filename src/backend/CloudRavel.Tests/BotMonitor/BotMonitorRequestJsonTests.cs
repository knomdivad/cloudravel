using System.Text.Json;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.DTOs;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorRequestJsonTests
{
    [Theory]
    [InlineData("""{"platform":"other"}""", BotMonitorPlatform.Other)]
    [InlineData("""{"platform":"Other"}""", BotMonitorPlatform.Other)]
    [InlineData("""{"platform":"grokBot"}""", BotMonitorPlatform.GrokBot)]
    [InlineData("""{"platform":2}""", BotMonitorPlatform.Other)]
    [InlineData("""{"platform":0}""", BotMonitorPlatform.GrokBot)]
    public void CreateBotRequest_platform_accepts_string_or_number(string json, BotMonitorPlatform expected)
    {
        var dto = JsonSerializer.Deserialize<CreateBotMonitorBotRequest>(json, BotMonitorRequestJson.Options);
        Assert.NotNull(dto);
        Assert.Equal(expected, dto!.Platform);
    }

    [Theory]
    [InlineData("""{"status":"ok"}""", BotMonitorStatus.Ok)]
    [InlineData("""{"status":"OK"}""", BotMonitorStatus.Ok)]
    [InlineData("""{"status":0}""", BotMonitorStatus.Ok)]
    public void Heartbeat_status_accepts_string_or_number(string json, BotMonitorStatus expected)
    {
        var payload = JsonSerializer.Deserialize<BotMonitorHeartbeatPayload>(json, BotMonitorRequestJson.Options);
        Assert.NotNull(payload);
        Assert.Equal(expected, payload!.Status);
    }

    [Theory]
    [InlineData("""{"keepCheck":"pass"}""", BotMonitorKeepCheck.Pass)]
    [InlineData("""{"keepCheck":0}""", BotMonitorKeepCheck.Pass)]
    public void Heartbeat_keepCheck_accepts_string_or_number(string json, BotMonitorKeepCheck expected)
    {
        var payload = JsonSerializer.Deserialize<BotMonitorHeartbeatPayload>(json, BotMonitorRequestJson.Options);
        Assert.NotNull(payload);
        Assert.Equal(expected, payload!.KeepCheck);
    }
}
