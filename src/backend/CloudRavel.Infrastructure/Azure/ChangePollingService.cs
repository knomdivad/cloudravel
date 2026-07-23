using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Azure;

/// <summary>
/// Polls Azure Resource Graph for resource changes.
/// 
/// Uses the resourcechanges and resourcecontainerchanges tables.
/// Change History is limited to ~14 days, so this service is used
/// only for short-term delta detection — NOT as a replacement for ARI snapshots.
/// 
/// Changes are classified based on a rules engine:
///   - NSG rule changes → Security
///   - Tag changes → Governance
///   - SKU changes → Cost
///   - Everything else → Operational
/// </summary>
public sealed class ChangePollingService : IChangePollingService
{
    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly IChangeRepository _changeRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly ILogger<ChangePollingService> _logger;

    // Properties that indicate a security-impacting change
    private static readonly HashSet<string> SecurityProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "properties.securityRules",
        "properties.networkSecurityGroup",
        "properties.firewallRules",
        "properties.encryption",
        "properties.httpsOnly",
        "properties.minimumTlsVersion",
        "properties.publicNetworkAccess",
        "properties.networkAcls",
        "properties.identity",
        "properties.siteConfig.ftpsState",
        "properties.siteConfig.http20Enabled",
        "identity"
    };

    // Properties that indicate a cost-impacting change
    private static readonly HashSet<string> CostProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "sku",
        "sku.name",
        "sku.tier",
        "sku.capacity",
        "properties.hardwareProfile.vmSize",
        "properties.sku",
        "properties.reserved",
        "properties.autoscaleSettings"
    };

    // Properties that indicate a governance change
    private static readonly HashSet<string> GovernanceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "tags",
        "properties.roleAssignments",
        "properties.policyAssignments",
        "location"
    };

    public ChangePollingService(
        IAzureCredentialFactory credentialFactory,
        IChangeRepository changeRepo,
        ITenantRepository tenantRepo,
        ILogger<ChangePollingService> logger)
    {
        _credentialFactory = credentialFactory;
        _changeRepo = changeRepo;
        _tenantRepo = tenantRepo;
        _logger = logger;
    }

    public async Task PollChangesAsync(Guid tenantId, DateTime? fromOverride = null)
    {
        var tenant = await _tenantRepo.GetByIdAsync(tenantId)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
        var armClient = new ArmClient(credential);

        // Query window: last (pollFrequency + 5 min buffer) or from override
        var to = DateTime.UtcNow;
        var from = fromOverride ?? to.AddMinutes(-(tenant.ChangePollFrequencyMinutes + 5));

        _logger.LogInformation(
            "Polling changes for tenant {TenantId} from {From} to {To}",
            tenantId, from, to);

        var changes = new List<ResourceChange>();

        // Query Resource Graph Change History
        var query = BuildChangeQuery(from, to);
        var tenantResource = armClient.GetTenants().GetAll().First();

        try
        {
            var request = new ResourceQueryContent(query)
            {
                Options = new ResourceQueryRequestOptions { ResultFormat = ResultFormat.ObjectArray }
            };

            var response = await tenantResource.GetResourcesAsync(request);
            using var resultDoc = JsonDocument.Parse(response.Value.Data);
            var resultData = resultDoc.RootElement;

            if (resultData.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in resultData.EnumerateArray())
                {
                    var change = ParseChange(tenantId, element);
                    if (change != null)
                    {
                        changes.Add(change);
                    }
                }
            }

            _logger.LogInformation(
                "Found {Count} changes for tenant {TenantId} in window {From} — {To}",
                changes.Count, tenantId, from, to);

            if (changes.Count > 0)
            {
                await _changeRepo.UpsertChangesAsync(changes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to poll changes for tenant {TenantId}", tenantId);
            throw;
        }
    }

    private static string BuildChangeQuery(DateTime from, DateTime to)
    {
        // Resource Graph KQL query for change history
        return $@"
            resourcechanges
            | where properties.changeAttributes.timestamp >= datetime('{from:yyyy-MM-ddTHH:mm:ssZ}')
                and properties.changeAttributes.timestamp <= datetime('{to:yyyy-MM-ddTHH:mm:ssZ}')
            | extend changeId = properties.changeAttributes.correlationId,
                     changeType = tostring(properties.changeType),
                     targetResourceId = tostring(properties.targetResourceId),
                     targetResourceType = tostring(properties.targetResourceType),
                     changeTime = todatetime(properties.changeAttributes.timestamp),
                     changedBy = tostring(properties.changeAttributes.changedBy),
                     changedByType = tostring(properties.changeAttributes.changedByType),
                     clientType = tostring(properties.changeAttributes.clientType),
                     changes = properties.changes
            | project changeId, changeType, targetResourceId, targetResourceType,
                      changeTime, changedBy, changedByType, clientType, changes
            | order by changeTime desc
            | take 1000";
    }

    private ResourceChange? ParseChange(Guid tenantId, JsonElement element)
    {
        try
        {
            var changeId = element.GetProperty("changeId").GetString() ?? Guid.NewGuid().ToString();
            var resourceId = element.GetProperty("targetResourceId").GetString() ?? "";
            var resourceType = element.GetProperty("targetResourceType").GetString() ?? "";
            var changeTypeStr = element.GetProperty("changeType").GetString() ?? "Update";
            var changeTime = element.GetProperty("changeTime").GetDateTime();
            var changedBy = element.TryGetProperty("changedBy", out var cb) ? cb.GetString() : null;
            var changedByType = element.TryGetProperty("changedByType", out var cbt) ? cbt.GetString() : null;
            var clientType = element.TryGetProperty("clientType", out var ct) ? ct.GetString() : null;

            var propertyChanges = new List<PropertyChange>();
            if (element.TryGetProperty("changes", out var changesNode) && changesNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in changesNode.EnumerateObject())
                {
                    var before = prop.Value.TryGetProperty("previousValue", out var pv) ? pv.ToString() : null;
                    var after = prop.Value.TryGetProperty("newValue", out var nv) ? nv.ToString() : null;

                    propertyChanges.Add(new PropertyChange
                    {
                        Path = prop.Name,
                        Before = before,
                        After = after
                    });
                }
            }

            var classification = ClassifyChange(propertyChanges);
            var severity = DetermineSeverity(classification, changeTypeStr);

            return new ResourceChange
            {
                TenantId = tenantId,
                ChangeId = changeId,
                ResourceId = resourceId,
                ResourceType = resourceType,
                ChangeType = Enum.TryParse<ChangeType>(changeTypeStr, true, out var ct2) ? ct2 : ChangeType.Update,
                DetectedAt = changeTime,
                IngestedAt = DateTime.UtcNow,
                ChangedProperties = propertyChanges,
                ActorType = changedByType,
                ActorId = changedBy,
                ActorName = changedBy,  // Will be resolved separately if needed
                ClientType = clientType,
                Classification = classification,
                Severity = severity
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse change element");
            return null;
        }
    }

    private static ChangeClassification ClassifyChange(List<PropertyChange> changes)
    {
        foreach (var change in changes)
        {
            if (SecurityProperties.Any(sp => change.Path.Contains(sp, StringComparison.OrdinalIgnoreCase)))
                return ChangeClassification.Security;
        }

        foreach (var change in changes)
        {
            if (CostProperties.Any(cp => change.Path.Contains(cp, StringComparison.OrdinalIgnoreCase)))
                return ChangeClassification.Cost;
        }

        foreach (var change in changes)
        {
            if (GovernanceProperties.Any(gp => change.Path.Contains(gp, StringComparison.OrdinalIgnoreCase)))
                return ChangeClassification.Governance;
        }

        return ChangeClassification.Operational;
    }

    private static ChangeSeverity DetermineSeverity(ChangeClassification classification, string changeType)
    {
        if (changeType.Equals("Delete", StringComparison.OrdinalIgnoreCase))
            return ChangeSeverity.High;

        return classification switch
        {
            ChangeClassification.Security => ChangeSeverity.High,
            ChangeClassification.Cost => ChangeSeverity.Medium,
            ChangeClassification.Governance => ChangeSeverity.Medium,
            ChangeClassification.Operational => ChangeSeverity.Low,
            _ => ChangeSeverity.Info
        };
    }
}
