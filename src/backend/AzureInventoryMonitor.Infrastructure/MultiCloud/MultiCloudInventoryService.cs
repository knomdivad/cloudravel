using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.MultiCloud;

/// <summary>
/// Orchestrates AWS/GCP inventory collection.
///
/// Merge strategy: non-Azure resources are attached to the tenant's LATEST
/// snapshot (replacing that provider's previous rows), so the inventory
/// explorer, dashboards, and AI tools see a single merged multi-cloud view.
/// If the tenant has no snapshot yet (e.g. AWS-only tenant), a snapshot is
/// created to host the rows.
/// </summary>
public sealed class MultiCloudInventoryService : IMultiCloudInventoryService
{
    private readonly ICloudAccountRepository _accountRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly ICloudProviderAdapterFactory _adapterFactory;
    private readonly ILogger<MultiCloudInventoryService> _logger;

    public MultiCloudInventoryService(
        ICloudAccountRepository accountRepo,
        IInventoryRepository inventoryRepo,
        ICloudProviderAdapterFactory adapterFactory,
        ILogger<MultiCloudInventoryService> logger)
    {
        _accountRepo = accountRepo;
        _inventoryRepo = inventoryRepo;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    public async Task<int> SyncAccountAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        if (account.Provider == CloudProvider.Azure)
            return 0; // Azure flows through the first-class Resource Graph pipeline

        try
        {
            var adapter = _adapterFactory.GetAdapter(account.Provider);
            var resources = await adapter.CollectInventoryAsync(account, cancellationToken);

            var latest = await _inventoryRepo.GetLatestSnapshotAsync(account.TenantId);
            long snapshotId;
            if (latest == null)
            {
                var snapshot = await _inventoryRepo.CreateSnapshotAsync(account.TenantId, "multicloud-sync");
                snapshotId = snapshot.SnapshotId;
                foreach (var r in resources) r.SnapshotId = snapshotId;
                await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, resources);
                await _inventoryRepo.CompleteSnapshotAsync(snapshotId, resources.Count, $"multicloud:{account.Provider}");
                await _inventoryRepo.SetLatestSnapshotAsync(account.TenantId, snapshotId);
            }
            else
            {
                snapshotId = latest.SnapshotId;
                var providerKey = account.Provider.ToString().ToLowerInvariant();
                await _inventoryRepo.DeleteResourcesByProviderAsync(snapshotId, providerKey);
                foreach (var r in resources) r.SnapshotId = snapshotId;
                await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, resources);
            }

            await _accountRepo.TouchInventoryAsync(account.AccountId, DateTime.UtcNow);
            if (account.Status != CloudAccountStatus.Connected)
                await _accountRepo.UpdateStatusAsync(account.AccountId, CloudAccountStatus.Connected, null);

            _logger.LogInformation("Multi-cloud sync: {Count} {Provider} resources merged into snapshot {SnapshotId} for tenant {TenantId}",
                resources.Count, account.Provider, snapshotId, account.TenantId);
            return resources.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Multi-cloud sync failed for {Provider} account {ExternalId} (tenant {TenantId})",
                account.Provider, account.ExternalId, account.TenantId);
            await _accountRepo.UpdateStatusAsync(account.AccountId, CloudAccountStatus.Degraded, ex.Message);
            throw;
        }
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _accountRepo.GetAllActiveAsync();
        foreach (var account in accounts.Where(a => a.Provider != CloudProvider.Azure))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncAccountAsync(account, cancellationToken);
            }
            catch
            {
                // Logged + status recorded in SyncAccountAsync; keep going.
            }

            // Spread provider API load
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}
