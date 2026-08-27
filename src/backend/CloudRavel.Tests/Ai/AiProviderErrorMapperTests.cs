using System.Net;
using CloudRavel.Api.AI;
using CloudRavel.Api.Functions;

namespace CloudRavel.Tests.Ai;

/// <summary>
/// OpenAI returns HTTP 429 for billing/quota AND for burst rate limits.
/// These tests pin that we read the JSON body (which the SDK often omits from
/// Exception.Message) instead of labelling every 429 as "slow down and retry".
/// </summary>
public class AiProviderErrorMapperTests
{
    private const string Model = "gpt-4o-mini";

    [Fact]
    public void QuotaJsonInResponseBody_NotInExceptionMessage_IsQuotaExceeded()
    {
        // The failure mode on GCP: SDK Message is "HTTP 429", structured body is
        // only on GetRawResponse — previously this became AI_RATE_LIMITED.
        var body = """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","code":"insufficient_quota"}}""";

        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", body, Model, null);

        Assert.Equal(HttpStatusCode.PaymentRequired, mapped.Status);
        Assert.Equal("AI_QUOTA_EXCEEDED", mapped.Code);
        Assert.Contains("quota", mapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("billing", mapped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exceeded your current quota", mapped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BillingNotActiveType_IsQuotaExceeded()
    {
        var body = """{"error":{"message":"Your account is not active.","type":"billing_not_active","code":"billing_not_active"}}""";

        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", body, Model, null);

        Assert.Equal("AI_QUOTA_EXCEEDED", mapped.Code);
    }

    [Fact]
    public void ZeroTpmLimit_IsQuotaExceeded()
    {
        var msg = "Rate limit reached for gpt-4o-mini in organization org-x on tokens per min (TPM): Limit 0, Used 0, Requested 12000.";
        var body = "{\"error\":{\"message\":\"" + msg + "\",\"code\":\"rate_limit_exceeded\"}}";

        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", body, Model, null);

        Assert.Equal("AI_QUOTA_EXCEEDED", mapped.Code);
    }

    [Fact]
    public void RequestLargerThanLimit_ExplainsWaitingWillNotHelp()
    {
        var msg = "Rate limit reached for gpt-4o-mini in organization org-x on tokens per min (TPM): Limit 10000, Used 0, Requested 45000.";
        var body = "{\"error\":{\"message\":\"" + msg + "\",\"code\":\"rate_limit_exceeded\"}}";

        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", body, Model, null);

        Assert.Equal("AI_RATE_LIMITED", mapped.Code);
        Assert.Contains("Waiting will not help", mapped.Message, StringComparison.Ordinal);
        Assert.Contains("45000", mapped.Message);
        Assert.Contains("10000", mapped.Message);
    }

    [Fact]
    public void BurstRpmLimit_StaysRateLimited_AndIncludesProviderText()
    {
        var msg = "Rate limit reached for gpt-4o-mini in organization org-x on requests per min (RPM): Limit 500, Used 500, Requested 1.";
        var body = "{\"error\":{\"message\":\"" + msg + "\",\"code\":\"rate_limit_exceeded\"}}";

        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", body, Model, null);

        Assert.Equal("AI_RATE_LIMITED", mapped.Code);
        Assert.Contains(msg, mapped.Message);
    }

    [Fact]
    public void Bare429WithNoBody_StillRateLimited_ButDoesNotPretendItIsTraffic()
    {
        var mapped = AiProviderErrorMapper.Map(429, "HTTP 429", null, Model, null);

        Assert.Equal("AI_RATE_LIMITED", mapped.Code);
        Assert.Contains("first prompt", mapped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidApiKey_IsUnauthorized()
    {
        var body = """{"error":{"message":"Incorrect API key provided.","code":"invalid_api_key"}}""";

        var mapped = AiProviderErrorMapper.Map(401, "HTTP 401", body, Model, null);

        Assert.Equal(HttpStatusCode.Unauthorized, mapped.Status);
        Assert.Equal("AI_INVALID_API_KEY", mapped.Code);
    }

    [Fact]
    public void ModelNotFound_IncludesConfiguredModel()
    {
        var body = """{"error":{"message":"The model `gpt-5.5` does not exist","code":"model_not_found"}}""";

        var mapped = AiProviderErrorMapper.Map(404, "HTTP 404", body, "gpt-5.5", "https://api.openai.com/v1");

        Assert.Equal("AI_MODEL_NOT_FOUND", mapped.Code);
        Assert.Contains("gpt-5.5", mapped.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("https://example.com/v1", "https://example.com/v1")]
    public void NormalizeAiBaseUrl(string? input, string? expected) =>
        Assert.Equal(expected, AiFunctions.NormalizeAiBaseUrl(input));
}
