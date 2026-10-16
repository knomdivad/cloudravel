using System.Net;
using System.Security.Claims;
using System.Text;
using CloudRavel.Api.Functions;
using CloudRavel.Core.BotMonitor;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure.BotMonitor;
using CloudRavel.Tests.Assessment;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudRavel.Tests.BotMonitor;

public sealed class BotMonitorHeartbeatHttpTests
{
    [Fact]
    public async Task Missing_signature_returns_http_401()
    {
        var status = await InvokeHeartbeatAsync(null, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Malformed_signature_returns_http_401()
    {
        var status = await InvokeHeartbeatAsync("not-valid-hex", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    private static async Task<HttpStatusCode> InvokeHeartbeatAsync(string? signature, string jsonBody)
    {
        var cred = BotMonitorTestConfig.Credentials();
        var mint = cred.MintCredentials();
        var repo = Substitute.For<IBotMonitorRepository>();
        repo.GetBotByTokenHashAsync(Arg.Any<string>()).Returns(new BotMonitorBot
        {
            BotId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            TokenHash = mint.TokenHash,
            HmacSecretEnc = mint.HmacSecretEnc,
            CheckinIntervalMinutes = 60,
        });

        var fixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime;
        var hb = new BotMonitorHeartbeatService(repo, cred, new ConfigurationBuilder().Build(), () => fixedNow);
        var fn = new BotMonitorFunctions(
            repo,
            Substitute.For<IUserRepository>(),
            Substitute.For<IOrganizationRepository>(),
            cred,
            hb,
            new ConfigurationBuilder().Build(),
            NullLogger<BotMonitorFunctions>.Instance);

        var ctx = new TestFunctionContext();
        var req = new SignedHeartbeatRequest(ctx, mint.Token, signature, jsonBody);
        ctx.SetRequest(req);

        var response = await fn.Heartbeat(req, ctx);
        return response.StatusCode;
    }

    private sealed class SignedHeartbeatRequest : HttpRequestData
    {
        public SignedHeartbeatRequest(TestFunctionContext ctx, string token, string? signature, string json)
            : base(ctx)
        {
            Method = "POST";
            Url = new Uri("http://localhost:7071/api/bot-monitor/heartbeat");
            Headers = new HttpHeadersCollection();
            Headers.Add("Authorization", $"Bearer {token}");
            if (signature != null)
            {
                Headers.Add("X-CR-Timestamp", "1700000000");
                Headers.Add("X-CR-Signature", signature);
            }

            Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        public override Stream Body { get; }
        public override HttpHeadersCollection Headers { get; }
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; }
        public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
        public override string Method { get; }
        public override HttpResponseData CreateResponse() => new FakeHttpResponseData(this);
    }
}
