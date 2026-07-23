using System.Text.Json;
using AzureInventoryMonitor.Core.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// Timer-triggered background workers. These functions execute the scheduled
/// data collection pipelines.
/// </summary>
public sealed class WorkerFunctions
{
    private readonly IChangePollingService _changePolling;
    private readonly IRecommendationSyncService _recSync;
    private readonly IAriIngestionService _ariIngestion;
    private readonly IInventoryCollectionService _inventoryCollection;
    private readonly ITenantRepository _tenantRepo;
    private readonly IJobQueue _jobQueue;
    private readonly IPlatformInfo _platform;
    private readonly ILogger<WorkerFunctions> _logger;

    public WorkerFunctions(
        IChangePollingService changePolling,
        IRecommendationSyncService recSync,
        IAriIngestionService ariIngestion,
        IInventoryCollectionService inventoryCollection,
        ITenantRepository tenantRepo,
        IJobQueue jobQueue,
        IPlatformInfo platform,
        ILogger<WorkerFunctions> logger)
    {
        _changePolling = changePolling;
        _recSync = recSync;
        _ariIngestion = ariIngestion;
        _inventoryCollection = inventoryCollection;
        _tenantRepo = tenantRepo;
        _jobQueue = jobQueue;
        _platform = platform;
        _logger = logger;
    }

    /// <summary>
    /// Polls Azure Resource Graph Change History every 15 minutes.
    /// Iterates over all active tenants and collects recent changes.
    /// 
    /// Why 15 minutes? 
    ///   - Resource Graph Change History has near-real-time indexing (~5 min lag)
    ///   - 15-min polls with 20-min lookback windows provide overlap for missed events
    ///   - More frequent polling hits throttling limits
    /// </summary>
    [Function("PollChangesTimer")]
    public async Task PollChanges([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Change polling timer fired at {Time}", DateTime.UtcNow);

        var tenants = await _tenantRepo.GetAllActiveAsync();
        var tasks = new List<Task>();

        foreach (var tenant in tenants)
        {
            tasks.Add(PollTenantChangesAsync(tenant.TenantId));
        }

        // Process tenants in parallel (bounded by Function host concurrency)
        await Task.WhenAll(tasks);

        _logger.LogInformation("Change polling completed for {Count} tenants", tenants.Count);
    }

    private async Task PollTenantChangesAsync(Guid tenantId)
    {
        try
        {
            await _changePolling.PollChangesAsync(tenantId);
        }
        catch (Exception ex)
        {
            // Don't fail the entire batch if one tenant fails
            _logger.LogError(ex, "Change polling failed for tenant {TenantId}", tenantId);
        }
    }

    /// <summary>
    /// Syncs Azure Advisor recommendations every hour at :00.
    /// Split into a separate timer so it gets its own 10-minute Consumption plan budget.
    /// </summary>
    [Function("SyncAdvisorTimer")]
    public async Task SyncAdvisor([TimerTrigger("0 0 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Advisor sync timer fired at {Time}", DateTime.UtcNow);
        var tenants = await _tenantRepo.GetAllActiveAsync();

        foreach (var tenant in tenants)
        {
            try
            {
                await _recSync.SyncAdvisorAsync(tenant.TenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advisor sync failed for tenant {TenantId}", tenant.TenantId);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Syncs Azure Policy compliance every hour at :05.
    /// </summary>
    [Function("SyncPolicyTimer")]
    public async Task SyncPolicy([TimerTrigger("0 5 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Policy sync timer fired at {Time}", DateTime.UtcNow);
        var tenants = await _tenantRepo.GetAllActiveAsync();

        foreach (var tenant in tenants)
        {
            try
            {
                await _recSync.SyncPolicyComplianceAsync(tenant.TenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Policy sync failed for tenant {TenantId}", tenant.TenantId);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Syncs Microsoft Defender for Cloud findings every hour at :10.
    /// </summary>
    [Function("SyncDefenderTimer")]
    public async Task SyncDefender([TimerTrigger("0 10 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Defender sync timer fired at {Time}", DateTime.UtcNow);
        var tenants = await _tenantRepo.GetAllActiveAsync();

        foreach (var tenant in tenants)
        {
            try
            {
                await _recSync.SyncDefenderFindingsAsync(tenant.TenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Defender sync failed for tenant {TenantId}", tenant.TenantId);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Collects a full inventory snapshot from Azure Resource Graph daily at 2 AM UTC.
    /// Iterates over all active tenants sequentially to avoid API throttling.
    /// </summary>
    [Function("CollectInventoryTimer")]
    public async Task CollectInventory([TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        if (!_platform.IsProduction)
        {
            _logger.LogInformation("Skipping scheduled Azure inventory collection — instance environment is {Env}.", _platform.Environment);
            return;
        }

        _logger.LogInformation("Daily inventory collection timer fired at {Time}", DateTime.UtcNow);

        var tenants = await _tenantRepo.GetAllActiveAsync();

        foreach (var tenant in tenants)
        {
            try
            {
                _logger.LogInformation("Collecting inventory for tenant {TenantId} ({Name})",
                    tenant.TenantId, tenant.DisplayName);

                var (snapshotId, resourceCount) = await _inventoryCollection.CollectInventoryAsync(tenant.TenantId, "schedule");

                _logger.LogInformation("Inventory collection completed for tenant {TenantId}: snapshot {SnapshotId} with {Count} resources",
                    tenant.TenantId, snapshotId, resourceCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory collection failed for tenant {TenantId}", tenant.TenantId);
            }

            // Delay between tenants to spread Azure API load
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        _logger.LogInformation("Daily inventory collection completed for {Count} tenants", tenants.Count);
    }

    /// <summary>
    /// Drains the "snapshot-ingestion" queue every minute. Fed by the ARI
    /// Automation runbook (via IJobQueue.EnqueueAsync, or directly against
    /// Service Bus when that's the active queue — see automation/Invoke-AriSnapshot.ps1)
    /// once it uploads output to Blob Storage. Polling the abstraction instead
    /// of a native [ServiceBusTrigger] binding means the Functions host never
    /// hard-depends on Service Bus just to start: DatabaseJobQueue (the
    /// default when no Service Bus connection is configured) needs nothing
    /// beyond the SQL database every deployment already requires.
    /// </summary>
    [Function("SnapshotIngestionQueueTimer")]
    public async Task ProcessSnapshotQueue([TimerTrigger("0 * * * * *")] TimerInfo timer)
    {
        var jobs = await _jobQueue.DequeueBatchAsync("snapshot-ingestion", maxCount: 20);
        if (jobs.Count == 0) return;

        foreach (var job in jobs)
        {
            SnapshotMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<SnapshotMessage>(job.PayloadJson);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Malformed snapshot message (job {JobId}), discarding", job.Id);
                await _jobQueue.CompleteAsync(job);
                continue;
            }

            if (message == null)
            {
                await _jobQueue.CompleteAsync(job);
                continue;
            }

            await ProcessSnapshotMessageAsync(job, message);
        }
    }

    private async Task ProcessSnapshotMessageAsync(Core.Models.QueuedJob job, SnapshotMessage message)
    {
        _logger.LogInformation("Processing snapshot message: type={Type}, tenant={TenantId}, blob={BlobPath}",
            message.Type, message.TenantId, message.BlobPath);

        if (message.Type == "snapshot-failed")
        {
            _logger.LogError("Snapshot failed for tenant {TenantId}: {Error}", message.TenantId, message.Error);
            await _jobQueue.CompleteAsync(job);
            return;
        }

        if (message.Type != "snapshot-ready")
        {
            _logger.LogWarning("Unknown message type: {Type}", message.Type);
            await _jobQueue.CompleteAsync(job);
            return;
        }

        if (string.IsNullOrEmpty(message.BlobPath) || !Guid.TryParse(message.TenantId, out var tenantId))
        {
            _logger.LogError("Snapshot-ready message missing BlobPath or has an invalid tenant ID: {TenantId}", message.TenantId);
            await _jobQueue.CompleteAsync(job);
            return;
        }

        try
        {
            var snapshotId = await _ariIngestion.IngestSnapshotAsync(tenantId, message.BlobPath, "schedule");
            await _jobQueue.CompleteAsync(job);
            _logger.LogInformation("Snapshot ingestion completed: {SnapshotId} for tenant {TenantId}",
                snapshotId, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot ingestion failed for tenant {TenantId} from {BlobPath}",
                message.TenantId, message.BlobPath);
            await _jobQueue.FailAsync(job, ex.Message, TimeSpan.FromMinutes(5));
        }
    }
}

/// <summary>
/// Service Bus message schema for snapshot events.
/// </summary>
public sealed class SnapshotMessage
{
    public string Type { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? AzureTenantId { get; set; }
    public string? BlobPath { get; set; }
    public string? Timestamp { get; set; }
    public List<string>? ResourceFiles { get; set; }
    public string? Error { get; set; }
}
