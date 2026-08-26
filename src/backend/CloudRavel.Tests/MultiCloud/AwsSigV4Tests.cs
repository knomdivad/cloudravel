using CloudRavel.Infrastructure.MultiCloud;

namespace CloudRavel.Tests.MultiCloud;

/// <summary>
/// CloudRavel signs AWS requests by hand rather than taking the AWS SDK, so a
/// regression here breaks every AWS call at once and looks like a credential
/// problem rather than a signing bug.
///
/// The expected signatures are not hand-derived. They were produced by botocore
/// (AWS's own reference implementation) at a frozen timestamp, signing the same
/// header set this implementation signs. Matching them proves agreement with the
/// specification rather than with our reading of it.
/// </summary>
public class AwsSigV4Tests
{
    // From the AWS SigV4 test suite; not a live credential.
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";

    private static readonly DateTime FrozenUtc = new(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);

    private static string AuthorizationHeaderFor(HttpRequestMessage request)
        => request.Headers.GetValues("Authorization").Single();

    [Fact]
    public void Sign_GetQueryRequest_MatchesReferenceSignature()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            "https://ec2.us-east-1.amazonaws.com/?Action=DescribeInstances&Version=2016-11-15");

        AwsSigV4.Sign(request, "ec2", "us-east-1", AccessKeyId, SecretAccessKey,
            sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/ec2/aws4_request, " +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=9a842ff7153eb2256db24d8143645f9b4e5648d9f5925157e9aa0f1f67cb7fa6",
            AuthorizationHeaderFor(request));
    }

    [Fact]
    public void Sign_PostWithBodyContentTypeAndSessionToken_MatchesReferenceSignature()
    {
        var payload = "{\"limit\":10}"u8.ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://config.eu-west-1.amazonaws.com/")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");

        AwsSigV4.Sign(request, "config", "eu-west-1", AccessKeyId, SecretAccessKey,
            sessionToken: "FQoGZXIvYXdzEExampleSessionToken", payload: payload, utcNowOverride: FrozenUtc);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/eu-west-1/config/aws4_request, " +
            "SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token, " +
            "Signature=ebc3a15e00b892ee46d7faab4fe69c21a98987fab9c0687e7df37b63b09e7252",
            AuthorizationHeaderFor(request));
    }

    [Fact]
    public void Sign_QueryParameterOrderDoesNotChangeTheSignature()
    {
        // SigV4 canonicalization sorts query parameters, so a caller building the
        // URL in a different order must still produce a signature AWS accepts.
        var ordered = new HttpRequestMessage(HttpMethod.Get,
            "https://ec2.us-east-1.amazonaws.com/?Action=DescribeInstances&Version=2016-11-15");
        var shuffled = new HttpRequestMessage(HttpMethod.Get,
            "https://ec2.us-east-1.amazonaws.com/?Version=2016-11-15&Action=DescribeInstances");

        foreach (var request in new[] { ordered, shuffled })
        {
            AwsSigV4.Sign(request, "ec2", "us-east-1", AccessKeyId, SecretAccessKey,
                sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc);
        }

        Assert.Equal(AuthorizationHeaderFor(ordered), AuthorizationHeaderFor(shuffled));
    }

    [Fact]
    public void Sign_OmitsSecurityTokenHeaderWhenNoSessionToken()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");

        AwsSigV4.Sign(request, "ec2", "us-east-1", AccessKeyId, SecretAccessKey,
            sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc);

        Assert.False(request.Headers.Contains("X-Amz-Security-Token"));
        Assert.DoesNotContain("x-amz-security-token", AuthorizationHeaderFor(request));
    }

    [Fact]
    public void Sign_SetsPayloadHashHeaderForTheActualBody()
    {
        var payload = "hello"u8.ToArray();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://s3.us-east-1.amazonaws.com/bucket/key")
        {
            Content = new ByteArrayContent(payload)
        };

        AwsSigV4.Sign(request, "s3", "us-east-1", AccessKeyId, SecretAccessKey,
            sessionToken: null, payload: payload, utcNowOverride: FrozenUtc);

        // SHA-256 of "hello".
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            request.Headers.GetValues("X-Amz-Content-Sha256").Single());
    }

    [Fact]
    public void Sign_UsesTheSignedTimestampForBothDateHeaderAndCredentialScope()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");

        AwsSigV4.Sign(request, "ec2", "us-east-1", AccessKeyId, SecretAccessKey,
            sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc);

        // A scope date that drifts from X-Amz-Date is rejected by AWS as skew.
        Assert.Equal("20150830T123600Z", request.Headers.GetValues("X-Amz-Date").Single());
        Assert.Contains("/20150830/", AuthorizationHeaderFor(request));
    }

    [Fact]
    public void Sign_DifferentSecretsProduceDifferentSignatures()
    {
        static string SignWith(string secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://ec2.us-east-1.amazonaws.com/");
            AwsSigV4.Sign(request, "ec2", "us-east-1", AccessKeyId, secret,
                sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc);
            return request.Headers.GetValues("Authorization").Single();
        }

        Assert.NotEqual(SignWith(SecretAccessKey), SignWith(SecretAccessKey + "x"));
    }

    [Fact]
    public void Sign_MissingRequestUriIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AwsSigV4.Sign(
            new HttpRequestMessage(), "ec2", "us-east-1", AccessKeyId, SecretAccessKey,
            sessionToken: null, payload: Array.Empty<byte>(), utcNowOverride: FrozenUtc));
    }
}
