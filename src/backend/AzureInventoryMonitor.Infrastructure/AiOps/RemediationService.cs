using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.AiOps;

/// <summary>
/// The remediation engine.
///
/// Approval gate policy (defense in depth, most restrictive wins):
///   - tenant AutoRemediationMode = Disabled → action lands as Proposed; a human
///     must explicitly approve it before anything can run.
///   - tenant AutoRemediationMode = Gated    → action lands as PendingApproval.
///   - tenant AutoRemediationMode = Auto     → LOW-risk playbooks auto-approve;
///     medium/high risk still require a human.
///   - playbook AlwaysRequiresApproval       → never auto-approves, ever.
///
/// Execution only ever happens from Approved state via allow-listed playbook
/// executors on the provider adapters, and every transition is persisted.
/// </summary>
public sealed class RemediationService : IRemediationService
{
    private static readonly TimeSpan ApprovalTtl = TimeSpan.FromDays(7);

    private readonly ITenantRepository _tenantRepo;
    private readonly IRemediationRepository _remediationRepo;
    private readonly IIncidentRepository _incidentRepo;
    private readonly ICloudProviderAdapterFactory _adapterFactory;
    private readonly ILogger<RemediationService> _logger;

    public RemediationService(
        ITenantRepository tenantRepo,
        IRemediationRepository remediationRepo,
        IIncidentRepository incidentRepo,
        ICloudProviderAdapterFactory adapterFactory,
        ILogger<RemediationService> logger)
    {
        _tenantRepo = tenantRepo;
        _remediationRepo = remediationRepo;
        _incidentRepo = incidentRepo;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    public async Task<RemediationAction> ProposeAsync(
        Guid tenantId, string playbookKey, string? resourceId, string title, string reason,
        string? parametersJson, string requestedBy, long? anomalyId = null, long? incidentId = null)
    {
        var playbook = await _remediationRepo.GetPlaybookAsync(playbookKey)
            ?? throw new KeyNotFoundException($"Playbook '{playbookKey}' does not exist.");
        if (!playbook.Enabled)
            throw new InvalidOperationException($"Playbook '{playbookKey}' is disabled.");

        var tenant = await _tenantRepo.GetByIdAsync(tenantId)
            ?? throw new KeyNotFoundException($"Tenant {tenantId} not found.");

        // Dedup: never stack a second identical action while one is in flight.
        if (await _remediationRepo.HasOpenActionAsync(tenantId, playbookKey, resourceId))
        {
            var existing = (await _remediationRepo.GetActionsAsync(tenantId, limit: 200))
                .First(a => a.PlaybookKey == playbookKey && a.ResourceId == resourceId &&
                            a.Status is RemediationStatus.Proposed or RemediationStatus.PendingApproval
                                      or RemediationStatus.Approved or RemediationStatus.Executing);
            _logger.LogInformation("Skipping duplicate remediation proposal ({Playbook} on {Resource}); action {Id} already open",
                playbookKey, resourceId, existing.Id);
            return existing;
        }

        var (status, approvalMode, approvedBy) = ResolveApprovalGate(tenant, playbook);

        var action = new RemediationAction
        {
            TenantId = tenantId,
            PlaybookKey = playbookKey,
            Provider = playbook.Provider,
            ResourceId = resourceId,
            Title = string.IsNullOrWhiteSpace(title) ? playbook.DisplayName : title,
            Reason = reason,
            ParametersJson = parametersJson,
            Status = status,
            RiskLevel = playbook.RiskLevel,
            RequestedBy = requestedBy,
            AnomalyId = anomalyId,
            IncidentId = incidentId,
            ApprovalMode = approvalMode,
            ApprovedBy = approvedBy,
            ApprovedAt = approvedBy != null ? DateTime.UtcNow : null,
            ExpiresAt = status is RemediationStatus.Proposed or RemediationStatus.PendingApproval
                ? DateTime.UtcNow.Add(ApprovalTtl)
                : null
        };

        await _remediationRepo.CreateActionAsync(action);

        if (incidentId.HasValue)
        {
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = incidentId.Value,
                TenantId = tenantId,
                EventType = "remediation_linked",
                Message = $"Remediation proposed ({status}): {action.Title}",
                ActorName = requestedBy
            });
        }

        return action;
    }

    public async Task<RemediationAction> ApproveAsync(Guid tenantId, long actionId, string approvedBy)
    {
        var action = await _remediationRepo.GetActionByIdAsync(tenantId, actionId)
            ?? throw new KeyNotFoundException($"Remediation action {actionId} not found.");

        if (action.Status is not (RemediationStatus.Proposed or RemediationStatus.PendingApproval))
            throw new InvalidOperationException($"Action {actionId} is {action.Status} and cannot be approved.");

        await _remediationRepo.UpdateStatusAsync(tenantId, actionId, RemediationStatus.Approved, approvedBy);
        return (await _remediationRepo.GetActionByIdAsync(tenantId, actionId))!;
    }

    public async Task<RemediationAction> RejectAsync(Guid tenantId, long actionId, string rejectedBy, string? reason)
    {
        var action = await _remediationRepo.GetActionByIdAsync(tenantId, actionId)
            ?? throw new KeyNotFoundException($"Remediation action {actionId} not found.");

        if (action.Status is not (RemediationStatus.Proposed or RemediationStatus.PendingApproval))
            throw new InvalidOperationException($"Action {actionId} is {action.Status} and cannot be rejected.");

        await _remediationRepo.UpdateStatusAsync(tenantId, actionId, RemediationStatus.Rejected, rejectedBy, reason);

        if (action.IncidentId.HasValue)
        {
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = action.IncidentId.Value,
                TenantId = tenantId,
                EventType = "remediation_linked",
                Message = $"Remediation rejected by {rejectedBy}: {action.Title}" +
                          (string.IsNullOrEmpty(reason) ? "" : $" — {reason}"),
                ActorName = rejectedBy
            });
        }

        return (await _remediationRepo.GetActionByIdAsync(tenantId, actionId))!;
    }

    public async Task<RemediationAction> ExecuteAsync(Guid tenantId, long actionId)
    {
        var action = await _remediationRepo.GetActionByIdAsync(tenantId, actionId)
            ?? throw new KeyNotFoundException($"Remediation action {actionId} not found.");

        if (action.Status != RemediationStatus.Approved)
            throw new InvalidOperationException($"Action {actionId} is {action.Status}; only Approved actions execute.");

        var playbook = await _remediationRepo.GetPlaybookAsync(action.PlaybookKey)
            ?? throw new InvalidOperationException($"Playbook '{action.PlaybookKey}' no longer exists.");

        // Atomic Approved → Executing transition (loses gracefully to a concurrent executor)
        await _remediationRepo.MarkExecutionStartedAsync(actionId);

        RemediationExecutionResult result;
        try
        {
            var adapter = _adapterFactory.GetAdapter(action.Provider);
            result = await adapter.ExecuteRemediationAsync(tenantId, playbook, action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remediation action {ActionId} threw during execution", actionId);
            result = RemediationExecutionResult.Fail(ex.Message);
        }

        await _remediationRepo.MarkExecutionCompletedAsync(actionId, result.Succeeded, result.ResultJson, result.ErrorMessage);

        _logger.LogInformation("Remediation action {ActionId} ({Playbook}) {Outcome}",
            actionId, action.PlaybookKey, result.Succeeded ? "succeeded" : $"failed: {result.ErrorMessage}");

        if (action.IncidentId.HasValue)
        {
            await _incidentRepo.AddEventAsync(new IncidentEvent
            {
                IncidentId = action.IncidentId.Value,
                TenantId = tenantId,
                EventType = "remediation_linked",
                Message = result.Succeeded
                    ? $"Remediation executed successfully: {action.Title}"
                    : $"Remediation FAILED: {action.Title} — {result.ErrorMessage}",
                ActorName = "remediation-engine"
            });
        }

        return (await _remediationRepo.GetActionByIdAsync(tenantId, actionId))!;
    }

    public async Task<int> ExecuteApprovedActionsAsync(CancellationToken cancellationToken = default)
    {
        // Expire stale approvals first so nothing ancient ever fires.
        await _remediationRepo.ExpireStaleActionsAsync(DateTime.UtcNow);

        var queue = await _remediationRepo.GetApprovedPendingExecutionAsync(limit: 25);
        var executed = 0;

        foreach (var action in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteAsync(action.TenantId, action.Id);
                executed++;
            }
            catch (Exception ex)
            {
                // Keep draining; a failing tenant/provider must not wedge the queue.
                _logger.LogError(ex, "Queued execution failed for action {ActionId}", action.Id);
            }
        }

        return executed;
    }

    private static (RemediationStatus Status, string ApprovalMode, string? ApprovedBy) ResolveApprovalGate(
        Tenant tenant, RemediationPlaybook playbook)
    {
        if (tenant.AutoRemediationMode == AutoRemediationMode.Auto &&
            playbook.RiskLevel == RemediationRisk.Low &&
            !playbook.AlwaysRequiresApproval)
        {
            return (RemediationStatus.Approved, "auto", "system:auto-policy");
        }

        return tenant.AutoRemediationMode == AutoRemediationMode.Disabled
            ? (RemediationStatus.Proposed, "gated", null)
            : (RemediationStatus.PendingApproval, "gated", null);
    }
}
