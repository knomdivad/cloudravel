using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.AiOps;

/// <summary>
/// Proactive anomaly detection over the platform's authoritative data stores.
///
/// Detectors (all baseline-driven, no fabricated thresholds):
///   1. ChangeVelocitySpike      — 24h change count vs 30-day daily baseline (z-score)
///   2. SecurityPostureRegression— new Critical/High Defender findings in 24h
///   3. ConfigurationDrift       — critical/high security-classified changes per resource
///   4. UnusualActorActivity     — actors changing resources never seen in the 30d baseline
///   5. ResourceSprawl           — resource count growth vs stored baseline
///   6. CostAnomaly              — active cost-savings total jump vs stored baseline
///   7. StaleTelemetry           — snapshot pipeline older than 2× configured frequency
///
/// High/Critical anomalies auto-open (or join) an incident, and a small set of
/// unambiguous security drifts auto-propose gated remediations from the playbook
/// catalog. The scan never executes anything directly.
/// </summary>
public sealed class AnomalyDetectionService : IAnomalyDetectionService
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly IRecommendationRepository _recRepo;
    private readonly IAnomalyRepository _anomalyRepo;
    private readonly IIncidentRepository _incidentRepo;
    private readonly IRemediationService _remediationService;
    private readonly ILogger<AnomalyDetectionService> _logger;

    public AnomalyDetectionService(
        ITenantRepository tenantRepo,
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        IRecommendationRepository recRepo,
        IAnomalyRepository anomalyRepo,
        IIncidentRepository incidentRepo,
        IRemediationService remediationService,
        ILogger<AnomalyDetectionService> logger)
    {
        _tenantRepo = tenantRepo;
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
        _recRepo = recRepo;
        _anomalyRepo = anomalyRepo;
        _incidentRepo = incidentRepo;
        _remediationService = remediationService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Anomaly>> ScanTenantAsync(Guid tenantId)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        if (tenant == null || !tenant.AiOpsMonitoringEnabled)
            return [];

        var detected = new List<Anomaly>();
        var now = DateTime.UtcNow;

        await RunDetectorAsync(detected, () => DetectChangeVelocityAsync(tenantId, now), "ChangeVelocity");
        await RunDetectorAsync(detected, () => DetectSecurityRegressionAsync(tenantId, now), "SecurityRegression");
        await RunDetectorAsync(detected, () => DetectConfigurationDriftAsync(tenantId, now), "ConfigurationDrift");
        await RunDetectorAsync(detected, () => DetectUnusualActorsAsync(tenantId, now), "UnusualActors");
        await RunDetectorAsync(detected, () => DetectResourceSprawlAsync(tenantId, now), "ResourceSprawl");
        await RunDetectorAsync(detected, () => DetectCostAnomalyAsync(tenantId, now), "CostAnomaly");
        await RunDetectorAsync(detected, () => DetectStaleTelemetryAsync(tenant, now), "StaleTelemetry");

        // Persist: new fingerprints open anomalies; repeats refresh existing rows.
        var newlyOpened = new List<Anomaly>();
        foreach (var anomaly in detected)
        {
            var (_, created) = await _anomalyRepo.UpsertAnomalyAsync(anomaly);
            if (created)
                newlyOpened.Add(anomaly);
        }

        foreach (var anomaly in newlyOpened)
        {
            if (anomaly.Severity is AnomalySeverity.Critical or AnomalySeverity.High)
                await EnsureIncidentAsync(anomaly);

            await TryProposeRemediationAsync(anomaly);
        }

        if (newlyOpened.Count > 0)
            _logger.LogInformation("Anomaly scan for tenant {TenantId}: {New} new, {Total} evaluated",
                tenantId, newlyOpened.Count, detected.Count);

        return newlyOpened;
    }

    private async Task RunDetectorAsync(List<Anomaly> sink, Func<Task<IEnumerable<Anomaly>>> detector, string name)
    {
        try
        {
            sink.AddRange(await detector());
        }
        catch (Exception ex)
        {
            // One broken detector must not block the rest of the scan.
            _logger.LogError(ex, "Detector {Detector} failed", name);
        }
    }

    // ------------------------------------------------------------------
    // Detector 1: change velocity vs 30-day daily baseline
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectChangeVelocityAsync(Guid tenantId, DateTime now)
    {
        var observed = await _changeRepo.GetChangeCountAsync(tenantId, now.AddHours(-24), now);

        // Daily buckets over the 30 days preceding the observation window
        var timeline = await _changeRepo.GetChangeTimelineAsync(tenantId, now.AddDays(-31), now.AddHours(-24));
        var samples = timeline.Select(b => (double)b.TotalChanges).ToList();
        if (samples.Count < 7 || observed < 10)
            return []; // not enough history (or volume) for a meaningful baseline

        var (mean, stdDev) = MeanStdDev(samples);
        var z = stdDev > 0.0001 ? (observed - mean) / stdDev : 0;

        await _anomalyRepo.UpsertBaselineAsync(new MetricBaseline
        {
            TenantId = tenantId, MetricKey = "changes.total.24h", WindowHours = 720,
            Mean = mean, StdDev = stdDev, SampleCount = samples.Count
        });

        if (z < 3)
            return [];

        return new[]
        {
            new Anomaly
            {
                TenantId = tenantId,
                Fingerprint = Fingerprint(tenantId, "ChangeVelocitySpike", "changes.total.24h"),
                Kind = AnomalyKind.ChangeVelocitySpike,
                Severity = z >= 5 ? AnomalySeverity.High : AnomalySeverity.Medium,
                Title = $"Change volume spike: {observed} changes in 24h ({z:F1}σ above baseline)",
                Description = $"The tenant recorded {observed} resource changes in the last 24 hours against a " +
                              $"30-day daily baseline of {mean:F1} (σ={stdDev:F1}). Investigate for runaway " +
                              $"automation, bulk misconfiguration, or unauthorized activity.",
                MetricName = "changes.total.24h",
                ObservedValue = observed,
                BaselineMean = mean,
                BaselineStdDev = stdDev,
                Score = z,
                DetectedAt = now,
                LastSeenAt = now
            }
        };
    }

    // ------------------------------------------------------------------
    // Detector 2: new critical/high Defender findings
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectSecurityRegressionAsync(Guid tenantId, DateTime now)
    {
        var findings = await _recRepo.GetDefenderFindingsAsync(tenantId, status: "Unhealthy", limit: 500);
        var newSevere = findings
            .Where(f => (f.Severity == "Critical" || f.Severity == "High") && f.FirstSeenAt >= now.AddHours(-24))
            .ToList();

        if (newSevere.Count == 0)
            return [];

        var criticalCount = newSevere.Count(f => f.Severity == "Critical");
        var sample = newSevere.Take(10).Select(f => new { f.AssessmentName, f.Severity, f.ResourceId });

        return new[]
        {
            new Anomaly
            {
                TenantId = tenantId,
                Fingerprint = Fingerprint(tenantId, "SecurityPostureRegression", now.ToString("yyyyMMdd")),
                Kind = AnomalyKind.SecurityPostureRegression,
                Severity = criticalCount > 0 ? AnomalySeverity.Critical : AnomalySeverity.High,
                Title = $"{newSevere.Count} new severe security finding(s) in 24h ({criticalCount} critical)",
                Description = "Microsoft Defender for Cloud reported new Critical/High findings in the last 24 hours. " +
                              "Review the security page and correlate with recent changes to identify the root cause.",
                MetricName = "defender.new_severe.24h",
                ObservedValue = newSevere.Count,
                Score = newSevere.Count,
                DetectedAt = now,
                LastSeenAt = now,
                DetailsJson = JsonSerializer.Serialize(new { findings = sample })
            }
        };
    }

    // ------------------------------------------------------------------
    // Detector 3: per-resource security configuration drift
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectConfigurationDriftAsync(Guid tenantId, DateTime now)
    {
        var changes = await _changeRepo.GetChangesAsync(
            tenantId, now.AddHours(-24), null, null, ChangeClassification.Security, limit: 500);

        var severe = changes
            .Where(c => c.Severity is ChangeSeverity.Critical or ChangeSeverity.High)
            .GroupBy(c => c.ResourceId);

        var anomalies = new List<Anomaly>();
        foreach (var group in severe)
        {
            var latest = group.OrderByDescending(c => c.DetectedAt).First();
            anomalies.Add(new Anomaly
            {
                TenantId = tenantId,
                Fingerprint = Fingerprint(tenantId, "ConfigurationDrift", group.Key),
                Kind = AnomalyKind.ConfigurationDrift,
                Severity = group.Any(c => c.Severity == ChangeSeverity.Critical)
                    ? AnomalySeverity.Critical
                    : AnomalySeverity.High,
                Title = $"Security configuration drift on {ShortResourceName(group.Key)}",
                Description = $"{group.Count()} security-impacting change(s) detected in 24h. Latest by " +
                              $"{latest.ActorName ?? "unknown actor"} via {latest.ClientType ?? "unknown client"}.",
                ResourceId = group.Key,
                MetricName = "changes.security.severe",
                ObservedValue = group.Count(),
                DetectedAt = now,
                LastSeenAt = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    changes = group.Take(10).Select(c => new
                    {
                        c.ChangeId,
                        c.DetectedAt,
                        c.ActorName,
                        properties = c.ChangedProperties?.Take(10).Select(p => new { p.Path, p.Before, p.After })
                    })
                })
            });
        }

        return anomalies;
    }

    // ------------------------------------------------------------------
    // Detector 4: actors not seen in the 30-day baseline
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectUnusualActorsAsync(Guid tenantId, DateTime now)
    {
        var recent = await _changeRepo.GetChangesAsync(tenantId, now.AddHours(-24), null, limit: 1000);
        var recentActors = recent
            .Where(c => !string.IsNullOrEmpty(c.ActorId))
            .GroupBy(c => c.ActorId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (recentActors.Count == 0)
            return [];

        var baselineChanges = await _changeRepo.GetChangesAsync(tenantId, now.AddDays(-30), now.AddHours(-24), limit: 5000);
        var knownActors = baselineChanges
            .Where(c => !string.IsNullOrEmpty(c.ActorId))
            .Select(c => c.ActorId!)
            .ToHashSet();

        if (knownActors.Count == 0)
            return []; // no baseline yet — everything would look new

        var anomalies = new List<Anomaly>();
        foreach (var (actorId, changes) in recentActors.Where(kv => !knownActors.Contains(kv.Key)))
        {
            var first = changes[0];
            var touchesSecurity = changes.Any(c => c.Classification == ChangeClassification.Security);
            anomalies.Add(new Anomaly
            {
                TenantId = tenantId,
                Fingerprint = Fingerprint(tenantId, "UnusualActorActivity", actorId),
                Kind = AnomalyKind.UnusualActorActivity,
                Severity = touchesSecurity ? AnomalySeverity.High : AnomalySeverity.Medium,
                Title = $"First-seen actor '{first.ActorName ?? actorId}' made {changes.Count} change(s)",
                Description = $"Actor {first.ActorName ?? actorId} ({first.ActorType ?? "unknown type"}) has no " +
                              $"activity in the 30-day baseline but made {changes.Count} change(s) in the last " +
                              $"24 hours{(touchesSecurity ? ", including security-classified changes" : "")}. " +
                              "Verify this identity is expected (new hire, new automation, or compromised credential).",
                MetricName = "changes.actor.new",
                ObservedValue = changes.Count,
                DetectedAt = now,
                LastSeenAt = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    actorId,
                    actorName = first.ActorName,
                    actorType = first.ActorType,
                    clientTypes = changes.Select(c => c.ClientType).Distinct(),
                    resources = changes.Select(c => c.ResourceId).Distinct().Take(10)
                })
            });
        }

        return anomalies;
    }

    // ------------------------------------------------------------------
    // Detector 5: resource sprawl vs stored baseline
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectResourceSprawlAsync(Guid tenantId, DateTime now)
    {
        var count = await _inventoryRepo.GetResourceCountAsync(tenantId);
        if (count == 0)
            return [];

        var baseline = await _anomalyRepo.GetBaselineAsync(tenantId, "inventory.resource_count");
        var anomalies = new List<Anomaly>();

        if (baseline != null && baseline.Mean > 0)
        {
            var growth = (count - baseline.Mean) / baseline.Mean;
            if (growth >= 0.20 && count - baseline.Mean >= 20)
            {
                anomalies.Add(new Anomaly
                {
                    TenantId = tenantId,
                    Fingerprint = Fingerprint(tenantId, "ResourceSprawl", "inventory.resource_count"),
                    Kind = AnomalyKind.ResourceSprawl,
                    Severity = growth >= 0.5 ? AnomalySeverity.High : AnomalySeverity.Medium,
                    Title = $"Resource count grew {growth:P0} above baseline ({baseline.Mean:F0} → {count})",
                    Description = "The tenant's resource count grew sharply against its rolling baseline. " +
                                  "Check for runaway automation, unapproved deployments, or misfiring IaC pipelines.",
                    MetricName = "inventory.resource_count",
                    ObservedValue = count,
                    BaselineMean = baseline.Mean,
                    BaselineStdDev = baseline.StdDev,
                    Score = growth,
                    DetectedAt = now,
                    LastSeenAt = now
                });
            }
        }

        // Exponentially-weighted baseline update (slow adaptation, ~5% per scan)
        var newMean = baseline == null ? count : baseline.Mean * 0.95 + count * 0.05;
        var newStd = baseline == null ? 0 : Math.Sqrt(0.95 * baseline.StdDev * baseline.StdDev + 0.05 * Math.Pow(count - newMean, 2));
        await _anomalyRepo.UpsertBaselineAsync(new MetricBaseline
        {
            TenantId = tenantId, MetricKey = "inventory.resource_count", WindowHours = 720,
            Mean = newMean, StdDev = newStd, SampleCount = (baseline?.SampleCount ?? 0) + 1
        });

        return anomalies;
    }

    // ------------------------------------------------------------------
    // Detector 6: cost-savings opportunity jump
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectCostAnomalyAsync(Guid tenantId, DateTime now)
    {
        var costRecs = await _recRepo.GetAdvisorRecommendationsAsync(
            tenantId, category: "Cost", status: RecommendationLifecycle.Active, limit: 500);
        var totalSavings = (double)costRecs.Where(r => r.EstimatedSavings.HasValue).Sum(r => r.EstimatedSavings!.Value);

        var baseline = await _anomalyRepo.GetBaselineAsync(tenantId, "advisor.cost.savings");
        var anomalies = new List<Anomaly>();

        if (baseline != null && totalSavings - baseline.Mean >= 1000 &&
            (baseline.Mean <= 0.01 || totalSavings / baseline.Mean >= 1.5))
        {
            anomalies.Add(new Anomaly
            {
                TenantId = tenantId,
                Fingerprint = Fingerprint(tenantId, "CostAnomaly", "advisor.cost.savings"),
                Kind = AnomalyKind.CostAnomaly,
                Severity = totalSavings - baseline.Mean >= 10000 ? AnomalySeverity.High : AnomalySeverity.Medium,
                Title = $"Identified waste jumped to ${totalSavings:N0}/yr (baseline ${baseline.Mean:N0}/yr)",
                Description = "Azure Advisor's estimated annual cost savings rose sharply, which usually means " +
                              "newly deployed over-provisioned resources or workloads left running. Review the " +
                              "cost recommendations and consider the proposed right-sizing actions.",
                MetricName = "advisor.cost.savings",
                ObservedValue = totalSavings,
                BaselineMean = baseline.Mean,
                Score = baseline.Mean > 0.01 ? totalSavings / baseline.Mean : totalSavings,
                DetectedAt = now,
                LastSeenAt = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    topOpportunities = costRecs
                        .Where(r => r.EstimatedSavings.HasValue)
                        .OrderByDescending(r => r.EstimatedSavings)
                        .Take(5)
                        .Select(r => new { r.Title, r.ResourceId, r.EstimatedSavings })
                })
            });
        }

        var newSavingsMean = baseline == null ? totalSavings : baseline.Mean * 0.9 + totalSavings * 0.1;
        await _anomalyRepo.UpsertBaselineAsync(new MetricBaseline
        {
            TenantId = tenantId, MetricKey = "advisor.cost.savings", WindowHours = 720,
            Mean = newSavingsMean, StdDev = 0, SampleCount = (baseline?.SampleCount ?? 0) + 1
        });

        return anomalies;
    }

    // ------------------------------------------------------------------
    // Detector 7: stale telemetry (the MSP watches the watcher)
    // ------------------------------------------------------------------
    private async Task<IEnumerable<Anomaly>> DetectStaleTelemetryAsync(Tenant tenant, DateTime now)
    {
        var latest = await _inventoryRepo.GetLatestSnapshotAsync(tenant.TenantId);
        if (latest?.CompletedAt == null)
            return []; // never snapshotted — onboarding state, not an anomaly

        var maxAge = TimeSpan.FromMinutes(Math.Max(tenant.SnapshotFrequencyMinutes * 2, 1440));
        var age = now - latest.CompletedAt.Value;
        if (age <= maxAge)
        {
            await _anomalyRepo.ResolveByFingerprintAsync(tenant.TenantId,
                Fingerprint(tenant.TenantId, "StaleTelemetry", "snapshot"));
            return [];
        }

        return new[]
        {
            new Anomaly
            {
                TenantId = tenant.TenantId,
                Fingerprint = Fingerprint(tenant.TenantId, "StaleTelemetry", "snapshot"),
                Kind = AnomalyKind.StaleTelemetry,
                Severity = age > maxAge * 2 ? AnomalySeverity.High : AnomalySeverity.Medium,
                Title = $"Inventory snapshot is stale ({age.TotalHours:F0}h old)",
                Description = $"The last completed snapshot finished {latest.CompletedAt:u}. Expected cadence is " +
                              $"every {tenant.SnapshotFrequencyMinutes} minutes. Check ARI automation, credentials " +
                              "(Lighthouse delegation / app registration secret expiry), and the ingestion queue.",
                MetricName = "snapshot.age.hours",
                ObservedValue = age.TotalHours,
                DetectedAt = now,
                LastSeenAt = now
            }
        };
    }

    // ------------------------------------------------------------------
    // Incident correlation + conservative auto-remediation proposals
    // ------------------------------------------------------------------

    private async Task EnsureIncidentAsync(Anomaly anomaly)
    {
        var existing = await _incidentRepo.FindOpenIncidentForFingerprintAsync(anomaly.TenantId, anomaly.Fingerprint);
        long incidentId;
        if (existing != null)
        {
            incidentId = existing.Id;
        }
        else
        {
            var slaHours = anomaly.Severity == AnomalySeverity.Critical ? 4 : 8;
            incidentId = await _incidentRepo.CreateAsync(new Incident
            {
                TenantId = anomaly.TenantId,
                Title = anomaly.Title,
                Severity = anomaly.Severity,
                Status = IncidentStatus.Open,
                Source = "anomaly",
                SummaryMarkdown = anomaly.Description,
                SlaDueAt = DateTime.UtcNow.AddHours(slaHours)
            });

            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = incidentId,
                TenantId = anomaly.TenantId,
                EventType = "created",
                Message = $"Incident opened automatically from {anomaly.Kind} anomaly.",
                ActorName = "aiops-engine"
            });
        }

        await _anomalyRepo.LinkToIncidentAsync(anomaly.TenantId, anomaly.Id, incidentId);
        anomaly.IncidentId = incidentId;

        await _incidentRepo.AddEventAsync(new IncidentEvent
        {
            IncidentId = incidentId,
            TenantId = anomaly.TenantId,
            EventType = "anomaly_linked",
            Message = $"Anomaly linked: {anomaly.Title}",
            ActorName = "aiops-engine"
        });
    }

    /// <summary>
    /// Auto-proposes remediations ONLY for unambiguous, reversible security drifts.
    /// Everything else goes through the AI assistant or a human operator.
    /// </summary>
    private async Task TryProposeRemediationAsync(Anomaly anomaly)
    {
        if (anomaly.Kind != AnomalyKind.ConfigurationDrift ||
            string.IsNullOrEmpty(anomaly.ResourceId) ||
            string.IsNullOrEmpty(anomaly.DetailsJson))
            return;

        try
        {
            var details = anomaly.DetailsJson;

            // Storage account made publicly readable → propose flipping it back.
            if (details.Contains("allowBlobPublicAccess", StringComparison.OrdinalIgnoreCase) &&
                anomaly.ResourceId.Contains("/Microsoft.Storage/storageAccounts/", StringComparison.OrdinalIgnoreCase))
            {
                await _remediationService.ProposeAsync(
                    anomaly.TenantId,
                    playbookKey: "azure-storage-disable-public-blob",
                    resourceId: anomaly.ResourceId,
                    title: $"Disable public blob access on {ShortResourceName(anomaly.ResourceId)}",
                    reason: $"Anomaly #{anomaly.Id}: security drift enabled public blob access. " +
                            "Reverting to the secure default.",
                    parametersJson: null,
                    requestedBy: "system:anomaly",
                    anomalyId: anomaly.Id,
                    incidentId: anomaly.IncidentId);
            }

            // HTTPS-only turned off on a storage account → propose re-enabling.
            if (details.Contains("supportsHttpsTrafficOnly", StringComparison.OrdinalIgnoreCase) &&
                anomaly.ResourceId.Contains("/Microsoft.Storage/storageAccounts/", StringComparison.OrdinalIgnoreCase))
            {
                await _remediationService.ProposeAsync(
                    anomaly.TenantId,
                    playbookKey: "azure-storage-require-https",
                    resourceId: anomaly.ResourceId,
                    title: $"Re-enable HTTPS-only on {ShortResourceName(anomaly.ResourceId)}",
                    reason: $"Anomaly #{anomaly.Id}: security drift disabled HTTPS-only transport.",
                    parametersJson: null,
                    requestedBy: "system:anomaly",
                    anomalyId: anomaly.Id,
                    incidentId: anomaly.IncidentId);
            }
        }
        catch (Exception ex)
        {
            // Proposals are best-effort; the anomaly itself is already recorded.
            _logger.LogWarning(ex, "Auto-proposal failed for anomaly {AnomalyId}", anomaly.Id);
        }
    }

    // ---- math + helpers ----

    private static (double Mean, double StdDev) MeanStdDev(IReadOnlyList<double> samples)
    {
        var mean = samples.Average();
        var variance = samples.Sum(s => Math.Pow(s - mean, 2)) / samples.Count;
        return (mean, Math.Sqrt(variance));
    }

    private static string Fingerprint(Guid tenantId, string kind, string scope)
    {
        var input = $"{tenantId}:{kind}:{scope}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..32].ToLowerInvariant();
    }

    private static string ShortResourceName(string resourceId)
    {
        var idx = resourceId.LastIndexOf('/');
        return idx >= 0 && idx < resourceId.Length - 1 ? resourceId[(idx + 1)..] : resourceId;
    }
}
