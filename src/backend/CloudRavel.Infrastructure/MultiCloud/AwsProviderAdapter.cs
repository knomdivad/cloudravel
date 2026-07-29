using System.Text;
using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// AWS adapter using SigV4-signed REST calls (no AWS SDK dependency).
///
/// Credentials come from the secret store (CloudAccount.CredentialSecretName) as JSON:
///   { "accessKeyId": "...", "secretAccessKey": "...", "sessionToken": "...?", "defaultRegion": "us-east-1" }
///
/// Inventory: Resource Groups Tagging API GetResources — one call per region
/// enumerates every taggable resource's ARN + tags across all services.
///
/// Supported remediation action types:
///   aws.ec2.stop_instance       — EC2 StopInstances (params: instanceId, region)
///   aws.s3.block_public_access  — S3 PutPublicAccessBlock (params: bucket, region)
/// </summary>
public sealed partial class AwsProviderAdapter : ICloudProviderAdapter
{
    private static readonly HttpClient Http = new();

    private readonly ICloudAccountRepository _accountRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<AwsProviderAdapter> _logger;

    public AwsProviderAdapter(
        ICloudAccountRepository accountRepo,
        ILogger<AwsProviderAdapter> logger,
        ISecretStore? secretStore = null)
    {
        _accountRepo = accountRepo;
        _secretStore = secretStore;
        _logger = logger;
    }

    public CloudProvider Provider => CloudProvider.Aws;

    public async Task<(bool Healthy, string? Error)> TestConnectivityAsync(CloudAccount account)
    {
        try
        {
            var creds = await ResolveCredentialsAsync(account);
            var body = Encoding.UTF8.GetBytes("Action=GetCallerIdentity&Version=2011-06-15");
            var request = new HttpRequestMessage(HttpMethod.Post, "https://sts.amazonaws.com/")
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            AwsSigV4.Sign(request, "sts", "us-east-1", creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);

            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, $"STS GetCallerIdentity returned {(int)response.StatusCode}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<InventoryResource>> CollectInventoryAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        var creds = await ResolveCredentialsAsync(account);
        var regions = account.Regions is { Count: > 0 } ? account.Regions : new List<string> { creds.DefaultRegion };
        var resources = new List<InventoryResource>();

        foreach (var region in regions)
        {
            string? paginationToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["ResourcesPerPage"] = 100,
                    ["PaginationToken"] = string.IsNullOrEmpty(paginationToken) ? null : paginationToken
                }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                var body = Encoding.UTF8.GetBytes(payload);
                var request = new HttpRequestMessage(HttpMethod.Post, $"https://tagging.{region}.amazonaws.com/")
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");
                request.Headers.TryAddWithoutValidation("X-Amz-Target", "ResourceGroupsTaggingAPI_20170126.GetResources");
                AwsSigV4.Sign(request, "tagging", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);

                var response = await Http.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Tagging API ({region}) returned {(int)response.StatusCode}: {responseBody}");

                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("ResourceTagMappingList", out var list))
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var arn = item.TryGetProperty("ResourceARN", out var arnProp) ? arnProp.GetString() : null;
                        if (string.IsNullOrEmpty(arn)) continue;

                        Dictionary<string, string>? tags = null;
                        if (item.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                        {
                            tags = new Dictionary<string, string>();
                            foreach (var t in tagsProp.EnumerateArray())
                            {
                                var key = t.TryGetProperty("Key", out var k) ? k.GetString() : null;
                                var value = t.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(key)) tags[key] = value;
                            }
                        }

                        resources.Add(NormalizeArn(account, arn, region, tags));
                    }
                }

                paginationToken = doc.RootElement.TryGetProperty("PaginationToken", out var pt) ? pt.GetString() : null;
            } while (!string.IsNullOrEmpty(paginationToken));
        }

        _logger.LogInformation("Collected {Count} AWS resources for account {AccountId} ({Regions})",
            resources.Count, account.ExternalId, string.Join(",", regions));
        return resources;
    }

    public async Task<RemediationExecutionResult> ExecuteRemediationAsync(
        Guid tenantId, RemediationPlaybook playbook, RemediationAction action, CancellationToken cancellationToken = default)
    {
        var parameters = ParseParameters(action.ParametersJson);
        var account = await ResolveAccountAsync(tenantId, parameters);
        if (account == null)
            return RemediationExecutionResult.Fail("No connected AWS account linked to this tenant.");

        var creds = await ResolveCredentialsAsync(account);
        var region = parameters.TryGetValue("region", out var r) ? r.GetString() ?? creds.DefaultRegion : creds.DefaultRegion;

        try
        {
            return playbook.ActionType switch
            {
                "aws.ec2.stop_instance" => await StopEc2InstanceAsync(creds, region, parameters, action, cancellationToken),
                "aws.s3.block_public_access" => await BlockS3PublicAccessAsync(creds, region, parameters, cancellationToken),
                _ => RemediationExecutionResult.Fail($"Action type '{playbook.ActionType}' is not allow-listed for AWS.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS remediation {ActionType} failed", playbook.ActionType);
            return RemediationExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<RemediationExecutionResult> StopEc2InstanceAsync(
        AwsCredentials creds, string region, Dictionary<string, JsonElement> parameters,
        RemediationAction action, CancellationToken ct)
    {
        var instanceId = parameters.TryGetValue("instanceId", out var i)
            ? i.GetString()
            : ExtractArnResourceName(action.ResourceId);
        if (string.IsNullOrEmpty(instanceId))
            return RemediationExecutionResult.Fail("stop_instance requires an 'instanceId' parameter or instance ARN.");

        var body = Encoding.UTF8.GetBytes(
            $"Action=StopInstances&InstanceId.1={Uri.EscapeDataString(instanceId)}&Version=2016-11-15");
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://ec2.{region}.amazonaws.com/")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        AwsSigV4.Sign(request, "ec2", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);

        var response = await Http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return RemediationExecutionResult.Fail($"EC2 StopInstances returned {(int)response.StatusCode}: {Truncate(responseBody, 500)}");

        return RemediationExecutionResult.Ok(JsonSerializer.Serialize(new { instanceId, region, statusCode = (int)response.StatusCode }));
    }

    private async Task<RemediationExecutionResult> BlockS3PublicAccessAsync(
        AwsCredentials creds, string region, Dictionary<string, JsonElement> parameters, CancellationToken ct)
    {
        var bucket = parameters.TryGetValue("bucket", out var b) ? b.GetString() : null;
        if (string.IsNullOrEmpty(bucket))
            return RemediationExecutionResult.Fail("block_public_access requires a 'bucket' parameter.");

        const string xml = """
            <PublicAccessBlockConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <BlockPublicAcls>true</BlockPublicAcls>
              <IgnorePublicAcls>true</IgnorePublicAcls>
              <BlockPublicPolicy>true</BlockPublicPolicy>
              <RestrictPublicBuckets>true</RestrictPublicBuckets>
            </PublicAccessBlockConfiguration>
            """;
        var body = Encoding.UTF8.GetBytes(xml);
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"https://{bucket}.s3.{region}.amazonaws.com/?publicAccessBlock=")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        AwsSigV4.Sign(request, "s3", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);

        var response = await Http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return RemediationExecutionResult.Fail($"S3 PutPublicAccessBlock returned {(int)response.StatusCode}: {Truncate(responseBody, 500)}");

        return RemediationExecutionResult.Ok(JsonSerializer.Serialize(new { bucket, statusCode = (int)response.StatusCode }));
    }

    // ---- helpers ----

    private async Task<CloudAccount?> ResolveAccountAsync(Guid tenantId, Dictionary<string, JsonElement> parameters)
    {
        var accounts = await _accountRepo.GetByTenantAsync(tenantId);
        var awsAccounts = accounts.Where(a => a.Provider == CloudProvider.Aws && a.Status != CloudAccountStatus.Disconnected).ToList();

        if (parameters.TryGetValue("accountId", out var acc) && Guid.TryParse(acc.GetString(), out var accountId))
            return awsAccounts.FirstOrDefault(a => a.AccountId == accountId);
        return awsAccounts.FirstOrDefault();
    }

    private async Task<AwsCredentials> ResolveCredentialsAsync(CloudAccount account)
    {
        if (_secretStore == null)
            throw new InvalidOperationException("Secret store is not configured; cannot resolve AWS credentials.");
        var secretName = !string.IsNullOrWhiteSpace(account.CredentialSecretName)
            ? account.CredentialSecretName
            : $"cloudaccount-{account.AccountId}";

        var secretValue = await _secretStore.GetSecretAsync(secretName)
            ?? throw new InvalidOperationException(
                $"No credential secret found for AWS account '{account.ExternalId}' (looked up '{secretName}'). " +
                "Re-paste keys via Clouds → Credentials if OpenBao was restarted without persistence.");
        var creds = JsonSerializer.Deserialize<AwsCredentials>(secretValue,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (creds == null || string.IsNullOrEmpty(creds.AccessKeyId) || string.IsNullOrEmpty(creds.SecretAccessKey))
            throw new InvalidOperationException($"AWS credential secret for account {account.ExternalId} is malformed.");
        return creds;
    }

    /// <summary>Normalizes an ARN into the shared inventory model.</summary>
    private static InventoryResource NormalizeArn(CloudAccount account, string arn, string region, Dictionary<string, string>? tags)
    {
        // arn:partition:service:region:account-id:resource-type/resource-id
        var parts = arn.Split(':', 6);
        var service = parts.Length > 2 ? parts[2] : "unknown";
        var arnRegion = parts.Length > 3 && !string.IsNullOrEmpty(parts[3]) ? parts[3] : region;
        var resourcePart = parts.Length > 5 ? parts[5] : (parts.Length > 4 ? parts[4] : arn);

        string resourceType = service, resourceName = resourcePart;
        var slashIdx = resourcePart.IndexOf('/');
        var colonIdx = resourcePart.IndexOf(':');
        if (slashIdx > 0)
        {
            resourceType = $"{service}/{resourcePart[..slashIdx]}";
            resourceName = resourcePart[(slashIdx + 1)..];
        }
        else if (colonIdx > 0)
        {
            resourceType = $"{service}/{resourcePart[..colonIdx]}";
            resourceName = resourcePart[(colonIdx + 1)..];
        }

        return new InventoryResource
        {
            TenantId = account.TenantId,
            Provider = "aws",
            ResourceId = arn,
            SubscriptionId = account.ExternalId,
            ResourceGroup = service,
            ResourceType = resourceType,
            ResourceName = resourceName,
            Location = string.IsNullOrEmpty(arnRegion) ? "global" : arnRegion,
            Tags = tags is { Count: > 0 } ? tags : null
        };
    }

    private static string? ExtractArnResourceName(string? arn)
    {
        if (string.IsNullOrEmpty(arn)) return null;
        var lastSlash = arn.LastIndexOf('/');
        return lastSlash >= 0 ? arn[(lastSlash + 1)..] : arn;
    }

    private static Dictionary<string, JsonElement> ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson) ?? new(); }
        catch { return new(); }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class AwsCredentials
    {
        public string AccessKeyId { get; set; } = string.Empty;
        public string SecretAccessKey { get; set; } = string.Empty;
        public string? SessionToken { get; set; }
        public string DefaultRegion { get; set; } = "us-east-1";
    }
}
