using System.ClientModel;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudRavel.Api.AI;

/// <summary>
/// Maps OpenAI-compatible HTTP errors to admin-facing codes.
///
/// OpenAI (and Azure OpenAI) return HTTP 429 for both "slow down" rate limits
/// and billing/quota exhaustion. The SDK's <see cref="ClientResultException.Message"/>
/// is often just "HTTP 429" — the structured body lives on
/// <see cref="ClientResultException.GetRawResponse"/>. Treating every 429 as a
/// rate limit made first-prompt failures look like traffic when the key had
/// no credits or a 0 TPM project limit.
/// </summary>
internal static class AiProviderErrorMapper
{
    private static readonly Regex LimitRequested = new(
        @"Limit\s+(?<limit>\d+).{0,80}Requested\s+(?<requested>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static (HttpStatusCode Status, string Code, string Message) Map(
        ClientResultException ex, string model, string? baseUrl)
    {
        return Map(ex.Status, ex.Message, TryGetResponseBody(ex), model, baseUrl);
    }

    internal static string? TryGetResponseBody(ClientResultException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Content?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure mapping over status + text so tests do not have to fabricate SDK responses.</summary>
    internal static (HttpStatusCode Status, string Code, string Message) Map(
        int status, string? exceptionMessage, string? responseBody, string model, string? baseUrl)
    {
        var parsed = TryParseError(responseBody) ?? TryParseError(exceptionMessage);
        var providerMsg = FirstNonEmpty(parsed?.Message, ExtractJsonSnippet(responseBody), exceptionMessage) ?? string.Empty;
        var code = parsed?.Code;
        var type = parsed?.Type;
        var haystack = $"{code} {type} {providerMsg}";

        if (IsQuotaExhausted(code, type, haystack))
        {
            return (HttpStatusCode.PaymentRequired, "AI_QUOTA_EXCEEDED",
                WithProvider(
                    "The OpenAI API key has no remaining quota (billing). Add credits or raise the project usage limit at platform.openai.com, then retry.",
                    providerMsg));
        }

        if (EqualsOrdinal(code, "invalid_api_key")
            || ContainsAny(haystack, "Incorrect API key", "invalid_api_key", "invalid api key"))
        {
            return (HttpStatusCode.Unauthorized, "AI_INVALID_API_KEY",
                WithProvider("The configured OpenAI API key was rejected. Update it under Admin → System Settings.", providerMsg));
        }

        if (EqualsOrdinal(code, "model_not_found")
            || (ContainsAny(haystack, "does not exist") && ContainsAny(haystack, "model")))
        {
            return (HttpStatusCode.BadRequest, "AI_MODEL_NOT_FOUND",
                WithProvider(
                    $"Model '{model}' is not available for this key/endpoint. Choose a model your account can use (e.g. gpt-4o-mini, gpt-4o).",
                    providerMsg));
        }

        if (status == 401 || status == 403)
            return (HttpStatusCode.Unauthorized, "AI_PROVIDER_AUTH",
                WithProvider("The AI provider rejected authentication. Check the API key under Admin → System Settings.", providerMsg));

        if (status == 404)
            return (HttpStatusCode.BadRequest, "AI_PROVIDER_NOT_FOUND",
                WithProvider(
                    $"The AI endpoint or model was not found (model '{model}', base '{baseUrl ?? "default"}'). Check Base URL and Model.",
                    providerMsg));

        if (status == 429)
        {
            var limitHint = DescribeImpossibleLimit(providerMsg);
            var friendly = limitHint
                ?? "The AI provider rate-limited this request. If this happens on the first prompt, it is usually a billing/TPM limit of 0 — not traffic. Check usage limits at platform.openai.com.";
            return (HttpStatusCode.TooManyRequests, "AI_RATE_LIMITED", WithProvider(friendly, providerMsg));
        }

        return (HttpStatusCode.BadGateway, "AI_PROVIDER_ERROR",
            string.IsNullOrWhiteSpace(providerMsg)
                ? $"The AI provider returned HTTP {status}."
                : $"The AI provider returned an error: {Truncate(providerMsg, 400)}");
    }

    private static bool IsQuotaExhausted(string? code, string? type, string haystack)
    {
        if (EqualsOrdinal(code, "insufficient_quota", "billing_not_active", "billing_hard_limit_reached")
            || EqualsOrdinal(type, "insufficient_quota", "insufficient_quota_error", "billing_not_active"))
            return true;

        // TPM/RPM of 0 is a billing/project cap, not a burst of traffic.
        if (ContainsAny(haystack, "Limit 0,", "Limit 0 ", "limit of 0 ", "tokens per min (TPM): Limit 0",
                "requests per min (RPM): Limit 0"))
            return true;

        return ContainsAny(haystack,
            "exceeded your current quota",
            "check your plan and billing",
            "billing details",
            "insufficient_quota",
            "billing_not_active",
            "you have hit your organization rate limit of 0");
    }

    private static string? DescribeImpossibleLimit(string providerMsg)
    {
        var m = LimitRequested.Match(providerMsg);
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups["limit"].Value, out var limit)
            || !long.TryParse(m.Groups["requested"].Value, out var requested)
            || requested <= limit)
            return null;

        return $"This single request needs {requested} tokens but the model/tier limit is {limit}. Waiting will not help — pick a model with a higher TPM limit, or a key on a paid tier.";
    }

    private static ParsedError? TryParseError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw[jsonStart..]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out var errEl))
                return null;

            if (errEl.ValueKind == JsonValueKind.String)
                return new ParsedError(errEl.GetString(), null, null);

            if (errEl.ValueKind != JsonValueKind.Object)
                return null;

            var msg = errEl.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = errEl.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var type = errEl.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new ParsedError(msg, code, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJsonSnippet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var jsonStart = raw.IndexOf('{');
        return jsonStart >= 0 ? raw[jsonStart..] : raw;
    }

    private static string WithProvider(string friendly, string? providerMsg)
    {
        var detail = Truncate((providerMsg ?? string.Empty).Trim(), 400);
        if (string.IsNullOrWhiteSpace(detail)) return friendly;
        if (friendly.Contains(detail, StringComparison.OrdinalIgnoreCase)) return friendly;
        return $"{friendly} Provider: {detail}";
    }

    private static bool EqualsOrdinal(string? value, params string[] expected) =>
        expected.Any(e => string.Equals(value, e, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record ParsedError(string? Message, string? Code, string? Type);
}
