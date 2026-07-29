using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// Pulls AWS/GCP provider-native Security, Governance, and Policy signals into the
/// same tables Azure Advisor / Policy / Defender use, then auto-proposes a small
/// set of allow-listed remediations for Approvals parity.
/// </summary>
public sealed class MultiCloudGovernanceSyncService : IMultiCloudGovernanceSyncService
{
    private readonly ICloudAccountRepository _accountRepo;
    private readonly ICloudProviderAdapterFactory _adapters;
    private readonly IRecommendationRepository _recRepo;
    private readonly IRemediationService _remediationService;
    private readonly IMultiCloudPostureService _inventoryPosture;
    private readonly ILogger<MultiCloudGovernanceSyncService> _logger;

    public MultiCloudGovernanceSyncService(
        ICloudAccountRepository accountRepo,
        ICloudProviderAdapterFactory adapters,
        IRecommendationRepository recRepo,
        IRemediationService remediationService,
        IMultiCloudPostureService inventoryPosture,
        ILogger<MultiCloudGovernanceSyncService> logger)
    {
        _accountRepo = accountRepo;
        _adapters = adapters;
        _recRepo = recRepo;
        _remediationService = remediationService;
        _inventoryPosture = inventoryPosture;
        _logger = logger;
    }

    public async Task<CloudGovernanceSnapshot> SyncTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var accounts = (await _accountRepo.GetByTenantAsync(tenantId))
            .Where(a => a.Provider is CloudProvider.Aws or CloudProvider.Gcp
                        && a.Status != CloudAccountStatus.Disconnected)
            .ToList();

        var allFindings = new List<DefenderFinding>();
        var allRecs = new List<AdvisorRecommendation>();
        var allPolicy = new List<PolicyComplianceRecord>();
        var notes = new List<string>();
        var proposals = 0;

        // Inventory-derived rules first (works even when native APIs are off).
        try
        {
            var posture = await _inventoryPosture.EvaluateTenantAsync(tenantId, cancellationToken);
            notes.Add($"Inventory posture: {posture.SecurityFindings} security, {posture.GovernanceRecommendations} recommendations, {posture.RemediationProposals} proposals");
            proposals += posture.RemediationProposals;
        }
        catch (Exception ex)
        {
            notes.Add($"Inventory posture: failed ({ex.Message})");
            _logger.LogWarning(ex, "Inventory posture failed for tenant {TenantId}", tenantId);
        }

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var adapter = _adapters.GetAdapter(account.Provider);
                var snap = await adapter.CollectGovernanceAsync(account, cancellationToken);
                allFindings.AddRange(snap.SecurityFindings);
                allRecs.AddRange(snap.Recommendations);
                allPolicy.AddRange(snap.PolicyRecords);
                foreach (var n in snap.SourceNotes)
                    notes.Add($"{account.Provider} {account.ExternalId}: {n}");
            }
            catch (Exception ex)
            {
                notes.Add($"{account.Provider} {account.ExternalId}: failed ({ex.Message})");
                _logger.LogWarning(ex, "Governance sync failed for {Provider} account {ExternalId}",
                    account.Provider, account.ExternalId);
            }
        }

        if (allFindings.Count > 0)
            await _recRepo.UpsertDefenderFindingsAsync(allFindings);
        if (allRecs.Count > 0)
            await _recRepo.UpsertAdvisorRecommendationsAsync(allRecs);
        if (allPolicy.Count > 0)
            await _recRepo.UpsertPolicyComplianceAsync(allPolicy);

        proposals += await ProposeFromFindingsAsync(tenantId, allFindings);

        _logger.LogInformation(
            "Multi-cloud governance for {TenantId}: {Findings} security, {Recs} recommendations, {Policy} policy rows, {Proposals} new remediation proposals. Notes: {Notes}",
            tenantId, allFindings.Count, allRecs.Count, allPolicy.Count, proposals, string.Join(" | ", notes.Take(8)));

        return new CloudGovernanceSnapshot
        {
            SecurityFindings = allFindings,
            Recommendations = allRecs,
            PolicyRecords = allPolicy,
            SourceNotes = notes
        };
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _accountRepo.GetAllActiveAsync();
        var tenantIds = accounts
            .Where(a => a.Provider is CloudProvider.Aws or CloudProvider.Gcp)
            .Select(a => a.TenantId)
            .Distinct()
            .ToList();

        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncTenantAsync(tenantId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Multi-cloud governance sync failed for tenant {TenantId}", tenantId);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>
    /// Map high-confidence findings onto allow-listed playbooks for Approvals.
    /// </summary>
    private async Task<int> ProposeFromFindingsAsync(Guid tenantId, IReadOnlyList<DefenderFinding> findings)
    {
        var count = 0;
        foreach (var f in findings.Where(x => x.Status == "Unhealthy"))
        {
            try
            {
                if (f.FindingId.StartsWith("aws-s3-pab:", StringComparison.Ordinal)
                    || (f.AssessmentName?.Contains("S3", StringComparison.OrdinalIgnoreCase) == true
                        && f.AssessmentName.Contains("public", StringComparison.OrdinalIgnoreCase)))
                {
                    var bucket = ExtractS3Bucket(f.ResourceId) ?? ExtractS3Bucket(f.FindingId);
                    if (bucket == null) continue;
                    await _remediationService.ProposeAsync(
                        tenantId,
                        "aws-s3-block-public-access",
                        f.ResourceId,
                        $"Block public access on S3 bucket {bucket}",
                        f.Description ?? f.AssessmentName ?? "S3 public access block incomplete",
                        JsonSerializer.Serialize(new { bucket }),
                        "system:aws-governance");
                    count++;
                }
                else if (f.FindingId.StartsWith("gcp-scc:", StringComparison.Ordinal) == false
                         && f.AssessmentName?.Contains("public access prevention", StringComparison.OrdinalIgnoreCase) == true
                         && f.ResourceId != null)
                {
                    var bucket = f.ResourceId.Contains('/')
                        ? f.ResourceId[(f.ResourceId.LastIndexOf('/') + 1)..]
                        : f.ResourceId;
                    await _remediationService.ProposeAsync(
                        tenantId,
                        "gcp-storage-enforce-pap",
                        f.ResourceId,
                        $"Enforce public access prevention on {bucket}",
                        f.Description ?? f.AssessmentName ?? "GCS public access prevention not enforced",
                        JsonSerializer.Serialize(new { bucket }),
                        "system:gcp-governance");
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Remediation proposal skipped for finding {Id}", f.FindingId);
            }
        }

        return count;
    }

    private static string? ExtractS3Bucket(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;
        // arn:aws:s3:::bucket
        if (resourceId.StartsWith("arn:aws:s3:::", StringComparison.Ordinal))
            return resourceId["arn:aws:s3:::".Length..];
        if (resourceId.Contains(':'))
        {
            var parts = resourceId.Split(':');
            return parts[^1];
        }
        return null;
    }
}
