using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// GCP-native Security / Governance collection for parity with Azure
/// Advisor + Policy + Defender: Security Command Center, Recommender, Org Policy.
/// </summary>
public sealed partial class GcpProviderAdapter
{
    // High-value recommenders across cost, security, IAM, and reliability.
    private static readonly string[] DefaultRecommenders =
    [
        "google.compute.instance.MachineTypeRecommender",
        "google.compute.instance.IdleResourceRecommender",
        "google.compute.disk.IdleResourceRecommender",
        "google.compute.address.IdleResourceRecommender",
        "google.iam.policy.Recommender",
        "google.cloud.security.general.IAMRecommender",
        "google.cloudsql.instance.IdleRecommender",
        "google.cloudsql.instance.OverprovisionedRecommender",
        "google.container.DiagnosisRecommender",
        "google.resourcemanager.projectUtilization.Recommender",
    ];

    private static readonly string[] RecommenderLocations =
    [
        "global", "us-central1", "us-east1", "us-west1", "europe-west1", "asia-east1"
    ];

    public async Task<CloudGovernanceSnapshot> CollectGovernanceAsync(
        CloudAccount account, CancellationToken cancellationToken = default)
    {
        var findings = new List<DefenderFinding>();
        var recommendations = new List<AdvisorRecommendation>();
        var policy = new List<PolicyComplianceRecord>();
        var notes = new List<string>();

        string token;
        try
        {
            token = await GetAccessTokenAsync(account);
        }
        catch (Exception ex)
        {
            return new CloudGovernanceSnapshot { SourceNotes = [$"GCP credentials unavailable: {ex.Message}"] };
        }

        var project = account.ExternalId;

        await TrySourceAsync(notes, "Security Command Center", async () =>
        {
            findings.AddRange(await FetchSccFindingsAsync(account, token, project, cancellationToken));
        });

        await TrySourceAsync(notes, "Recommender", async () =>
        {
            recommendations.AddRange(await FetchRecommendersAsync(account, token, project, cancellationToken));
        });

        await TrySourceAsync(notes, "Organization Policy (effective)", async () =>
        {
            policy.AddRange(await FetchOrgPoliciesAsync(account, token, project, cancellationToken));
        });

        return new CloudGovernanceSnapshot
        {
            SecurityFindings = findings,
            Recommendations = recommendations,
            PolicyRecords = policy,
            SourceNotes = notes
        };
    }

    private async Task<List<DefenderFinding>> FetchSccFindingsAsync(
        CloudAccount account, string token, string project, CancellationToken ct)
    {
        var results = new List<DefenderFinding>();
        // v2 API parent: projects/{project}/sources/- 
        // Also try v1 list with filter for ACTIVE
        string? pageToken = null;
        var pages = 0;
        do
        {
            var url =
                $"https://securitycenter.googleapis.com/v1/projects/{Uri.EscapeDataString(project)}/sources/-/findings" +
                $"?pageSize=100&filter={Uri.EscapeDataString("state=\"ACTIVE\"")}" +
                (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

            using var doc = await GcpGetJsonAsync(token, url, ct);
            if (doc.RootElement.TryGetProperty("listFindingsResults", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (!item.TryGetProperty("finding", out var f))
                        continue;
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var category = f.TryGetProperty("category", out var c) ? c.GetString() : "SCC Finding";
                    var severity = MapGcpSeverity(f.TryGetProperty("severity", out var s) ? s.GetString() : null);
                    var desc = f.TryGetProperty("description", out var d) ? d.GetString() : null;
                    var resourceName = f.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null;
                    var externalUri = f.TryGetProperty("externalUri", out var eu) ? eu.GetString() : null;

                    results.Add(new DefenderFinding
                    {
                        TenantId = account.TenantId,
                        FindingId = $"gcp-scc:{StableHash(name!)}",
                        ResourceId = resourceName,
                        AssessmentName = category ?? "Security Command Center finding",
                        Severity = severity,
                        Status = "Unhealthy",
                        Description = desc ?? category,
                        RemediationSteps = externalUri != null
                            ? $"Review in Security Command Center: {externalUri}"
                            : "Review the finding in Google Security Command Center and apply the recommended remediation.",
                        Categories = ["Security", "GCP", "SCC"],
                        LastSeenAt = DateTime.UtcNow
                    });
                }
            }
            // Alternate shape: findings array (some API variants)
            else if (doc.RootElement.TryGetProperty("findings", out var findingsArr))
            {
                foreach (var f in findingsArr.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    results.Add(new DefenderFinding
                    {
                        TenantId = account.TenantId,
                        FindingId = $"gcp-scc:{StableHash(name!)}",
                        ResourceId = f.TryGetProperty("resourceName", out var rn) ? rn.GetString() : null,
                        AssessmentName = f.TryGetProperty("category", out var c) ? c.GetString() ?? "SCC" : "SCC",
                        Severity = MapGcpSeverity(f.TryGetProperty("severity", out var s) ? s.GetString() : null),
                        Status = "Unhealthy",
                        Description = f.TryGetProperty("description", out var d) ? d.GetString() : null,
                        RemediationSteps = "Review in Google Security Command Center.",
                        Categories = ["Security", "GCP", "SCC"],
                        LastSeenAt = DateTime.UtcNow
                    });
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            pages++;
        } while (!string.IsNullOrEmpty(pageToken) && pages < 10);

        return results;
    }

    private async Task<List<AdvisorRecommendation>> FetchRecommendersAsync(
        CloudAccount account, string token, string project, CancellationToken ct)
    {
        var results = new List<AdvisorRecommendation>();
        foreach (var location in RecommenderLocations)
        {
            foreach (var recommender in DefaultRecommenders)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var url =
                        $"https://recommender.googleapis.com/v1/projects/{Uri.EscapeDataString(project)}" +
                        $"/locations/{Uri.EscapeDataString(location)}/recommenders/{Uri.EscapeDataString(recommender)}/recommendations" +
                        $"?pageSize=50&filter={Uri.EscapeDataString("stateInfo.state=ACTIVE")}";

                    using var doc = await GcpGetJsonAsync(token, url, ct);
                    if (!doc.RootElement.TryGetProperty("recommendations", out var recs))
                        continue;

                    foreach (var rec in recs.EnumerateArray())
                    {
                        var name = rec.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        var desc = rec.TryGetProperty("description", out var d) ? d.GetString() : recommender;
                        var category = MapRecommenderCategory(recommender);
                        var impact = "Medium";
                        if (rec.TryGetProperty("priority", out var p))
                            impact = p.GetString() switch
                            {
                                "P1" => "High",
                                "P2" => "High",
                                "P3" => "Medium",
                                "P4" => "Low",
                                _ => "Medium"
                            };

                        string? resourceId = null;
                        if (rec.TryGetProperty("content", out var content)
                            && content.TryGetProperty("overview", out var overview)
                            && overview.ValueKind == JsonValueKind.Object)
                        {
                            // best-effort resource extraction
                            foreach (var prop in overview.EnumerateObject())
                            {
                                if (prop.Name.Contains("resource", StringComparison.OrdinalIgnoreCase)
                                    && prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    resourceId = prop.Value.GetString();
                                    break;
                                }
                            }
                        }

                        decimal? savings = null;
                        if (rec.TryGetProperty("primaryImpact", out var pi)
                            && pi.TryGetProperty("costProjection", out var cp)
                            && cp.TryGetProperty("cost", out var cost)
                            && cost.TryGetProperty("units", out var units)
                            && long.TryParse(units.GetString(), out var u))
                        {
                            // GCP cost projection is often negative for savings
                            savings = Math.Abs(u);
                        }

                        results.Add(new AdvisorRecommendation
                        {
                            TenantId = account.TenantId,
                            RecommendationId = $"gcp-rec:{StableHash(name!)}",
                            ResourceId = resourceId,
                            Category = category,
                            Impact = impact,
                            Title = Truncate(desc ?? recommender, 200),
                            Description = $"{recommender} @ {location}",
                            RemediationAction = "Apply the recommender recommendation in Google Cloud Console or gcloud recommender.",
                            EstimatedSavings = savings,
                            LifecycleStatus = RecommendationLifecycle.Active,
                            LastSeenAt = DateTime.UtcNow
                        });
                    }
                }
                catch
                {
                    // Recommender not enabled / no permission for this type/location — skip.
                }
            }
        }

        return results;
    }

    private async Task<List<PolicyComplianceRecord>> FetchOrgPoliciesAsync(
        CloudAccount account, string token, string project, CancellationToken ct)
    {
        var results = new List<PolicyComplianceRecord>();
        // Effective org policies on the project (v2)
        var url =
            $"https://orgpolicy.googleapis.com/v2/projects/{Uri.EscapeDataString(project)}/policies";
        using var doc = await GcpGetJsonAsync(token, url, ct);
        if (!doc.RootElement.TryGetProperty("policies", out var policies))
            return results;

        foreach (var pol in policies.EnumerateArray())
        {
            var name = pol.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var shortName = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;

            // Presence of an effective constraint is "enforced"; we record as Compliant
            // for the project baseline. Non-compliant resource evaluation needs Asset
            // Policy Analyzer (optional follow-up).
            results.Add(new PolicyComplianceRecord
            {
                TenantId = account.TenantId,
                PolicyAssignmentId = name!,
                PolicyDefinitionId = shortName,
                PolicyName = shortName,
                ResourceId = $"//cloudresourcemanager.googleapis.com/projects/{project}",
                ComplianceState = "Compliant",
                Category = "GCP Organization Policy",
                LastEvaluatedAt = DateTime.UtcNow
            });
        }

        return results;
    }

    private async Task<JsonDocument> GcpGetJsonAsync(string token, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GCP {(int)response.StatusCode}: {Truncate(body, 300)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
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

    private static string MapGcpSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "CRITICAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" => "Medium",
        "LOW" => "Low",
        _ => "Medium"
    };

    private static string MapRecommenderCategory(string recommenderId)
    {
        if (recommenderId.Contains("IAM", StringComparison.OrdinalIgnoreCase)
            || recommenderId.Contains("security", StringComparison.OrdinalIgnoreCase))
            return "Security";
        if (recommenderId.Contains("Idle", StringComparison.OrdinalIgnoreCase)
            || recommenderId.Contains("MachineType", StringComparison.OrdinalIgnoreCase)
            || recommenderId.Contains("Overprovisioned", StringComparison.OrdinalIgnoreCase)
            || recommenderId.Contains("Utilization", StringComparison.OrdinalIgnoreCase))
            return "Cost";
        if (recommenderId.Contains("Diagnosis", StringComparison.OrdinalIgnoreCase))
            return "Reliability";
        return "OperationalExcellence";
    }

    private static string StableHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
