using CloudRavel.Api.Functions;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.AI;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudRavel.Tests.Ai;

/// <summary>
/// POST /api/ai/query is reachable by any member of a workspace, but its
/// propose_remediation tool reaches the same RemediationService.ProposeAsync that
/// POST /api/remediations gates behind cloud_admin. These tests pin both halves of
/// the fix: the tool is withheld from the catalog, and the dispatcher refuses it
/// even when the model asks for a tool it was never offered.
/// </summary>
public class AiToolAuthorizationTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AiFunctions CreateFunctions(IRemediationService remediationService) =>
        new(
            Substitute.For<IInventoryRepository>(),
            Substitute.For<IChangeRepository>(),
            Substitute.For<IRecommendationRepository>(),
            Substitute.For<IAnomalyRepository>(),
            Substitute.For<IIncidentRepository>(),
            Substitute.For<IRemediationRepository>(),
            remediationService,
            Substitute.For<ICloudAccountRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<ISystemSettingsRepository>(),
            Substitute.For<IConfiguration>(),
            NullLogger<AiFunctions>.Instance);

    private const string ValidProposeArgs = """
        {"playbook_key":"stop-idle-vm","title":"Stop idle VM","reason":"Idle for 14 days"}
        """;

    // ---- Tool catalog ------------------------------------------------------

    [Fact]
    public void BuildChatTools_WithoutProposePermission_OmitsTheWriteTool()
    {
        var tools = AiFunctions.BuildChatTools(canPropose: false);

        Assert.DoesNotContain(tools, t => t.FunctionName == AiToolDefinitions.ProposeRemediationTool);
    }

    [Fact]
    public void BuildChatTools_WithProposePermission_OffersTheWriteTool()
    {
        var tools = AiFunctions.BuildChatTools(canPropose: true);

        Assert.Contains(tools, t => t.FunctionName == AiToolDefinitions.ProposeRemediationTool);
    }

    [Fact]
    public void BuildChatTools_WithoutProposePermission_StillOffersEveryReadTool()
    {
        var withWrite = AiFunctions.BuildChatTools(canPropose: true);
        var readOnly = AiFunctions.BuildChatTools(canPropose: false);

        // Losing the write tool must not quietly cost a read-only caller the
        // assistant itself; exactly one tool should disappear.
        Assert.Equal(withWrite.Count - 1, readOnly.Count);

        var expected = withWrite
            .Select(t => t.FunctionName)
            .Where(n => n != AiToolDefinitions.ProposeRemediationTool)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(expected, readOnly.Select(t => t.FunctionName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---- Dispatch guard ----------------------------------------------------

    [Fact]
    public async Task ExecuteTool_ProposeWithoutPermission_IsRefusedWithoutTouchingTheService()
    {
        var remediationService = Substitute.For<IRemediationService>();
        var functions = CreateFunctions(remediationService);

        var result = await functions.ExecuteToolAsync(
            TenantId, AiToolDefinitions.ProposeRemediationTool, ValidProposeArgs,
            canPropose: false, actor: "reader@example.com");

        Assert.Contains("permission_denied", result);
        Assert.Contains(OrgRole.CloudAdmin, result);

        await remediationService.DidNotReceiveWithAnyArgs().ProposeAsync(
            default, default!, default, default!, default!, default, default!, default, default);
    }

    [Fact]
    public async Task ExecuteTool_ProposeWithPermission_ReachesTheService()
    {
        var remediationService = Substitute.For<IRemediationService>();
        remediationService.ProposeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>())
            .Returns(new RemediationAction
            {
                Id = 42,
                TenantId = TenantId,
                PlaybookKey = "stop-idle-vm",
                Title = "Stop idle VM",
                Reason = "Idle for 14 days",
                Status = RemediationStatus.PendingApproval,
                RiskLevel = RemediationRisk.Low,
                ApprovalMode = "gated"
            });

        var functions = CreateFunctions(remediationService);

        var result = await functions.ExecuteToolAsync(
            TenantId, AiToolDefinitions.ProposeRemediationTool, ValidProposeArgs,
            canPropose: true, actor: "ops@example.com");

        Assert.Contains("\"proposed\":true", result);
        Assert.DoesNotContain("permission_denied", result);
    }

    [Fact]
    public async Task ExecuteTool_ProposeWithPermission_AttributesTheRequestingHuman()
    {
        var remediationService = Substitute.For<IRemediationService>();
        remediationService.ProposeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>())
            .Returns(new RemediationAction { Id = 1, TenantId = TenantId, PlaybookKey = "stop-idle-vm" });

        var functions = CreateFunctions(remediationService);

        await functions.ExecuteToolAsync(
            TenantId, AiToolDefinitions.ProposeRemediationTool, ValidProposeArgs,
            canPropose: true, actor: "ops@example.com");

        // An approver seeing only "ai:query" cannot tell who drove the model.
        await remediationService.Received(1).ProposeAsync(
            TenantId, "stop-idle-vm", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), "ai:query:ops@example.com", Arg.Any<long?>(), Arg.Any<long?>());
    }

    [Fact]
    public async Task ExecuteTool_ReadToolWithoutProposePermission_StillWorks()
    {
        var functions = CreateFunctions(Substitute.For<IRemediationService>());

        var result = await functions.ExecuteToolAsync(
            TenantId, "get_tenant_summary", "{}", canPropose: false, actor: "reader@example.com");

        Assert.DoesNotContain("permission_denied", result);
    }

    [Fact]
    public async Task ExecuteTool_UnknownToolName_IsReportedNotSilentlyIgnored()
    {
        var functions = CreateFunctions(Substitute.For<IRemediationService>());

        var result = await functions.ExecuteToolAsync(
            TenantId, "drop_all_tables", "{}", canPropose: true, actor: "ops@example.com");

        Assert.Contains("Unknown tool", result);
    }
}
