using System.Text.Json;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.MultiCloud;

/// <summary>
/// Orchestrates AWS/GCP inventory collection.
///
/// Merge strategy: non-Azure resources are attached to the tenant's LATEST
/// snapshot (replacing that account's previous rows), so the inventory
/// explorer, dashboards, and AI tools see a single merged multi-cloud view.
/// If the tenant has no snapshot yet (e.g. AWS-only tenant), a snapshot is
/// created to host the rows.
///
/// Change detection: AWS and GCP have no native change-history API like Azure
/// Resource Graph Change History, so changes are derived by diffing each
/// account's previous resource set against the newly collected one — the
/// same resource_changes table Azure's ChangePollingService populates, so
/// the change-driven anomaly detectors (velocity spike, config drift, unusual
/// actor, security regression) work against AWS/GCP resources too.
/// </summary>
public sealed class MultiCloudInventoryService : IMultiCloudInventoryService
{
    private readonly ICloudAccountRepository _accountRepo;
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IChangeRepository _changeRepo;
    private readonly ICloudProviderAdapterFactory _adapterFactory;
    private readonly ILogger<MultiCloudInventoryService> _logger;

    // Property-name keywords that indicate a security-impacting change. Matched
    // against top-level JSON property keys (GCP's Cloud Asset properties) — AWS's
    // Tagging API doesn't return deep config, so only tags/location are available
    // for it; this set only ever matches for GCP resources today.
    private static readonly string[] SecurityKeywords =
    {
        "public", "encrypt", "iam", "policy", "acl", "https", "tls",
        "securitygroup", "firewall", "ingress", "egress", "role", "auth"
    };

    // Property-name keywords that indicate a cost-impacting change (instance/machine
    // sizing). Same GCP-only caveat as above.
    private static readonly string[] CostKeywords =
    {
        "instancetype", "machinetype", "sku", "size", "tier", "capacity", "class"
    };

    public MultiCloudInventoryService(
        ICloudAccountRepository accountRepo,
        IInventoryRepository inventoryRepo,
        IChangeRepository changeRepo,
        ICloudProviderAdapterFactory adapterFactory,
        ILogger<MultiCloudInventoryService> logger)
    {
        _accountRepo = accountRepo;
        _inventoryRepo = inventoryRepo;
        _changeRepo = changeRepo;
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
            var providerKey = account.Provider.ToString().ToLowerInvariant();

            var latest = await _inventoryRepo.GetLatestSnapshotAsync(account.TenantId);
            long snapshotId;
            IReadOnlyList<InventoryResource> previous = Array.Empty<InventoryResource>();

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
                // Capture this account's current rows BEFORE replacing them, so the
                // new set can be diffed against them below.
                previous = await _inventoryRepo.GetResourcesBySubscriptionAsync(account.TenantId, snapshotId, account.ExternalId);

                await _inventoryRepo.DeleteResourcesByProviderAsync(snapshotId, providerKey, account.ExternalId);
                foreach (var r in resources) r.SnapshotId = snapshotId;
                await _inventoryRepo.BulkInsertResourcesAsync(snapshotId, resources);
            }

            try
            {
                await DetectAndRecordChangesAsync(account.TenantId, previous, resources);
            }
            catch (Exception ex)
            {
                // Change detection is a best-effort enrichment — never fail the
                // inventory sync itself because of it.
                _logger.LogWarning(ex, "Change detection failed for {Provider} account {ExternalId} (tenant {TenantId})",
                    account.Provider, account.ExternalId, account.TenantId);
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

    /// <summary>
    /// Diffs one account's previous resource set against the newly collected one
    /// and writes Create/Update/Delete rows to resource_changes.
    /// </summary>
    private async Task DetectAndRecordChangesAsync(
        Guid tenantId, IReadOnlyList<InventoryResource> previous, IReadOnlyList<InventoryResource> current)
    {
        if (previous.Count == 0 && current.Count == 0) return;

        var previousById = previous.ToDictionary(r => r.ResourceId, StringComparer.OrdinalIgnoreCase);
        var currentById = current.ToDictionary(r => r.ResourceId, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changes = new List<ResourceChange>();

        foreach (var (resourceId, resource) in currentById)
        {
            if (!previousById.TryGetValue(resourceId, out var before))
            {
                changes.Add(new ResourceChange
                {
                    TenantId = tenantId,
                    ChangeId = Guid.NewGuid().ToString(),
                    ResourceId = resourceId,
                    ResourceType = resource.ResourceType,
                    ChangeType = ChangeType.Create,
                    DetectedAt = now,
                    IngestedAt = now,
                    Classification = ChangeClassification.Operational,
                    Severity = ChangeSeverity.Low
                });
                continue;
            }

            var propertyChanges = DiffProperties(before, resource);
            if (propertyChanges.Count == 0) continue;

            var classification = ClassifyChange(propertyChanges);
            changes.Add(new ResourceChange
            {
                TenantId = tenantId,
                ChangeId = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                ResourceType = resource.ResourceType,
                ChangeType = ChangeType.Update,
                DetectedAt = now,
                IngestedAt = now,
                ChangedProperties = propertyChanges,
                Classification = classification,
                Severity = classification switch
                {
                    ChangeClassification.Security => ChangeSeverity.High,
                    ChangeClassification.Cost => ChangeSeverity.Medium,
                    ChangeClassification.Governance => ChangeSeverity.Medium,
                    _ => ChangeSeverity.Low
                }
            });
        }

        foreach (var (resourceId, before) in previousById)
        {
            if (currentById.ContainsKey(resourceId)) continue;
            changes.Add(new ResourceChange
            {
                TenantId = tenantId,
                ChangeId = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                ResourceType = before.ResourceType,
                ChangeType = ChangeType.Delete,
                DetectedAt = now,
                IngestedAt = now,
                Classification = ChangeClassification.Operational,
                Severity = ChangeSeverity.High
            });
        }

        if (changes.Count > 0)
        {
            await _changeRepo.UpsertChangesAsync(changes);
            _logger.LogInformation("Detected {Count} change(s) for tenant {TenantId} via multi-cloud diff", changes.Count, tenantId);
        }
    }

    /// <summary>
    /// Compares tags, location, and (when both sides have it — currently GCP only,
    /// since AWS's Tagging API returns no deep properties) top-level property keys.
    /// </summary>
    private static List<PropertyChange> DiffProperties(InventoryResource before, InventoryResource after)
    {
        var changes = new List<PropertyChange>();

        if (!TagsEqual(before.Tags, after.Tags))
            changes.Add(new PropertyChange { Path = "tags", Before = SerializeTags(before.Tags), After = SerializeTags(after.Tags) });

        if (!string.Equals(before.Location, after.Location, StringComparison.OrdinalIgnoreCase))
            changes.Add(new PropertyChange { Path = "location", Before = before.Location, After = after.Location });

        if (!string.IsNullOrEmpty(before.PropertiesJson) && !string.IsNullOrEmpty(after.PropertiesJson))
            changes.AddRange(DiffTopLevelProperties(before.PropertiesJson, after.PropertiesJson));

        return changes;
    }

    private static bool TagsEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        a ??= new(); b ??= new();
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    private static string? SerializeTags(Dictionary<string, string>? tags) =>
        tags is { Count: > 0 } ? JsonSerializer.Serialize(tags) : null;

    private static List<PropertyChange> DiffTopLevelProperties(string beforeJson, string afterJson)
    {
        var result = new List<PropertyChange>();
        try
        {
            using var beforeDoc = JsonDocument.Parse(beforeJson);
            using var afterDoc = JsonDocument.Parse(afterJson);
            if (beforeDoc.RootElement.ValueKind != JsonValueKind.Object || afterDoc.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            var beforeProps = beforeDoc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
            var afterProps = afterDoc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);

            foreach (var key in beforeProps.Keys.Union(afterProps.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var hasBefore = beforeProps.TryGetValue(key, out var beforeVal);
                var hasAfter = afterProps.TryGetValue(key, out var afterVal);
                if (hasBefore && hasAfter && beforeVal == afterVal) continue;
                result.Add(new PropertyChange { Path = key, Before = hasBefore ? beforeVal : null, After = hasAfter ? afterVal : null });
            }
        }
        catch (JsonException)
        {
            // Malformed/unexpected properties shape — tags/location diffs above still apply.
        }
        return result;
    }

    private static ChangeClassification ClassifyChange(List<PropertyChange> changes)
    {
        foreach (var c in changes)
            if (SecurityKeywords.Any(k => c.Path.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return ChangeClassification.Security;

        foreach (var c in changes)
            if (CostKeywords.Any(k => c.Path.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return ChangeClassification.Cost;

        foreach (var c in changes)
            if (c.Path.Equals("tags", StringComparison.OrdinalIgnoreCase) || c.Path.Equals("location", StringComparison.OrdinalIgnoreCase))
                return ChangeClassification.Governance;

        return ChangeClassification.Operational;
    }
}
