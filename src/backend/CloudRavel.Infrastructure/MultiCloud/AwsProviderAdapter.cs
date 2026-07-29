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
/// Inventory (per scanned region):
///   1. Resource Groups Tagging API GetResources — taggable resources across services
///   2. EC2 Describe* — VPCs, subnets, security groups, IGWs, route tables, NACLs,
///      NAT gateways, instances, volumes (covers default/untagged networking that
///      Tagging API often omits)
///   Resources are merged by ARN (Tagging tags win when both sources return the same id).
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
        var regions = account.Regions is { Count: > 0 } ? account.Regions : new List<string> { creds.DefaultRegion ?? "us-east-1" };
        // Dedupe by ARN — EC2 Describe fills gaps Tagging API skips (untagged VPC/SG/etc.)
        var byArn = new Dictionary<string, InventoryResource>(StringComparer.OrdinalIgnoreCase);

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CollectTaggedResourcesAsync(account, creds, region, byArn, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tagging inventory failed for account {Account} region {Region}", account.ExternalId, region);
            }

            try
            {
                await CollectEc2NetworkAndComputeAsync(account, creds, region, byArn, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EC2 inventory failed for account {Account} region {Region}", account.ExternalId, region);
            }
        }

        var resources = byArn.Values.ToList();
        _logger.LogInformation("Collected {Count} AWS resources for account {AccountId} ({Regions})",
            resources.Count, account.ExternalId, string.Join(",", regions));
        return resources;
    }

    private async Task CollectTaggedResourcesAsync(
        CloudAccount account, AwsCredentials creds, string region,
        Dictionary<string, InventoryResource> byArn, CancellationToken cancellationToken)
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

                    // Tagging API is the richer source for tags — always overwrite
                    byArn[arn] = NormalizeArn(account, arn, region, tags);
                }
            }

            paginationToken = doc.RootElement.TryGetProperty("PaginationToken", out var pt) ? pt.GetString() : null;
        } while (!string.IsNullOrEmpty(paginationToken));
    }

    /// <summary>
    /// EC2 networking + compute that Tagging API often misses (default VPC, untagged SGs, etc.).
    /// </summary>
    private async Task CollectEc2NetworkAndComputeAsync(
        CloudAccount account, AwsCredentials creds, string region,
        Dictionary<string, InventoryResource> byArn, CancellationToken ct)
    {
        var accountId = account.ExternalId;
        // Collect raw XML so we can harvest VPC ids referenced by SGs/instances even if
        // DescribeVpcs is denied or returns an unexpected shape.
        var xmlBlobs = new List<string>();

        // action → (xml id tag, arn resource type prefix, inventory type)
        var describes = new (string Action, string IdTag, string ArnType, string ResourceType)[]
        {
            ("DescribeVpcs", "vpcId", "vpc", "ec2/vpc"),
            ("DescribeSubnets", "subnetId", "subnet", "ec2/subnet"),
            ("DescribeSecurityGroups", "groupId", "security-group", "ec2/security-group"),
            ("DescribeInternetGateways", "internetGatewayId", "internet-gateway", "ec2/internet-gateway"),
            ("DescribeRouteTables", "routeTableId", "route-table", "ec2/route-table"),
            ("DescribeNetworkAcls", "networkAclId", "network-acl", "ec2/network-acl"),
            ("DescribeNatGateways", "natGatewayId", "natgateway", "ec2/natgateway"),
            ("DescribeVolumes", "volumeId", "volume", "ec2/volume"),
        };

        foreach (var (action, idTag, arnType, resourceType) in describes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var xml = await Ec2QueryAsync(creds, region, action, ct);
                xmlBlobs.Add(xml);
                var ids = ExtractXmlTags(xml, idTag)
                    .Concat(ExtractEc2IdsByPrefix(xml, PrefixForArnType(arnType)))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var added = 0;
                foreach (var id in ids)
                {
                    if (!AddEc2Resource(byArn, account, region, accountId, arnType, resourceType, id!, xml))
                        continue;
                    added++;
                }

                if (string.Equals(action, "DescribeVpcs", StringComparison.OrdinalIgnoreCase))
                    _logger.LogInformation("EC2 DescribeVpcs {Region}: {Count} VPC(s) merged for account {Account}",
                        region, added, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EC2 {Action} skipped in {Region} for account {Account}",
                    action, region, accountId);
            }
        }

        // Instances (reservation → instances → instanceId)
        try
        {
            var xml = await Ec2QueryAsync(creds, region, "DescribeInstances", ct);
            xmlBlobs.Add(xml);
            foreach (var id in ExtractXmlTags(xml, "instanceId")
                         .Concat(ExtractEc2IdsByPrefix(xml, "i-"))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith("r-", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddEc2Resource(byArn, account, region, accountId, "instance", "ec2/instance", id, xml);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EC2 DescribeInstances skipped in {Region} for account {Account}", region, accountId);
        }

        // Fallback: any vpc-xxx referenced by SGs/instances/subnets but missing as its own row
        // (common when DescribeVpcs is missing from IAM but DescribeSecurityGroups works).
        var harvestedVpcs = 0;
        foreach (var xml in xmlBlobs)
        {
            foreach (var vpcId in ExtractEc2IdsByPrefix(xml, "vpc-")
                         .Concat(ExtractXmlTags(xml, "vpcId"))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (AddEc2Resource(byArn, account, region, accountId, "vpc", "ec2/vpc", vpcId, xml))
                    harvestedVpcs++;
            }
        }
        if (harvestedVpcs > 0)
            _logger.LogInformation("Harvested {Count} VPC id(s) from related EC2 responses in {Region}", harvestedVpcs, region);
    }

    private static bool AddEc2Resource(
        Dictionary<string, InventoryResource> byArn,
        CloudAccount account,
        string region,
        string accountId,
        string arnType,
        string resourceType,
        string id,
        string xmlForTags)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // Normalize id (strip accidental whitespace / XML noise)
        id = id.Trim();
        if (id.Length < 3) return false;

        var arn = $"arn:aws:ec2:{region}:{accountId}:{arnType}/{id}";
        if (byArn.ContainsKey(arn)) return false;

        var nameFromTags = ExtractNameFromEc2TagSetNearId(xmlForTags, id);
        byArn[arn] = new InventoryResource
        {
            TenantId = account.TenantId,
            Provider = "aws",
            ResourceId = arn,
            SubscriptionId = accountId,
            ResourceGroup = "ec2",
            ResourceType = resourceType,
            ResourceName = nameFromTags ?? id,
            Location = region
        };
        return true;
    }

    private static string PrefixForArnType(string arnType) => arnType switch
    {
        "vpc" => "vpc-",
        "subnet" => "subnet-",
        "security-group" => "sg-",
        "internet-gateway" => "igw-",
        "route-table" => "rtb-",
        "network-acl" => "acl-",
        "natgateway" => "nat-",
        "volume" => "vol-",
        "instance" => "i-",
        _ => ""
    };

    /// <summary>Find EC2 resource ids like vpc-0abc… / sg-0abc… in XML text.</summary>
    private static IEnumerable<string> ExtractEc2IdsByPrefix(string xml, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(xml))
            yield break;
        // vpc- / sg- / subnet- ids: prefix + 8–32 hex (classic) or 17+ hex (longer form)
        var pattern = prefix.Replace("-", "\\-") + "[0-9a-fA-F]{8,32}";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, pattern))
            yield return m.Value;
    }

    private async Task<string> Ec2QueryAsync(AwsCredentials creds, string region, string action, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"Action={Uri.EscapeDataString(action)}&Version=2016-11-15");
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://ec2.{region}.amazonaws.com/")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        AwsSigV4.Sign(request, "ec2", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);
        var response = await Http.SendAsync(request, ct);
        var xml = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EC2 {action} {(int)response.StatusCode}: {Truncate(xml, 300)}");
        return xml;
    }

    /// <summary>
    /// Best-effort Name tag near a resource id in EC2 XML (tagSet is nested under the item).
    /// </summary>
    private static string? ExtractNameFromEc2TagSetNearId(string xml, string resourceId)
    {
        var idIdx = xml.IndexOf(resourceId, StringComparison.Ordinal);
        if (idIdx < 0) return null;
        // Search a window after the id for tagSet with Name
        var window = xml.Substring(idIdx, Math.Min(4000, xml.Length - idIdx));
        var nameKey = window.IndexOf("<key>Name</key>", StringComparison.OrdinalIgnoreCase);
        if (nameKey < 0) nameKey = window.IndexOf("<key>name</key>", StringComparison.OrdinalIgnoreCase);
        if (nameKey < 0) return null;
        var after = window[(nameKey + 10)..];
        var open = after.IndexOf("<value>", StringComparison.OrdinalIgnoreCase);
        var close = after.IndexOf("</value>", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || close <= open) return null;
        var value = after[(open + 7)..close].Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // ExtractXmlTags lives on the Governance partial (shared by inventory + governance).

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

        string resourceType = service, leafFromArn = resourcePart;
        var slashIdx = resourcePart.IndexOf('/');
        var colonIdx = resourcePart.IndexOf(':');
        if (slashIdx > 0)
        {
            resourceType = $"{service}/{resourcePart[..slashIdx]}";
            // Prefer the last path segment as the human-facing name (e.g. loadbalancer/app/my-alb/abc → my-alb
            // is imperfect; Name tag below wins when present).
            leafFromArn = resourcePart[(resourcePart.LastIndexOf('/') + 1)..];
            // For simple "type/name" keep name; for multi-segment ALB ARNs keep middle name when useful
            var segments = resourcePart.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] is "app" or "net")
                leafFromArn = segments.Length >= 2 ? segments[1] : leafFromArn;
            else if (segments.Length == 2)
                leafFromArn = segments[1];
        }
        else if (colonIdx > 0)
        {
            resourceType = $"{service}/{resourcePart[..colonIdx]}";
            leafFromArn = resourcePart[(colonIdx + 1)..];
        }

        // Prefer AWS Name tag over opaque ids (i-…, vol-…, etc.)
        var resourceName = TryAwsNameTag(tags) ?? leafFromArn;
        if (string.IsNullOrWhiteSpace(resourceName) || resourceName.Equals(arn, StringComparison.Ordinal))
            resourceName = leafFromArn;

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

    private static string? TryAwsNameTag(Dictionary<string, string>? tags)
    {
        if (tags == null) return null;
        foreach (var key in new[] { "Name", "name", "aws:cloudformation:stack-name" })
        {
            if (tags.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
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
