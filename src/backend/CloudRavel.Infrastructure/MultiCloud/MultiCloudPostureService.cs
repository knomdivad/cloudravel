using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// Inventory-derived multi-cloud posture for AWS/GCP (Azure still uses Advisor /
/// Policy / Defender sync). Writes into the same tables the Security and
/// Governance pages already read, and auto-proposes low-risk playbook actions
/// for the Approvals queue.
///
/// Rules are intentionally conservative — only high-confidence signals from
/// Cloud Asset / inventory properties. Expand as more asset types are needed.
/// </summary>
public sealed class MultiCloudPostureService : IMultiCloudPostureService
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IRemediationService _remediationService;
    private readonly ILogger<MultiCloudPostureService> _logger;

    public MultiCloudPostureService(
        IInventoryRepository inventoryRepo,
        IRecommendationRepository recRepo,
        IRemediationService remediationService,
        ILogger<MultiCloudPostureService> logger)
    {
        _inventoryRepo = inventoryRepo;
        _recRepo = recRepo;
        _remediationService = remediationService;
        _logger = logger;
    }

    public async Task<MultiCloudPostureResult> EvaluateTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var result = new MultiCloudPostureResult();
        var resources = await _inventoryRepo.GetResourcesAsync(tenantId, limit: 5000);
        if (resources.Count == 0)
            return result;

        var findings = new List<DefenderFinding>();
        var recommendations = new List<AdvisorRecommendation>();
        var proposals = new List<(string Playbook, string ResourceId, string Title, string Reason, string? Params)>();

        foreach (var r in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = (r.Provider ?? "").ToLowerInvariant();
            if (provider is not ("gcp" or "aws"))
                continue;

            if (provider == "gcp")
                EvaluateGcp(tenantId, r, findings, recommendations, proposals);
            else
                EvaluateAws(tenantId, r, findings, recommendations, proposals);
        }

        if (findings.Count > 0)
            await _recRepo.UpsertDefenderFindingsAsync(findings);
        if (recommendations.Count > 0)
            await _recRepo.UpsertAdvisorRecommendationsAsync(recommendations);

        foreach (var p in proposals)
        {
            try
            {
                await _remediationService.ProposeAsync(
                    tenantId,
                    playbookKey: p.Playbook,
                    resourceId: p.ResourceId,
                    title: p.Title,
                    reason: p.Reason,
                    parametersJson: p.Params,
                    requestedBy: "system:multicloud-posture");
                result.RemediationProposals++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping remediation proposal {Playbook} for {Resource}", p.Playbook, p.ResourceId);
            }
        }

        result.SecurityFindings = findings.Count;
        result.GovernanceRecommendations = recommendations.Count;
        if (findings.Count + recommendations.Count + result.RemediationProposals > 0)
        {
            _logger.LogInformation(
                "Multi-cloud posture for {TenantId}: {Findings} security findings, {Recs} recommendations, {Props} remediation proposals",
                tenantId, result.SecurityFindings, result.GovernanceRecommendations, result.RemediationProposals);
        }
        return result;
    }

    private static void EvaluateGcp(
        Guid tenantId,
        InventoryResource r,
        List<DefenderFinding> findings,
        List<AdvisorRecommendation> recommendations,
        List<(string, string, string, string, string?)> proposals)
    {
        var type = r.ResourceType ?? "";
        using var props = TryParseProps(r.PropertiesJson);

        // --- Cloud Storage: public access prevention ---
        if (type.Contains("storage.googleapis.com/Bucket", StringComparison.OrdinalIgnoreCase)
            || type.Equals("storage.googleapis.com/Bucket", StringComparison.OrdinalIgnoreCase))
        {
            var pap = GetNestedString(props, "iamConfiguration", "publicAccessPrevention");
            var papEnforced = string.Equals(pap, "enforced", StringComparison.OrdinalIgnoreCase);
            if (!papEnforced)
            {
                var bucket = r.ResourceName;
                findings.Add(Finding(tenantId, r, "GCP.Storage.PublicAccessPrevention", "High",
                    "Cloud Storage bucket without public access prevention enforced",
                    $"Bucket '{bucket}' has publicAccessPrevention='{pap ?? "unset"}'. Anonymous or cross-project public access may be possible.",
                    "Set publicAccessPrevention=enforced (CloudRavel playbook gcp-storage-enforce-pap).",
                    ["Security", "GCP", "Storage"]));

                recommendations.Add(Advisor(tenantId, r, "Security", "High",
                    "Enforce public access prevention on GCS bucket",
                    $"Bucket '{bucket}' should set publicAccessPrevention=enforced unless intentionally public.",
                    "gcp-storage-enforce-pap"));

                proposals.Add((
                    "gcp-storage-enforce-pap",
                    r.ResourceId,
                    $"Enforce public access prevention on {bucket}",
                    "Inventory posture: bucket does not have publicAccessPrevention=enforced.",
                    JsonSerializer.Serialize(new { bucket })));
            }
        }

        // --- VPC firewall: 0.0.0.0/0 open ---
        if (type.Contains("compute.googleapis.com/Firewall", StringComparison.OrdinalIgnoreCase))
        {
            if (HasOpenCidr(props) && IsIngress(props))
            {
                findings.Add(Finding(tenantId, r, "GCP.Compute.OpenFirewall", "Critical",
                    "VPC firewall rule allows traffic from the public internet",
                    $"Firewall '{r.ResourceName}' includes 0.0.0.0/0 (or ::/0) as a source range.",
                    "Restrict sourceRanges to known CIDRs or remove the rule.",
                    ["Security", "GCP", "Network"]));

                recommendations.Add(Advisor(tenantId, r, "Security", "High",
                    "Tighten overly permissive VPC firewall rule",
                    $"Firewall '{r.ResourceName}' allows internet-wide ingress.",
                    "Review sourceRanges and allowed protocols/ports."));
            }
        }

        // --- GCE instance without labels (governance) ---
        if (type.Contains("compute.googleapis.com/Instance", StringComparison.OrdinalIgnoreCase))
        {
            if (r.Tags == null || r.Tags.Count == 0)
            {
                recommendations.Add(Advisor(tenantId, r, "OperationalExcellence", "Medium",
                    "Compute Engine instance missing labels",
                    $"Instance '{r.ResourceName}' has no labels. Apply owner/env/cost-center labels for governance.",
                    "Add required labels via Terraform/Console."));
            }

            // Spot / preemptible check not always in properties; skip cost heuristics when uncertain.
            var status = GetString(props, "status");
            if (string.Equals(status, "TERMINATED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                // Not a finding — optional cost note only if persistent disks still bill; skip noise.
            }
        }

        // --- Service account keys age not available from Asset resource alone ---
    }

    private static void EvaluateAws(
        Guid tenantId,
        InventoryResource r,
        List<DefenderFinding> findings,
        List<AdvisorRecommendation> recommendations,
        List<(string, string, string, string, string?)> proposals)
    {
        var type = r.ResourceType ?? "";
        // Tagging API inventory is tag-light; still flag untagged resources for governance.
        if (r.Tags == null || r.Tags.Count == 0)
        {
            if (type.Contains("s3", StringComparison.OrdinalIgnoreCase)
                || type.Contains("ec2", StringComparison.OrdinalIgnoreCase)
                || type.Contains("rds", StringComparison.OrdinalIgnoreCase)
                || type.Contains("lambda", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add(Advisor(tenantId, r, "OperationalExcellence", "Low",
                    "AWS resource missing tags",
                    $"Resource '{r.ResourceName}' ({type}) has no tags. Tag for ownership and cost allocation.",
                    "Apply required tags (owner, environment, cost-center)."));
            }
        }

        // S3 public access block is not returned by the Resource Groups Tagging API inventory path.
        // When properties eventually include PublicAccessBlock, evaluate here.
        using var props = TryParseProps(r.PropertiesJson);
        if (props != null && type.Contains("s3", StringComparison.OrdinalIgnoreCase))
        {
            var block = GetNestedBool(props, "PublicAccessBlockConfiguration", "BlockPublicAcls");
            if (block == false)
            {
                var bucket = r.ResourceName;
                findings.Add(Finding(tenantId, r, "AWS.S3.PublicAccessBlock", "High",
                    "S3 bucket public access block incomplete",
                    $"Bucket '{bucket}' does not fully block public ACLs/policies.",
                    "Enable all four S3 Public Access Block settings (playbook aws-s3-block-public-access).",
                    ["Security", "AWS", "Storage"]));

                recommendations.Add(Advisor(tenantId, r, "Security", "High",
                    "Block public access on S3 bucket",
                    $"Bucket '{bucket}' should enable S3 Block Public Access.",
                    "aws-s3-block-public-access"));

                proposals.Add((
                    "aws-s3-block-public-access",
                    r.ResourceId,
                    $"Block public access on {bucket}",
                    "Inventory posture: S3 public access block is not fully enabled.",
                    JsonSerializer.Serialize(new { bucket, region = r.Location })));
            }
        }
    }

    private static DefenderFinding Finding(
        Guid tenantId, InventoryResource r, string assessmentKey, string severity,
        string name, string description, string remediation, List<string> categories)
    {
        var findingId = StableId("sec", tenantId.ToString(), assessmentKey, r.ResourceId);
        return new DefenderFinding
        {
            TenantId = tenantId,
            FindingId = findingId,
            ResourceId = r.ResourceId,
            AssessmentName = name,
            Severity = severity,
            Status = "Unhealthy",
            Description = description,
            RemediationSteps = remediation,
            Categories = categories,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        };
    }

    private static AdvisorRecommendation Advisor(
        Guid tenantId, InventoryResource r, string category, string impact,
        string title, string description, string remediation)
    {
        var recId = StableId("gov", tenantId.ToString(), category, r.ResourceId, title);
        return new AdvisorRecommendation
        {
            TenantId = tenantId,
            RecommendationId = recId,
            ResourceId = r.ResourceId,
            Category = category,
            Impact = impact,
            Title = title,
            Description = description,
            RemediationAction = remediation,
            LifecycleStatus = RecommendationLifecycle.Active,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        };
    }

    private static string StableId(params string[] parts)
    {
        var raw = string.Join("|", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    private static JsonDocument? TryParseProps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch { return null; }
    }

    private static string? GetString(JsonDocument? doc, string name)
    {
        if (doc == null) return null;
        return doc.RootElement.TryGetProperty(name, out var el) ? el.GetString() : null;
    }

    private static string? GetNestedString(JsonDocument? doc, string a, string b)
    {
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty(a, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        return el.TryGetProperty(b, out var child) ? child.GetString() : null;
    }

    private static bool? GetNestedBool(JsonDocument? doc, string a, string b)
    {
        if (doc == null) return null;
        if (!doc.RootElement.TryGetProperty(a, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty(b, out var child)) return null;
        return child.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsIngress(JsonDocument? doc)
    {
        if (doc == null) return true;
        if (!doc.RootElement.TryGetProperty("direction", out var d)) return true;
        var dir = d.GetString();
        return string.IsNullOrEmpty(dir) || dir.Equals("INGRESS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOpenCidr(JsonDocument? doc)
    {
        if (doc == null) return false;
        if (!doc.RootElement.TryGetProperty("sourceRanges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var r in ranges.EnumerateArray())
        {
            var s = r.GetString();
            if (s is "0.0.0.0/0" or "::/0") return true;
        }
        return false;
    }
}
