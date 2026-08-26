using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using CloudRavel.Infrastructure.AiOps;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudRavel.Tests.AiOps;

/// <summary>
/// The approval gate is the last thing standing between a proposal and a change
/// applied to a customer's live cloud estate, and the execute path is what the
/// queue worker drives unattended. Neither had any coverage.
/// </summary>
public class RemediationServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IRemediationRepository _remediations = Substitute.For<IRemediationRepository>();
    private readonly IIncidentRepository _incidents = Substitute.For<IIncidentRepository>();
    private readonly ICloudProviderAdapterFactory _adapters = Substitute.For<ICloudProviderAdapterFactory>();

    private RemediationService CreateService() =>
        new(_tenants, _remediations, _incidents, _adapters, NullLogger<RemediationService>.Instance);

    private void GivenTenant(AutoRemediationMode mode) =>
        _tenants.GetByIdAsync(TenantId).Returns(new Tenant { TenantId = TenantId, AutoRemediationMode = mode });

    private void GivenPlaybook(
        RemediationRisk risk = RemediationRisk.Low,
        bool alwaysRequiresApproval = false,
        bool enabled = true,
        string key = "stop-idle-vm")
    {
        _remediations.GetPlaybookAsync(key).Returns(new RemediationPlaybook
        {
            PlaybookKey = key,
            DisplayName = "Stop idle VM",
            Provider = CloudProvider.Azure,
            RiskLevel = risk,
            AlwaysRequiresApproval = alwaysRequiresApproval,
            Enabled = enabled
        });
    }

    private Task<RemediationAction> Propose(RemediationService service) =>
        service.ProposeAsync(TenantId, "stop-idle-vm", "/subscriptions/x/vm1", "Stop idle VM",
            "Idle for 14 days", null, requestedBy: "user:ops@example.com");

    // ---- Approval gate matrix ---------------------------------------------

    [Theory]
    [InlineData(AutoRemediationMode.Auto, RemediationRisk.Low, RemediationStatus.Approved)]
    [InlineData(AutoRemediationMode.Auto, RemediationRisk.Medium, RemediationStatus.PendingApproval)]
    [InlineData(AutoRemediationMode.Auto, RemediationRisk.High, RemediationStatus.PendingApproval)]
    [InlineData(AutoRemediationMode.Gated, RemediationRisk.Low, RemediationStatus.PendingApproval)]
    [InlineData(AutoRemediationMode.Gated, RemediationRisk.Medium, RemediationStatus.PendingApproval)]
    [InlineData(AutoRemediationMode.Gated, RemediationRisk.High, RemediationStatus.PendingApproval)]
    [InlineData(AutoRemediationMode.Disabled, RemediationRisk.Low, RemediationStatus.Proposed)]
    [InlineData(AutoRemediationMode.Disabled, RemediationRisk.Medium, RemediationStatus.Proposed)]
    [InlineData(AutoRemediationMode.Disabled, RemediationRisk.High, RemediationStatus.Proposed)]
    public async Task Propose_ResolvesApprovalGateFromTenantModeAndRisk(
        AutoRemediationMode mode, RemediationRisk risk, RemediationStatus expected)
    {
        GivenTenant(mode);
        GivenPlaybook(risk);

        var action = await Propose(CreateService());

        Assert.Equal(expected, action.Status);
    }

    [Fact]
    public async Task Propose_AutoModeIsTheOnlyPathThatSkipsAHuman()
    {
        GivenTenant(AutoRemediationMode.Auto);
        GivenPlaybook(RemediationRisk.Low);

        var action = await Propose(CreateService());

        Assert.Equal(RemediationStatus.Approved, action.Status);
        Assert.Equal("auto", action.ApprovalMode);
        Assert.Equal("system:auto-policy", action.ApprovedBy);
        // Auto-approved actions run immediately, so they must not carry an
        // approval TTL that the expiry sweep could act on.
        Assert.Null(action.ExpiresAt);
    }

    [Theory]
    [InlineData(RemediationRisk.Low)]
    [InlineData(RemediationRisk.Medium)]
    [InlineData(RemediationRisk.High)]
    public async Task Propose_AlwaysRequiresApprovalOverridesAutoModeAtEveryRisk(RemediationRisk risk)
    {
        GivenTenant(AutoRemediationMode.Auto);
        GivenPlaybook(risk, alwaysRequiresApproval: true);

        var action = await Propose(CreateService());

        Assert.NotEqual(RemediationStatus.Approved, action.Status);
        Assert.Equal("gated", action.ApprovalMode);
        Assert.Null(action.ApprovedBy);
    }

    [Fact]
    public async Task Propose_GatedActionsCarryAnExpiry()
    {
        GivenTenant(AutoRemediationMode.Gated);
        GivenPlaybook();

        var action = await Propose(CreateService());

        Assert.NotNull(action.ExpiresAt);
        Assert.True(action.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Propose_DisabledPlaybookIsRejected()
    {
        GivenTenant(AutoRemediationMode.Auto);
        GivenPlaybook(enabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Propose(CreateService()));
        await _remediations.DidNotReceiveWithAnyArgs().CreateActionAsync(default!);
    }

    [Fact]
    public async Task Propose_UnknownPlaybookIsRejected()
    {
        GivenTenant(AutoRemediationMode.Auto);
        _remediations.GetPlaybookAsync("stop-idle-vm").Returns((RemediationPlaybook?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Propose(CreateService()));
        await _remediations.DidNotReceiveWithAnyArgs().CreateActionAsync(default!);
    }

    [Fact]
    public async Task Propose_DoesNotStackASecondActionWhileOneIsInFlight()
    {
        GivenTenant(AutoRemediationMode.Gated);
        GivenPlaybook();

        var existing = new RemediationAction
        {
            Id = 7,
            TenantId = TenantId,
            PlaybookKey = "stop-idle-vm",
            ResourceId = "/subscriptions/x/vm1",
            Status = RemediationStatus.PendingApproval
        };
        _remediations.HasOpenActionAsync(TenantId, "stop-idle-vm", "/subscriptions/x/vm1").Returns(true);
        _remediations.GetActionsAsync(TenantId, limit: 200).Returns(new List<RemediationAction> { existing });

        var action = await Propose(CreateService());

        Assert.Equal(7, action.Id);
        await _remediations.DidNotReceiveWithAnyArgs().CreateActionAsync(default!);
    }

    // ---- Execute state machine --------------------------------------------

    [Theory]
    [InlineData(RemediationStatus.Proposed)]
    [InlineData(RemediationStatus.PendingApproval)]
    [InlineData(RemediationStatus.Rejected)]
    [InlineData(RemediationStatus.Executing)]
    [InlineData(RemediationStatus.Succeeded)]
    [InlineData(RemediationStatus.Failed)]
    [InlineData(RemediationStatus.Expired)]
    public async Task Execute_RefusesAnythingThatIsNotApproved(RemediationStatus status)
    {
        _remediations.GetActionByIdAsync(TenantId, 1).Returns(new RemediationAction
        {
            Id = 1,
            TenantId = TenantId,
            PlaybookKey = "stop-idle-vm",
            Status = status
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().ExecuteAsync(TenantId, 1));

        // Nothing may reach a cloud provider from a non-approved state.
        _adapters.DidNotReceiveWithAnyArgs().GetAdapter(default);
        await _remediations.DidNotReceiveWithAnyArgs().MarkExecutionStartedAsync(default);
    }

    [Fact]
    public async Task Execute_ExpiresStaleApprovalsBeforeDrainingTheQueue()
    {
        _remediations.GetApprovedPendingExecutionAsync(limit: 25).Returns(new List<RemediationAction>());

        await CreateService().ExecuteApprovedActionsAsync();

        // An approval that sat past its TTL must not fire on the next sweep.
        await _remediations.Received(1).ExpireStaleActionsAsync(Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Execute_OneFailingActionDoesNotWedgeTheQueue()
    {
        var good = new RemediationAction
        {
            Id = 2, TenantId = TenantId, PlaybookKey = "stop-idle-vm", Status = RemediationStatus.Approved
        };
        var bad = new RemediationAction
        {
            Id = 1, TenantId = TenantId, PlaybookKey = "missing", Status = RemediationStatus.Approved
        };

        _remediations.GetApprovedPendingExecutionAsync(limit: 25)
            .Returns(new List<RemediationAction> { bad, good });
        _remediations.GetActionByIdAsync(TenantId, 1).Returns(bad);
        _remediations.GetActionByIdAsync(TenantId, 2).Returns(good);
        _remediations.GetPlaybookAsync("missing").Returns((RemediationPlaybook?)null);
        GivenPlaybook();

        var adapter = Substitute.For<ICloudProviderAdapter>();
        adapter.ExecuteRemediationAsync(TenantId, Arg.Any<RemediationPlaybook>(), Arg.Any<RemediationAction>())
            .Returns(RemediationExecutionResult.Ok(null));
        _adapters.GetAdapter(CloudProvider.Azure).Returns(adapter);

        var executed = await CreateService().ExecuteApprovedActionsAsync();

        Assert.Equal(1, executed);
        await _remediations.Received(1).MarkExecutionStartedAsync(2);
    }

    [Fact]
    public async Task Execute_AdapterFailureIsRecordedRatherThanThrown()
    {
        var action = new RemediationAction
        {
            Id = 1, TenantId = TenantId, PlaybookKey = "stop-idle-vm",
            Provider = CloudProvider.Azure, Status = RemediationStatus.Approved
        };
        _remediations.GetActionByIdAsync(TenantId, 1).Returns(action);
        GivenPlaybook();

        var adapter = Substitute.For<ICloudProviderAdapter>();
        adapter.ExecuteRemediationAsync(TenantId, Arg.Any<RemediationPlaybook>(), Arg.Any<RemediationAction>())
            .Returns<RemediationExecutionResult>(_ => throw new HttpRequestException("provider exploded"));
        _adapters.GetAdapter(CloudProvider.Azure).Returns(adapter);

        await CreateService().ExecuteAsync(TenantId, 1);

        await _remediations.Received(1).MarkExecutionCompletedAsync(1, false, Arg.Any<string?>(), "provider exploded");
    }

    // ---- Approve / reject transitions --------------------------------------

    [Theory]
    [InlineData(RemediationStatus.Proposed)]
    [InlineData(RemediationStatus.PendingApproval)]
    public async Task Approve_MovesOpenActionsToApproved(RemediationStatus from)
    {
        var action = new RemediationAction { Id = 1, TenantId = TenantId, Status = from };
        _remediations.GetActionByIdAsync(TenantId, 1).Returns(action);

        await CreateService().ApproveAsync(TenantId, 1, "ops@example.com");

        await _remediations.Received(1).UpdateStatusAsync(
            TenantId, 1, RemediationStatus.Approved, "ops@example.com");
    }

    [Theory]
    [InlineData(RemediationStatus.Approved)]
    [InlineData(RemediationStatus.Executing)]
    [InlineData(RemediationStatus.Succeeded)]
    [InlineData(RemediationStatus.Expired)]
    public async Task Approve_RefusesActionsThatAreNoLongerOpen(RemediationStatus from)
    {
        _remediations.GetActionByIdAsync(TenantId, 1)
            .Returns(new RemediationAction { Id = 1, TenantId = TenantId, Status = from });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ApproveAsync(TenantId, 1, "ops@example.com"));
    }

    [Fact]
    public async Task Approve_UnknownActionIsRejected()
    {
        _remediations.GetActionByIdAsync(TenantId, 99).Returns((RemediationAction?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().ApproveAsync(TenantId, 99, "ops@example.com"));
    }
}
