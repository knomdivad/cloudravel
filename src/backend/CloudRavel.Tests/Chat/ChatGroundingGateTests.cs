using Xunit;

using CloudRavel.Infrastructure.Chat;

namespace CloudRavel.Tests.Chat;

/// <summary>
/// Grounding-gate tests. Context blocks are rendered with the real
/// <see cref="CustomerChatFunctions.RenderWorkspaceContext"/> shape so the
/// fixtures reflect what the model actually sees.
/// </summary>
public sealed class ChatGroundingGateTests
{
    private const string Context = """
        WORKSPACE CONTEXT (authoritative, read from the customer's live data):
        workspace: Contoso Ltd
        total_resources: 42
        last_inventory_snapshot_at: 2026-09-16 09:00 UTC
        resource_changes_last_7_days: 5
        open_advisor_recommendations: 3
        estimated_annual_savings_usd: 1200
        open_security_findings: 7 (critical: 2)
        non_compliant_policies: 4
        resource_types (top by count):
          - microsoft.compute/virtualmachines: 18
          - microsoft.storage/storageaccounts: 9
        top_security_findings:
          - [Critical] NSG open to world on vm-prod-01 (vm-prod-01, last seen 2026-09-15)
        cloud_connections:
          - azure: Contoso Production (connected)
        END WORKSPACE CONTEXT. Answer only from the data above.
        """;

    // (1) Audit repro: fabricated figures about an entity that is nowhere in
    // the context — must be grounded out, whatever the model emitted.
    [Fact]
    public void Fabricated_figures_without_context_overlap_are_grounded_out()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Company B currently has 3,481 virtual machines and estimated annual savings of $86,400.",
            Context,
            userMessage: "How many resources does Company B have?");

        Assert.False(verdict.Passed);
        Assert.Contains("not grounded", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // (2) A legitimate answer built from context figures/entities passes.
    [Fact]
    public void Answer_citing_context_figures_and_entities_passes()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Your workspace has 42 resources and estimated annual savings of $1,200. " +
            "The top finding is NSG open to world on vm-prod-01.",
            Context,
            userMessage: "Give me an overview of my workspace.");

        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // (3) Same fabrication shape, asked as a plain out-of-context question.
    [Fact]
    public void Out_of_context_entity_with_plausible_numbers_is_grounded_out()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Globex runs about 120 storage accounts; you could save roughly $9,000 a year there.",
            Context,
            userMessage: "Tell me about Globex.");

        Assert.False(verdict.Passed);
    }

    // Generic, claim-free guidance must keep flowing (no false refusals).
    [Theory]
    [InlineData("You can reduce costs by right-sizing idle VMs and enabling auto-shutdown.")]
    [InlineData("Check the Security page in the app for the full list of findings.")]
    [InlineData("I don't know. I could not find an answer in your workspace data.")]
    public void Answers_without_extractable_claims_pass(string answer)
    {
        var verdict = ChatGroundingGate.Evaluate(answer, Context, userMessage: "what should I do?");
        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // Echoing a number the USER supplied is not fabrication.
    [Fact]
    public void User_supplied_number_is_not_treated_as_fabrication()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "For the 12 resources you mentioned, enabling auto-shutdown is a good next step.",
            Context,
            userMessage: "I have 12 idle VMs, what should I do?");

        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // A number that IS in the context resolves (with thousands separators).
    [Fact]
    public void Context_figure_with_separator_resolves()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Your estimated annual savings are $1,200 across 3 open recommendations.",
            Context,
            userMessage: "How much can I save?");

        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // Resource-id shapes must match the context.
    [Fact]
    public void Resource_id_not_in_context_is_grounded_out()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "There is a critical finding on vm-dev-staging-09 that needs attention.",
            Context,
            userMessage: "Any security problems?");

        Assert.False(verdict.Passed);
    }

    [Fact]
    public void Resource_id_in_context_passes()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Yes - NSG open to world on vm-prod-01 is critical.",
            Context,
            userMessage: "Any security problems?");

        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // Years and dates are prose numbers, not figure claims.
    [Fact]
    public void Years_and_dates_do_not_block()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "Since 2024 your team has been on Azure; the latest snapshot is from 2026-09-16.",
            Context,
            userMessage: "How long have we been on Azure?");

        Assert.True(verdict.Passed, verdict.Reason ?? "no reason");
    }

    // Degenerate inputs never throw.
    [Fact]
    public void Null_or_missing_context_still_refuses_figure_claims()
    {
        var verdict = ChatGroundingGate.Evaluate("You have 999 VMs.", contextBlock: null, userMessage: "how many?");
        Assert.False(verdict.Passed);
    }
}
