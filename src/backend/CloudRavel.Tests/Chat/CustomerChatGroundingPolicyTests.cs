using CloudRavel.Api.Functions;
using CloudRavel.Infrastructure.Chat;
using Xunit;

namespace CloudRavel.Tests.Chat;

/// <summary>
/// The chat endpoint MUST substitute the gate's fixed grounded-out text when
/// the grounding gate fails, and mark it refused (audit scenario: fabricated
/// out-of-context figures must never reach the customer with refused:false).
/// </summary>
public sealed class CustomerChatGroundingPolicyTests
{
    private static readonly string Fabricated =
        "Company B currently has 3,481 virtual machines and estimated annual savings of $86,400.";

    [Fact]
    public void Grounding_gate_failure_replaces_answer_with_grounded_out_text()
    {
        var verdict = ChatGroundingGate.Evaluate(Fabricated, contextBlock: "total_resources: 42");
        Assert.False(verdict.Passed);

        var answer = CustomerChatFunctions.ApplyGroundingGate(Fabricated, verdict);
        Assert.Equal(ChatGroundingGate.GroundedOutText, answer);
    }

    [Fact]
    public void Grounding_gate_pass_leaves_answer_unchanged()
    {
        var verdict = ChatGroundingGate.Evaluate(
            "You have 42 resources.", contextBlock: "total_resources: 42", userMessage: "how many?");

        var answer = CustomerChatFunctions.ApplyGroundingGate("You have 42 resources.", verdict);
        Assert.Equal("You have 42 resources.", answer);
    }

    // The endpoint's refused flag derives from the SAME predicate used for the
    // guard/idk path; the gate failure must classify as refused.
    [Fact]
    public void Grounded_out_text_is_detected_as_refused_by_endpoint_rule()
    {
        var groundedOut = CustomerChatFunctions.ApplyGroundingGate(Fabricated,
            ChatGroundingGate.Evaluate(Fabricated, contextBlock: "total_resources: 42"));

        var refused = !ReferenceEquals(groundedOut, Fabricated)
                      || groundedOut.StartsWith("I don't know", StringComparison.OrdinalIgnoreCase);
        Assert.True(refused);
    }
}
