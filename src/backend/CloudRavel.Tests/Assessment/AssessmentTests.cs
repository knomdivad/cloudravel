using Xunit;

using CloudRavel.Core.Models;

namespace CloudRavel.Tests.Assessment;

/// <summary>
/// Locks the fix-list ranking rules and the risk-free guarantee math.
/// </summary>
public sealed class AssessmentReportBuilderTests
{
    private static AdvisorRecommendation AdvisorCost(string id, decimal? savings, string impact = "Medium") => new()
    {
        TenantId = Guid.NewGuid(),
        RecommendationId = id,
        Category = "Cost",
        Impact = impact,
        Title = $"cost rec {id}",
        EstimatedSavings = savings,
        FirstSeenAt = DateTime.UtcNow
    };

    private static Anomaly Anomaly(AnomalyKind kind, AnomalySeverity severity = AnomalySeverity.Medium) => new()
    {
        TenantId = Guid.NewGuid(),
        Fingerprint = Guid.NewGuid().ToString("N"),
        Kind = kind,
        Severity = severity,
        Title = $"{kind} anomaly",
        DetectedAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow
    };

    [Fact]
    public void Tier1_items_rank_by_savings_descending()
    {
        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[]
            {
                AdvisorCost("small", 100m),
                AdvisorCost("large", 900m),
                AdvisorCost("mid", 500m)
            }
        });

        Assert.Equal(new[] { 900m, 500m, 100m }, report.FixList.Select(i => i.EstimatedAnnualSavings!.Value));
        Assert.All(report.FixList, i => Assert.Equal(1, i.Tier));
        Assert.Equal(new[] { 1, 2, 3 }, report.FixList.Select(i => i.Rank));
    }

    [Fact]
    public void Summary_carries_guarantee_math()
    {
        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[] { AdvisorCost("a", 7000m) }
        }, fee: 3000m);

        Assert.Equal(7000m, report.Savings.IdentifiedAnnual);
        Assert.Equal(6000m, report.Savings.TargetSavings);
        Assert.Equal(3000m, report.Savings.Fee);
        Assert.True(report.Savings.MeetsGuarantee);
        Assert.Equal(583.33m, report.Savings.IdentifiedMonthly);
    }

    [Fact]
    public void Below_target_does_not_meet_guarantee()
    {
        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[] { AdvisorCost("a", 5000m) }
        }, fee: 3000m);

        Assert.False(report.Savings.MeetsGuarantee);
        Assert.Equal(1000m, report.Savings.TargetSavings - report.Savings.IdentifiedAnnual);
    }

    [Fact]
    public void Unquantified_cost_and_anomalies_land_in_tier2_and_tier3()
    {
        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[] { AdvisorCost("no-figure", null) },
            Anomalies = new[]
            {
                Anomaly(AnomalyKind.CostAnomaly),
                Anomaly(AnomalyKind.SecurityPostureRegression, AnomalySeverity.High)
            }
        });

        Assert.Equal(0, report.FixList.Count(i => i.Tier == 1)); // nothing quantified → no tier-1 items
        Assert.Equal(2, report.FixList.Count(i => i.Tier == 2)); // advisor + cost anomaly
        Assert.Equal(1, report.FixList.Count(i => i.Tier == 3)); // security anomaly
        Assert.Equal(0m, report.Savings.IdentifiedAnnual);
        // Tier 2 items outrank the tier-3 risk item.
        Assert.Equal(2, report.FixList[0].Tier);
        Assert.Equal(3, report.FixList[^1].Tier);
    }

    [Fact]
    public void Resolved_anomalies_are_excluded()
    {
        var resolved = Anomaly(AnomalyKind.CostAnomaly);
        resolved.Status = AnomalyStatus.Resolved;

        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            Anomalies = new[] { resolved }
        });

        Assert.Empty(report.FixList);
    }

    [Fact]
    public void Non_cost_advisor_recs_do_not_appear_as_cost_items()
    {
        var security = AdvisorCost("sec", null);
        security.Category = "Security";

        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[] { security }
        });

        Assert.Empty(report.FixList);
    }

    [Fact]
    public void Engagement_state_reflects_lifecycle()
    {
        var now = DateTime.UtcNow;
        var inputs = new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AssessmentStartedAt = now,
            AssessmentEndsAt = now.AddDays(10)
        };
        Assert.Equal("watching", AssessmentReportBuilder.Build(inputs).EngagementState);

        inputs.AssessmentCompletedAt = now;
        Assert.Equal("completed", AssessmentReportBuilder.Build(inputs).EngagementState);

        Assert.Equal("not_started", AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test"
        }).EngagementState);
    }

    [Fact]
    public void Markdown_renders_summary_and_table()
    {
        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Contoso",
            AssessmentStartedAt = DateTime.UtcNow,
            AssessmentEndsAt = DateTime.UtcNow.AddDays(10),
            AdvisorRecommendations = new[] { AdvisorCost("a", 640m) }
        });

        var md = AssessmentReportBuilder.ToMarkdown(report);

        Assert.Contains("# Cloud Assessment Fix-List — Contoso", md);
        Assert.Contains("Identified savings: $640 per year", md);
        Assert.Contains("cost rec a", md); // the rec's title appears in the fix-list table
        Assert.Contains("Risk-free guarantee", md);
        Assert.Contains("| 1 | 1 | advisor |", md);
    }

    [Fact]
    public void Csv_renders_header_and_escaped_fields()
    {
        var rec = AdvisorCost("a", 640m);
        rec.Title = "Right-size, \"big\" VM";

        var report = AssessmentReportBuilder.Build(new AssessmentReportBuilder.EstateInputs
        {
            WorkspaceName = "Test",
            AdvisorRecommendations = new[] { rec }
        });

        var csv = AssessmentReportBuilder.ToCsv(report);

        Assert.StartsWith("rank,tier,source,title", csv);
        Assert.Contains("\"Right-size, \"\"big\"\" VM\"", csv);
        Assert.Contains("640", csv);
    }
}

/// <summary>
/// The read-only gate: an active assessment must block remediation
/// approval/execution; completed assessments and standard engagements must not.
/// </summary>
public sealed class AssessmentPolicyTests
{
    private static Tenant Tenant(EngagementKind kind, DateTime? completed = null) => new()
    {
        TenantId = Guid.NewGuid(),
        DisplayName = "Test",
        EngagementKind = kind,
        AssessmentStartedAt = kind == EngagementKind.Assessment ? DateTime.UtcNow : null,
        AssessmentCompletedAt = completed
    };

    [Fact]
    public void Active_assessment_blocks_actions()
    {
        Assert.True(AssessmentPolicy.BlocksActions(Tenant(EngagementKind.Assessment)));
    }

    [Fact]
    public void Completed_assessment_stops_blocking()
    {
        Assert.False(AssessmentPolicy.BlocksActions(Tenant(EngagementKind.Assessment, DateTime.UtcNow)));
    }

    [Fact]
    public void Standard_engagement_never_blocks()
    {
        Assert.False(AssessmentPolicy.BlocksActions(Tenant(EngagementKind.Standard)));
    }
}
