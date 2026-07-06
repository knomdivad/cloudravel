using System.Security.Cryptography;
using System.Text;

namespace AzureInventoryMonitor.Infrastructure.MultiCloud;

/// <summary>
/// Minimal AWS Signature Version 4 signer — enough to call AWS REST/Query/JSON
/// APIs without pulling in the AWS SDK. Implements the canonical request /
/// string-to-sign / signing-key derivation from the SigV4 specification.
/// </summary>
public static class AwsSigV4
{
    /// <summary>
    /// Signs the request in place: adds Host, X-Amz-Date, optional X-Amz-Security-Token,
    /// X-Amz-Content-Sha256 (S3 requirement, harmless elsewhere), and Authorization headers.
    /// Call AFTER all other headers and the body are final.
    /// </summary>
    public static void Sign(
        HttpRequestMessage request,
        string service,
        string region,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        byte[] payload,
        DateTime? utcNowOverride = null)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("Request URI is required.");
        var utcNow = utcNowOverride ?? DateTime.UtcNow;
        var amzDate = utcNow.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = utcNow.ToString("yyyyMMdd");

        var payloadHash = ToHex(SHA256.HashData(payload));

        // Collect headers to sign (host + x-amz-* + content-type when present)
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.Host,
            ["x-amz-date"] = amzDate,
            ["x-amz-content-sha256"] = payloadHash
        };
        if (!string.IsNullOrEmpty(sessionToken))
            headers["x-amz-security-token"] = sessionToken;
        if (request.Content?.Headers.ContentType != null)
            headers["content-type"] = request.Content.Headers.ContentType.ToString();
        foreach (var h in request.Headers)
        {
            var name = h.Key.ToLowerInvariant();
            if (name.StartsWith("x-amz-", StringComparison.Ordinal) && !headers.ContainsKey(name))
                headers[name] = string.Join(",", h.Value).Trim();
        }

        var canonicalHeaders = new StringBuilder();
        foreach (var (name, value) in headers)
            canonicalHeaders.Append(name).Append(':').Append(value.Trim()).Append('\n');
        var signedHeaders = string.Join(";", headers.Keys);

        var canonicalQuery = CanonicalizeQuery(uri.Query);

        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            CanonicalizeUriPath(uri.AbsolutePath),
            canonicalQuery,
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = HmacSha256(
            HmacSha256(
                HmacSha256(
                    HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp),
                    region),
                service),
            "aws4_request");

        var signature = ToHex(HmacSha256(signingKey, stringToSign));

        // Apply headers to the actual request
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);
        if (!string.IsNullOrEmpty(sessionToken))
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", sessionToken);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static string CanonicalizeUriPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        // Path segments must be URI-encoded per RFC 3986 (already encoded paths pass through)
        return string.Join("/", path.Split('/').Select(seg => Uri.EscapeDataString(Uri.UnescapeDataString(seg))));
    }

    private static string CanonicalizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var idx = p.IndexOf('=');
                var key = idx >= 0 ? p[..idx] : p;
                var value = idx >= 0 ? p[(idx + 1)..] : "";
                return (Key: Uri.EscapeDataString(Uri.UnescapeDataString(key)),
                        Value: Uri.EscapeDataString(Uri.UnescapeDataString(value)));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(data));

    private static string ToHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
