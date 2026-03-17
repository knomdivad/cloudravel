using Azure.ResourceManager;
using Azure.ResourceManager.Advisor;
using Azure.ResourceManager.PolicyInsights;
using Azure.ResourceManager.PolicyInsights.Models;
using Azure.ResourceManager.SecurityCenter;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.Azure;

/// <summary>
/// Synchronizes recommendations from Azure Advisor, Azure Policy, and Microsoft Defender for Cloud.
/// 
/// Each sync operation:
///   1. Authenticates to the customer tenant
///   2. Iterates over all subscriptions in scope
///   3. Pulls recommendations/compliance/findings via Azure SDK
///   4. Normalizes into the unified schema
///   5. Upserts into the database (detecting new/resolved transitions)
/// </summary>
public sealed class RecommendationSyncService : IRecommendationSyncService
{
    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly ITenantRepository _tenantRepo;
    private readonly IRecommendationRepository _recommendationRepo;
    private readonly ILogger<RecommendationSyncService> _logger;

    public RecommendationSyncService(
        IAzureCredentialFactory credentialFactory,
        ITenantRepository tenantRepo,
        IRecommendationRepository recommendationRepo,
        ILogger<RecommendationSyncService> logger)
    {
        _credentialFactory = credentialFactory;
        _tenantRepo = tenantRepo;
        _recommendationRepo = recommendationRepo;
        _logger = logger;
    }

    /// <summary>
    /// Sync Azure Advisor recommendations for a tenant across all subscriptions.
    /// </summary>
    public async Task SyncAdvisorAsync(Guid tenantId)
    {
        var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
        var armClient = new ArmClient(credential);
        var recommendations = new List<AdvisorRecommendation>();

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            _logger.LogDebug("Syncing Advisor recommendations for subscription {SubId}", sub.Data.SubscriptionId);

            await foreach (var rec in armClient.GetResourceRecommendationBases(sub.Id).GetAllAsync())
            {
                var data = rec.Data;
                recommendations.Add(new AdvisorRecommendation
                {
                    TenantId = tenantId,
                    RecommendationId = data.Id?.ToString() ?? "",
                    ResourceId = data.ResourceMetadata?.ResourceId?.ToString(),
                    Category = data.Category?.ToString() ?? "Unknown",
                    Impact = data.Impact?.ToString() ?? "Unknown",
                    Title = data.ShortDescription?.Problem ?? data.Label ?? "Untitled",
                    Description = data.ShortDescription?.Solution,
                    RemediationAction = data.ExtendedProperties?.TryGetValue("annot_solution", out var solution) == true
                        ? solution : null,
                    EstimatedSavings = ParseSavings(data.ExtendedProperties),
                    LastSeenAt = DateTime.UtcNow,
                    LifecycleStatus = RecommendationLifecycle.Active
                });
            }
        }

        _logger.LogInformation("Synced {Count} Advisor recommendations for tenant {TenantId}",
            recommendations.Count, tenantId);

        await _recommendationRepo.UpsertAdvisorRecommendationsAsync(recommendations);
    }

    /// <summary>
    /// Sync Azure Policy compliance states for a tenant.
    /// </summary>
    public async Task SyncPolicyComplianceAsync(Guid tenantId)
    {
        var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
        var armClient = new ArmClient(credential);
        var records = new List<PolicyComplianceRecord>();

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            _logger.LogDebug("Syncing Policy compliance for subscription {SubId}", sub.Data.SubscriptionId);

            await foreach (var state in sub.GetPolicyStateQueryResultsAsync(PolicyStateType.Latest))
            {
                records.Add(new PolicyComplianceRecord
                {
                    TenantId = tenantId,
                    PolicyAssignmentId = state.PolicyAssignmentId?.ToString() ?? "",
                    PolicyDefinitionId = state.PolicyDefinitionId?.ToString() ?? "",
                    PolicyName = state.PolicyDefinitionName ?? "Unnamed",
                    ResourceId = state.ResourceId?.ToString() ?? "",
                    ComplianceState = state.ComplianceState ?? "Unknown",
                    Category = state.PolicyDefinitionGroupNames?.FirstOrDefault(),
                    LastEvaluatedAt = state.Timestamp?.UtcDateTime ?? DateTime.UtcNow
                });
            }
        }

        _logger.LogInformation("Synced {Count} Policy compliance records for tenant {TenantId}",
            records.Count, tenantId);

        await _recommendationRepo.UpsertPolicyComplianceAsync(records);
    }

    /// <summary>
    /// Sync Microsoft Defender for Cloud findings for a tenant.
    /// </summary>
    public async Task SyncDefenderFindingsAsync(Guid tenantId)
    {
        var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
        var armClient = new ArmClient(credential);
        var findings = new List<DefenderFinding>();

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            _logger.LogDebug("Syncing Defender findings for subscription {SubId}", sub.Data.SubscriptionId);

            await foreach (var assessment in armClient.GetSecurityAssessmentsAsync(sub.Id))
            {
                var data = assessment.Data;
                var status = data.Status?.Code.ToString() ?? "Unknown";

                findings.Add(new DefenderFinding
                {
                    TenantId = tenantId,
                    FindingId = data.Id?.ToString() ?? "",
                    ResourceId = data.Id?.ToString(),
                    AssessmentName = data.DisplayName ?? "Unknown",
                    Severity = data.Metadata?.Severity.ToString() ?? "Medium",
                    Status = status,
                    Description = data.Metadata?.Description,
                    RemediationSteps = data.Metadata?.RemediationDescription,
                    Categories = data.Metadata?.Categories?.Select(c => c.ToString()).ToList(),
                    LastSeenAt = DateTime.UtcNow
                });
            }
        }

        _logger.LogInformation("Synced {Count} Defender findings for tenant {TenantId}",
            findings.Count, tenantId);

        await _recommendationRepo.UpsertDefenderFindingsAsync(findings);
    }

    private static decimal? ParseSavings(IDictionary<string, string>? extendedProperties)
    {
        if (extendedProperties == null) return null;
        if (extendedProperties.TryGetValue("annualSavingsAmount", out var savingsStr)
            && decimal.TryParse(savingsStr, out var savings))
        {
            return savings;
        }
        return null;
    }
}
