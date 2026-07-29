using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// AWS-native Security / Governance / Policy collection for parity with Azure
/// Advisor + Policy + Defender. Best-effort per source (403/not-enabled → notes).
/// </summary>
public sealed partial class AwsProviderAdapter
{
    public async Task<CloudGovernanceSnapshot> CollectGovernanceAsync(
        CloudAccount account, CancellationToken cancellationToken = default)
    {
        var findings = new List<DefenderFinding>();
        var recommendations = new List<AdvisorRecommendation>();
        var policy = new List<PolicyComplianceRecord>();
        var notes = new List<string>();

        AwsCredentials creds;
        try
        {
            creds = await ResolveCredentialsAsync(account);
        }
        catch (Exception ex)
        {
            return new CloudGovernanceSnapshot { SourceNotes = [$"AWS credentials unavailable: {ex.Message}"] };
        }

        var regions = account.Regions is { Count: > 0 }
            ? account.Regions
            : new List<string> { creds.DefaultRegion ?? "us-east-1" };
        var primary = regions[0];

        await TrySourceAsync(notes, "Security Hub", async () =>
        {
            foreach (var region in regions.Take(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                findings.AddRange(await FetchSecurityHubFindingsAsync(
                    account, creds, region, cancellationToken));
            }
        });

        await TrySourceAsync(notes, "AWS Config", async () =>
        {
            foreach (var region in regions.Take(8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                policy.AddRange(await FetchConfigComplianceAsync(
                    account, creds, region, cancellationToken));
            }
        });

        await TrySourceAsync(notes, "Trusted Advisor", async () =>
        {
            // Support API is us-east-1 only; requires Business/Enterprise support plan.
            recommendations.AddRange(await FetchTrustedAdvisorAsync(
                account, creds, cancellationToken));
        });

        await TrySourceAsync(notes, "S3 public access blocks", async () =>
        {
            findings.AddRange(await ProbeS3PublicAccessAsync(
                account, creds, primary, cancellationToken));
        });

        await TrySourceAsync(notes, "EC2 security groups", async () =>
        {
            foreach (var region in regions.Take(8))
            {
                findings.AddRange(await ProbeOpenSecurityGroupsAsync(
                    account, creds, region, cancellationToken));
            }
        });

        return new CloudGovernanceSnapshot
        {
            SecurityFindings = findings,
            Recommendations = recommendations,
            PolicyRecords = policy,
            SourceNotes = notes
        };
    }

    private async Task<List<DefenderFinding>> FetchSecurityHubFindingsAsync(
        CloudAccount account, AwsCredentials creds, string region, CancellationToken ct)
    {
        var results = new List<DefenderFinding>();
        string? nextToken = null;
        var pages = 0;
        do
        {
            var payload = new Dictionary<string, object?>
            {
                ["MaxResults"] = 50,
                ["NextToken"] = nextToken,
                ["Filters"] = new Dictionary<string, object>
                {
                    ["RecordState"] = new[] { new { Value = "ACTIVE", Comparison = "EQUALS" } },
                    ["WorkflowStatus"] = new[]
                    {
                        new { Value = "NEW", Comparison = "EQUALS" },
                        new { Value = "NOTIFIED", Comparison = "EQUALS" }
                    }
                }
            };
            using var doc = await AwsJsonAsync(creds, "securityhub", region,
                "SecurityHub_20180101.GetFindings",
                $"https://securityhub.{region}.amazonaws.com/",
                payload, ct);

            if (doc.RootElement.TryGetProperty("Findings", out var list))
            {
                foreach (var f in list.EnumerateArray())
                {
                    var id = f.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    var title = f.TryGetProperty("Title", out var t) ? t.GetString() : "Security Hub finding";
                    var desc = f.TryGetProperty("Description", out var d) ? d.GetString() : null;
                    var sevLabel = "Medium";
                    if (f.TryGetProperty("Severity", out var sev) && sev.TryGetProperty("Label", out var lab))
                        sevLabel = MapAwsSeverity(lab.GetString());
                    string? resourceId = null;
                    if (f.TryGetProperty("Resources", out var res) && res.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in res.EnumerateArray())
                        {
                            if (r.TryGetProperty("Id", out var rid))
                            {
                                resourceId = rid.GetString();
                                break;
                            }
                        }
                    }

                    results.Add(new DefenderFinding
                    {
                        TenantId = account.TenantId,
                        FindingId = $"aws-sh:{id}",
                        ResourceId = resourceId,
                        AssessmentName = title ?? "Security Hub finding",
                        Severity = sevLabel,
                        Status = "Unhealthy",
                        Description = desc,
                        RemediationSteps = f.TryGetProperty("Remediation", out var rem)
                            && rem.TryGetProperty("Recommendation", out var rec)
                            && rec.TryGetProperty("Text", out var txt)
                                ? txt.GetString()
                                : "Review in AWS Security Hub and remediate per the finding guidance.",
                        Categories = ["Security", "AWS", "SecurityHub", region],
                        LastSeenAt = DateTime.UtcNow
                    });
                }
            }

            nextToken = doc.RootElement.TryGetProperty("NextToken", out var nt) ? nt.GetString() : null;
            pages++;
        } while (!string.IsNullOrEmpty(nextToken) && pages < 10);

        return results;
    }

    private async Task<List<PolicyComplianceRecord>> FetchConfigComplianceAsync(
        CloudAccount account, AwsCredentials creds, string region, CancellationToken ct)
    {
        var results = new List<PolicyComplianceRecord>();
        // List rules first
        using var rulesDoc = await AwsJsonAsync(creds, "config", region,
            "StarlingDoveService.DescribeConfigRules",
            $"https://config.{region}.amazonaws.com/",
            new Dictionary<string, object?>(), ct);

        var ruleNames = new List<string>();
        if (rulesDoc.RootElement.TryGetProperty("ConfigRules", out var rules))
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("ConfigRuleName", out var n) && n.GetString() is { } name)
                    ruleNames.Add(name);
            }
        }

        foreach (var batch in ruleNames.Chunk(25))
        {
            ct.ThrowIfCancellationRequested();
            using var compDoc = await AwsJsonAsync(creds, "config", region,
                "StarlingDoveService.DescribeComplianceByConfigRule",
                $"https://config.{region}.amazonaws.com/",
                new Dictionary<string, object?>
                {
                    ["ConfigRuleNames"] = batch.ToArray(),
                    ["ComplianceTypes"] = new[] { "NON_COMPLIANT", "COMPLIANT" }
                }, ct);

            if (!compDoc.RootElement.TryGetProperty("ComplianceByConfigRules", out var list))
                continue;

            foreach (var item in list.EnumerateArray())
            {
                var ruleName = item.TryGetProperty("ConfigRuleName", out var rn) ? rn.GetString() ?? "rule" : "rule";
                var compliance = "Unknown";
                if (item.TryGetProperty("Compliance", out var c) && c.TryGetProperty("ComplianceType", out var ctEl))
                    compliance = ctEl.GetString() switch
                    {
                        "NON_COMPLIANT" => "NonCompliant",
                        "COMPLIANT" => "Compliant",
                        _ => ctEl.GetString() ?? "Unknown"
                    };

                results.Add(new PolicyComplianceRecord
                {
                    TenantId = account.TenantId,
                    PolicyAssignmentId = $"aws-config:{account.ExternalId}:{region}:{ruleName}",
                    PolicyDefinitionId = ruleName,
                    PolicyName = ruleName,
                    ResourceId = $"arn:aws:config:{region}:{account.ExternalId}:config-rule/{ruleName}",
                    ComplianceState = compliance,
                    Category = "AWS Config",
                    LastEvaluatedAt = DateTime.UtcNow
                });
            }
        }

        return results;
    }

    private async Task<List<AdvisorRecommendation>> FetchTrustedAdvisorAsync(
        CloudAccount account, AwsCredentials creds, CancellationToken ct)
    {
        var results = new List<AdvisorRecommendation>();
        // Support API only in us-east-1
        using var checksDoc = await AwsJsonAsync(creds, "support", "us-east-1",
            "AWSSupport_20130415.DescribeTrustedAdvisorChecks",
            "https://support.us-east-1.amazonaws.com/",
            new Dictionary<string, object?> { ["language"] = "en" }, ct);

        if (!checksDoc.RootElement.TryGetProperty("checks", out var checks))
            return results;

        var checkIds = new List<(string Id, string Name, string Category)>();
        foreach (var check in checks.EnumerateArray())
        {
            var id = check.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = check.TryGetProperty("name", out var n) ? n.GetString() : null;
            var cat = check.TryGetProperty("category", out var c) ? c.GetString() : "operational";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                checkIds.Add((id!, name!, cat ?? "operational"));
        }

        foreach (var (id, name, cat) in checkIds.Take(40))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resultDoc = await AwsJsonAsync(creds, "support", "us-east-1",
                    "AWSSupport_20130415.DescribeTrustedAdvisorCheckResult",
                    "https://support.us-east-1.amazonaws.com/",
                    new Dictionary<string, object?> { ["checkId"] = id, ["language"] = "en" }, ct);

                if (!resultDoc.RootElement.TryGetProperty("result", out var result))
                    continue;
                var status = result.TryGetProperty("status", out var st) ? st.GetString() : null;
                // error | warning | ok | not_available
                if (status is "ok" or "not_available" or null)
                    continue;

                var category = MapTaCategory(cat);
                var impact = status == "error" ? "High" : "Medium";
                results.Add(new AdvisorRecommendation
                {
                    TenantId = account.TenantId,
                    RecommendationId = $"aws-ta:{account.ExternalId}:{id}",
                    ResourceId = null,
                    Category = category,
                    Impact = impact,
                    Title = name,
                    Description = $"AWS Trusted Advisor status: {status}. Review the check in the AWS Support console.",
                    RemediationAction = "Open AWS Trusted Advisor and apply the recommended remediations.",
                    LifecycleStatus = RecommendationLifecycle.Active,
                    LastSeenAt = DateTime.UtcNow
                });
            }
            catch
            {
                // Individual check failures are non-fatal.
            }
        }

        return results;
    }

    private async Task<List<DefenderFinding>> ProbeS3PublicAccessAsync(
        CloudAccount account, AwsCredentials creds, string region, CancellationToken ct)
    {
        var results = new List<DefenderFinding>();
        // List buckets (global S3 endpoint)
        var listBody = Array.Empty<byte>();
        var listReq = new HttpRequestMessage(HttpMethod.Get, "https://s3.amazonaws.com/");
        AwsSigV4.Sign(listReq, "s3", "us-east-1", creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, listBody);
        var listResp = await Http.SendAsync(listReq, ct);
        var listXml = await listResp.Content.ReadAsStringAsync(ct);
        if (!listResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"S3 ListBuckets {(int)listResp.StatusCode}: {Truncate(listXml, 200)}");

        foreach (var bucket in ExtractXmlTags(listXml, "Name").Take(100))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var getBody = Array.Empty<byte>();
                // Try region-specific; fall back to path-style us-east-1
                var url = $"https://{bucket}.s3.{region}.amazonaws.com/?publicAccessBlock";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AwsSigV4.Sign(req, "s3", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, getBody);
                var resp = await Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if ((int)resp.StatusCode == 404)
                {
                    // No public access block configuration = risk
                    results.Add(S3Finding(account, bucket, region, "No Public Access Block configuration"));
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    continue;

                var blockAcls = body.Contains("<BlockPublicAcls>true</BlockPublicAcls>", StringComparison.OrdinalIgnoreCase);
                var ignoreAcls = body.Contains("<IgnorePublicAcls>true</IgnorePublicAcls>", StringComparison.OrdinalIgnoreCase);
                var blockPolicy = body.Contains("<BlockPublicPolicy>true</BlockPublicPolicy>", StringComparison.OrdinalIgnoreCase);
                var restrict = body.Contains("<RestrictPublicBuckets>true</RestrictPublicBuckets>", StringComparison.OrdinalIgnoreCase);
                if (!(blockAcls && ignoreAcls && blockPolicy && restrict))
                    results.Add(S3Finding(account, bucket, region, "Public Access Block not fully enabled"));
            }
            catch
            {
                // Skip individual bucket errors (wrong region etc.)
            }
        }

        return results;
    }

    private static DefenderFinding S3Finding(CloudAccount account, string bucket, string region, string detail) =>
        new()
        {
            TenantId = account.TenantId,
            FindingId = $"aws-s3-pab:{account.ExternalId}:{bucket}",
            ResourceId = $"arn:aws:s3:::{bucket}",
            AssessmentName = "S3 bucket public access block incomplete",
            Severity = "High",
            Status = "Unhealthy",
            Description = $"{detail} for bucket '{bucket}'.",
            RemediationSteps = "Enable all four S3 Block Public Access settings (CloudRavel playbook aws-s3-block-public-access).",
            Categories = ["Security", "AWS", "S3", region],
            LastSeenAt = DateTime.UtcNow
        };

    private async Task<List<DefenderFinding>> ProbeOpenSecurityGroupsAsync(
        CloudAccount account, AwsCredentials creds, string region, CancellationToken ct)
    {
        var results = new List<DefenderFinding>();
        var body = Encoding.UTF8.GetBytes("Action=DescribeSecurityGroups&Version=2016-11-15");
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://ec2.{region}.amazonaws.com/")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        AwsSigV4.Sign(request, "ec2", region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);
        var response = await Http.SendAsync(request, ct);
        var xml = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EC2 DescribeSecurityGroups {(int)response.StatusCode}");

        // Crude but effective: groupId near cidrIp 0.0.0.0/0 in ipPermissions (ingress)
        // Parse groupId blocks
        var groups = xml.Split("<item>", StringSplitOptions.RemoveEmptyEntries);
        foreach (var chunk in groups)
        {
            if (!chunk.Contains("<groupId>", StringComparison.Ordinal))
                continue;
            var groupId = ExtractXmlTags(chunk, "groupId").FirstOrDefault();
            var groupName = ExtractXmlTags(chunk, "groupName").FirstOrDefault() ?? groupId;
            if (string.IsNullOrEmpty(groupId))
                continue;
            // Only flag if this chunk looks like an ingress permission with open CIDR
            // (DescribeSecurityGroups nests ipPermissions; open CIDR in the whole group is a signal)
            if (!chunk.Contains("<cidrIp>0.0.0.0/0</cidrIp>", StringComparison.Ordinal)
                && !chunk.Contains("<cidrIpv6>::/0</cidrIpv6>", StringComparison.Ordinal))
                continue;
            // Prefer groups that also have ipPermissions section with open cidr
            if (!chunk.Contains("ipPermissions", StringComparison.OrdinalIgnoreCase)
                && !chunk.Contains("ipRanges", StringComparison.OrdinalIgnoreCase)
                && !chunk.Contains("cidrIp", StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new DefenderFinding
            {
                TenantId = account.TenantId,
                FindingId = $"aws-sg-open:{account.ExternalId}:{region}:{groupId}",
                ResourceId = $"arn:aws:ec2:{region}:{account.ExternalId}:security-group/{groupId}",
                AssessmentName = "Security group allows traffic from the public internet",
                Severity = "Critical",
                Status = "Unhealthy",
                Description = $"Security group '{groupName}' ({groupId}) in {region} includes 0.0.0.0/0 or ::/0.",
                RemediationSteps = "Restrict ingress CIDRs to known networks; remove unused open rules.",
                Categories = ["Security", "AWS", "Network", region],
                LastSeenAt = DateTime.UtcNow
            });
        }

        return results.DistinctBy(f => f.FindingId).ToList();
    }

    private async Task<JsonDocument> AwsJsonAsync(
        AwsCredentials creds, string service, string region, string target, string url,
        Dictionary<string, object?> payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        AwsSigV4.Sign(request, service, region, creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken, body);

        var response = await Http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{service}/{target} {(int)response.StatusCode}: {Truncate(responseBody, 300)}");
        return JsonDocument.Parse(responseBody);
    }

    private static async Task TrySourceAsync(List<string> notes, string source, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            notes.Add($"{source}: skipped ({ex.Message})");
        }
    }

    private static string MapAwsSeverity(string? label) => label?.ToUpperInvariant() switch
    {
        "CRITICAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" => "Medium",
        "LOW" => "Low",
        "INFORMATIONAL" => "Informational",
        _ => "Medium"
    };

    private static string MapTaCategory(string cat) => cat.ToLowerInvariant() switch
    {
        "cost_optimizing" or "cost" => "Cost",
        "security" => "Security",
        "fault_tolerance" or "reliability" => "Reliability",
        "performance" => "Performance",
        "service_limits" => "OperationalExcellence",
        _ => "OperationalExcellence"
    };

    /// <summary>
    /// Extract element text for a tag name, case-insensitive, allowing attributes on the open tag
    /// (e.g. &lt;vpcId&gt; or &lt;vpcId xmlns="…"&gt;).
    /// </summary>
    private static IEnumerable<string> ExtractXmlTags(string xml, string tag)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(tag))
            yield break;

        // <tag>value</tag> or <tag attr="x">value</tag> (case-insensitive)
        var pattern = $@"<{tag}(?:\s[^>]*)?>([^<]*)</{tag}>";
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(xml, pattern,
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var value = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(value))
                yield return value;
        }
    }
}
