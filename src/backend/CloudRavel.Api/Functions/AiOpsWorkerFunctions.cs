using CloudRavel.Core.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Functions;

/// <summary>
/// Timer-triggered AIOps workers: proactive anomaly scanning, the gated
/// remediation execution queue, and multi-cloud inventory sync.
/// </summary>
public sealed class AiOpsWorkerFunctions
{
    private readonly IAnomalyDetectionService _anomalyDetection;
    private readonly IRemediationService _remediationService;
    private readonly IMultiCloudInventoryService _multiCloudInventory;
    private readonly IMultiCloudGovernanceSyncService _multiCloudGovernance;
    private readonly ITenantRepository _tenantRepo;
    private readonly IPlatformInfo _platform;
    private readonly ILogger<AiOpsWorkerFunctions> _logger;

    public AiOpsWorkerFunctions(
        IAnomalyDetectionService anomalyDetection,
        IRemediationService remediationService,
        IMultiCloudInventoryService multiCloudInventory,
        IMultiCloudGovernanceSyncService multiCloudGovernance,
        ITenantRepository tenantRepo,
        IPlatformInfo platform,
        ILogger<AiOpsWorkerFunctions> logger)
    {
        _anomalyDetection = anomalyDetection;
        _remediationService = remediationService;
        _multiCloudInventory = multiCloudInventory;
        _multiCloudGovernance = multiCloudGovernance;
        _tenantRepo = tenantRepo;
        _platform = platform;
        _logger = logger;
    }

    /// <summary>
    /// Proactive anomaly scan every 15 minutes, offset from change polling
    /// (:05/:20/:35/:50) so each scan sees the freshest change data.
    /// </summary>
    [Function("AnomalyScanTimer")]
    public async Task ScanAnomalies([TimerTrigger("0 5-59/15 * * * *")] TimerInfo timer)
    {
        var tenants = await _tenantRepo.GetAllActiveAsync();
        var totalNew = 0;

        foreach (var tenant in tenants)
        {
            try
            {
                var newAnomalies = await _anomalyDetection.ScanTenantAsync(tenant.TenantId);
                totalNew += newAnomalies.Count;
            }
            catch (Exception ex)
            {
                // One tenant's failure must not block the rest of the fleet.
                _logger.LogError(ex, "Anomaly scan failed for tenant {TenantId}", tenant.TenantId);
            }
        }

        if (totalNew > 0)
            _logger.LogInformation("Anomaly scan complete: {New} new anomalies across {Tenants} tenants",
                totalNew, tenants.Count);
    }

    /// <summary>
    /// Drains the approved-remediation queue every 5 minutes. Actions approved
    /// via the API execute inline; this catches auto-approved actions and any
    /// approval whose inline execution was interrupted.
    /// </summary>
    [Function("RemediationQueueTimer")]
    public async Task DrainRemediationQueue([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        try
        {
            var executed = await _remediationService.ExecuteApprovedActionsAsync();
            if (executed > 0)
                _logger.LogInformation("Remediation queue drained: {Count} action(s) executed", executed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remediation queue drain failed");
        }
    }

    /// <summary>
    /// Multi-cloud inventory sync daily at 3 AM UTC — one hour after the Azure
    /// snapshot so AWS/GCP rows merge into the fresh latest snapshot.
    /// </summary>
    [Function("MultiCloudInventoryTimer")]
    public async Task SyncMultiCloudInventory([TimerTrigger("0 0 3 * * *")] TimerInfo timer)
    {
        if (!_platform.IsProduction)
        {
            _logger.LogInformation("Skipping scheduled AWS/GCP inventory sync — instance environment is {Env}.", _platform.Environment);
            return;
        }

        _logger.LogInformation("Multi-cloud inventory sync started at {Time}", DateTime.UtcNow);
        await _multiCloudInventory.SyncAllAsync();
        _logger.LogInformation("Multi-cloud inventory sync completed");
    }

    /// <summary>
    /// AWS/GCP Security Hub · Config · Trusted Advisor · SCC · Recommender · Org Policy
    /// sync — hourly, offset from Azure Advisor/Policy/Defender timers.
    /// </summary>
    [Function("MultiCloudGovernanceTimer")]
    public async Task SyncMultiCloudGovernance([TimerTrigger("0 20 * * * *")] TimerInfo timer)
    {
        if (!_platform.IsProduction)
        {
            _logger.LogInformation("Skipping multi-cloud governance sync — instance environment is {Env}.", _platform.Environment);
            return;
        }

        _logger.LogInformation("Multi-cloud governance sync started at {Time}", DateTime.UtcNow);
        await _multiCloudGovernance.SyncAllAsync();
        _logger.LogInformation("Multi-cloud governance sync completed");
    }
}
