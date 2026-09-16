namespace CloudRavel.Core.Models;

/// <summary>
/// Builds the assessment fix-list: every finding the platform observed, ranked
/// by dollars so the client reads the report top-down and acts on the biggest
/// savings first. Pure functions over the collected estate — no data access —
/// so the ranking rules are unit-testable.
///
/// Tiering:
///   1. Quantified cost items   — Advisor Cost recommendations with $ figures.
///   2. Unquantified cost items — Advisor cost-impact items without figures,
///                                plus open CostAnomaly anomalies.
///   3. Risk items              — security/posture findings (Defender, policy
///                                non-compliance, non-cost anomalies). Presented
///                                for risk reduction, not savings.
/// The summary carries the risk-free guarantee math: identified annual savings
/// vs double-the-fee target over 12 months.
/// </summary>
public static class AssessmentReportBuilder
{
    public const int DefaultFee = 3000;
    public const int DefaultWindowDays = 10;

    // ========================================================================
    // Input slices — what the API layer gathers from the repositories
    // ========================================================================

    public sealed class EstateInputs
    {
        public string WorkspaceName { get; set; } = string.Empty;
        public DateTime? AssessmentStartedAt { get; set; }
        public DateTime? AssessmentEndsAt { get; set; }
        public DateTime? AssessmentCompletedAt { get; set; }
        public int ResourceCount { get; set; }
        public DateTime? LastSnapshotAt { get; set; }
        public int ChangesInWindow { get; set; }
        public IReadOnlyList<AdvisorRecommendation> AdvisorRecommendations { get; set; } = [];
        public IReadOnlyList<Anomaly> Anomalies { get; set; } = [];
    }

    // ========================================================================
    // Output shape — JSON / CSV / Markdown all render from this
    // ========================================================================

    public sealed class AssessmentReport
    {
        public string WorkspaceName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public DateTime? WindowStart { get; set; }
        public DateTime? WindowEnd { get; set; }
        public string EngagementState { get; set; } = "not_started"; // not_started | watching | completed
        public EstateSummary Estate { get; set; } = new();
        public SavingsSummary Savings { get; set; } = new();
        public IReadOnlyList<FixListItem> FixList { get; set; } = [];
    }

    public sealed class EstateSummary
    {
        public int ResourceCount { get; set; }
        public DateTime? LastSnapshotAt { get; set; }
        public int ChangesInWindow { get; set; }
    }

    /// <summary>Risk-free guarantee math: fee, 2× fee target, identified savings vs target.</summary>
    public sealed class SavingsSummary
    {
        /// <summary>Sum of quantified annual savings across Tier-1 items (USD).</summary>
        public decimal IdentifiedAnnual { get; set; }
        /// <summary>IdentifiedAnnual / 12 — the monthly run-rate if every item lands.</summary>
        public decimal IdentifiedMonthly => Math.Round(IdentifiedAnnual / 12m, 2);
        public decimal Fee { get; set; }
        /// <summary>Guarantee target: double the fee in 12-month savings.</summary>
        public decimal TargetSavings => Fee * 2;
        /// <summary>True when identified savings already cover the double-the-fee guarantee.</summary>
        public bool MeetsGuarantee => Fee <= 0 || IdentifiedAnnual >= TargetSavings;
        /// <summary>IdentifiedAnnual as a multiple of the fee (e.g. 1.4×).</summary>
        public decimal FeeMultiple => Fee <= 0 ? 0 : Math.Round(IdentifiedAnnual / Fee, 2);
    }

    public sealed class FixListItem
    {
        public int Rank { get; set; }
        /// <summary>1 = quantified cost, 2 = unquantified cost, 3 = risk.</summary>
        public int Tier { get; set; }
        /// <summary>advisor | anomaly | defender | policy</summary>
        public string Source { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? ResourceId { get; set; }
        public string? Detail { get; set; }
        public string? Remediation { get; set; }
        /// <summary>Quantified annual savings when known (Tier 1 only, USD).</summary>
        public decimal? EstimatedAnnualSavings { get; set; }
        public string Severity { get; set; } = string.Empty;
        public DateTime FirstSeenAt { get; set; }
    }

    // ========================================================================
    // Builder
    // ========================================================================

    public static AssessmentReport Build(EstateInputs inputs, decimal fee = DefaultFee)
    {
        var items = new List<FixListItem>();

        // Tier 1: quantified cost items, biggest dollars first.
        var quantified = inputs.AdvisorRecommendations
            .Where(r => r.Category.Equals("Cost", StringComparison.OrdinalIgnoreCase)
                        && r.EstimatedSavings is > 0)
            .OrderByDescending(r => r.EstimatedSavings!.Value)
            .ToList();

        // Tier 2: cost-impact items the sources don't price.
        var unquantifiedAdvisor = inputs.AdvisorRecommendations
            .Where(r => r.Category.Equals("Cost", StringComparison.OrdinalIgnoreCase)
                        && (r.EstimatedSavings is null or <= 0))
            .ToList();
        var costAnomalies = inputs.Anomalies
            .Where(a => a.Kind == AnomalyKind.CostAnomaly && a.Status != AnomalyStatus.Resolved)
            .ToList();

        // Tier 3: risk items — security findings and non-cost anomalies.
        var riskAnomalies = inputs.Anomalies
            .Where(a => a.Kind != AnomalyKind.CostAnomaly && a.Status != AnomalyStatus.Resolved)
            .ToList();

        items.AddRange(quantified.Select(r => new FixListItem
        {
            Tier = 1,
            Source = "advisor",
            Title = r.Title,
            ResourceId = r.ResourceId,
            Detail = r.Description,
            Remediation = r.RemediationAction,
            EstimatedAnnualSavings = r.EstimatedSavings,
            Severity = r.Impact,
            FirstSeenAt = r.FirstSeenAt
        }));

        items.AddRange(unquantifiedAdvisor.Select(r => new FixListItem
        {
            Tier = 2,
            Source = "advisor",
            Title = r.Title,
            ResourceId = r.ResourceId,
            Detail = r.Description,
            Remediation = r.RemediationAction,
            Severity = r.Impact,
            FirstSeenAt = r.FirstSeenAt
        }));

        items.AddRange(costAnomalies.Select(a => new FixListItem
        {
            Tier = 2,
            Source = "anomaly",
            Title = a.Title,
            ResourceId = a.ResourceId,
            Detail = a.Description,
            Severity = a.Severity.ToString(),
            FirstSeenAt = a.DetectedAt
        }));

        items.AddRange(riskAnomalies.Select(a => new FixListItem
        {
            Tier = 3,
            Source = "anomaly",
            Title = a.Title,
            ResourceId = a.ResourceId,
            Detail = a.Description,
            Severity = a.Severity.ToString(),
            FirstSeenAt = a.DetectedAt
        }));

        // Rank: tier asc, then quantified dollars desc, then severity weight desc, then age.
        var severityWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Critical"] = 5, ["High"] = 4, ["Medium"] = 3, ["Low"] = 2, ["Info"] = 1
        };
        var ranked = items
            .OrderByDescending(i => i.Tier == 1 ? 1 : 0)
            .ThenBy(i => i.Tier)
            .ThenByDescending(i => i.EstimatedAnnualSavings ?? 0)
            .ThenByDescending(i => severityWeight.GetValueOrDefault(i.Severity, 0))
            .ThenBy(i => i.FirstSeenAt)
            .ToList();

        for (var i = 0; i < ranked.Count; i++)
            ranked[i].Rank = i + 1;

        return new AssessmentReport
        {
            WorkspaceName = inputs.WorkspaceName,
            GeneratedAt = DateTime.UtcNow,
            WindowStart = inputs.AssessmentStartedAt,
            WindowEnd = inputs.AssessmentEndsAt,
            EngagementState = ResolveState(inputs),
            Estate = new EstateSummary
            {
                ResourceCount = inputs.ResourceCount,
                LastSnapshotAt = inputs.LastSnapshotAt,
                ChangesInWindow = inputs.ChangesInWindow
            },
            Savings = new SavingsSummary
            {
                IdentifiedAnnual = quantified.Sum(r => r.EstimatedSavings!.Value),
                Fee = fee
            },
            FixList = ranked
        };
    }

    // ========================================================================
    // Renderers — Markdown (client-ready) and CSV (spreadsheet)
    // ========================================================================

    public static string ToMarkdown(AssessmentReport report)
    {
        var md = new System.Text.StringBuilder();
        md.AppendLine($"# Cloud Assessment Fix-List — {report.WorkspaceName}");
        md.AppendLine();
        md.AppendLine($"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC" +
                      (report.WindowStart.HasValue
                          ? $" · watch window {report.WindowStart:yyyy-MM-dd} → {report.WindowEnd:yyyy-MM-dd}"
                          : string.Empty));
        md.AppendLine();
        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine($"- **Estate:** {report.Estate.ResourceCount} resources" +
                      (report.Estate.LastSnapshotAt.HasValue
                          ? $", last inventoried {report.Estate.LastSnapshotAt:yyyy-MM-dd}"
                          : ", no completed inventory snapshot yet"));
        md.AppendLine($"- **Changes during watch window:** {report.Estate.ChangesInWindow}");
        md.AppendLine($"- **Identified savings: ${report.Savings.IdentifiedAnnual:N0} per year** " +
                      $"(${report.Savings.IdentifiedMonthly:N0} per month)");
        md.AppendLine($"- **Fix-list items: {report.FixList.Count}** " +
                      $"({report.FixList.Count(i => i.Tier == 1)} quantified, " +
                      $"{report.FixList.Count(i => i.Tier == 2)} unquantified cost, " +
                      $"{report.FixList.Count(i => i.Tier == 3)} risk)");
        md.AppendLine();
        md.AppendLine("### Risk-free guarantee");
        md.AppendLine();
        md.AppendLine($"Fee ${report.Savings.Fee:N0} · target {report.Savings.TargetSavings:N0} (2× fee) · " +
                      $"identified ${report.Savings.IdentifiedAnnual:N0} ({report.Savings.FeeMultiple:N2}× fee) — " +
                      (report.Savings.MeetsGuarantee
                          ? "**guarantee met**: identified savings exceed double the fee."
                          : $"guarantee not yet met — {report.Savings.TargetSavings - report.Savings.IdentifiedAnnual:N0} more identified savings needed."));
        md.AppendLine();

        if (report.FixList.Count > 0)
        {
            md.AppendLine("## Fix-list (ranked by dollars)");
            md.AppendLine();
            md.AppendLine("| # | Tier | Source | Finding | Est. annual savings |");
            md.AppendLine("|---|------|--------|---------|--------------------:|");
            foreach (var item in report.FixList)
            {
                md.AppendLine(
                    $"| {item.Rank} | {item.Tier} | {item.Source} | " +
                    $"{EscapePipe(item.Title)} | " +
                    (item.EstimatedAnnualSavings.HasValue ? $"${item.EstimatedAnnualSavings.Value:N0}" : "—") +
                    " |");
            }

            md.AppendLine();
            md.AppendLine("## Detail");
            md.AppendLine();
            foreach (var item in report.FixList)
            {
                md.AppendLine($"### {item.Rank}. {item.Title}");
                if (!string.IsNullOrEmpty(item.ResourceId)) md.AppendLine($"- Resource: `{item.ResourceId}`");
                if (!string.IsNullOrEmpty(item.Detail)) md.AppendLine($"- {item.Detail}");
                if (!string.IsNullOrEmpty(item.Remediation)) md.AppendLine($"- Fix: {item.Remediation}");
                if (item.EstimatedAnnualSavings.HasValue)
                    md.AppendLine($"- Estimated annual savings: ${item.EstimatedAnnualSavings.Value:N0}");
                md.AppendLine();
            }
        }
        else
        {
            md.AppendLine("## Fix-list");
            md.AppendLine();
            md.AppendLine("No findings recorded during the watch window.");
        }

        return md.ToString();
    }

    public static string ToCsv(AssessmentReport report)
    {
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("rank,tier,source,title,resource_id,estimated_annual_savings,severity,first_seen");
        foreach (var item in report.FixList)
        {
            csv.AppendLine(string.Join(',',
                item.Rank,
                item.Tier,
                EscapeCsv(item.Source),
                EscapeCsv(item.Title),
                EscapeCsv(item.ResourceId ?? string.Empty),
                item.EstimatedAnnualSavings?.ToString("0.##") ?? string.Empty,
                EscapeCsv(item.Severity),
                item.FirstSeenAt.ToString("yyyy-MM-dd")));
        }
        return csv.ToString();
    }

    private static string ResolveState(EstateInputs inputs)
    {
        if (inputs.AssessmentCompletedAt.HasValue) return "completed";
        if (inputs.AssessmentStartedAt.HasValue) return "watching";
        return "not_started";
    }

    private static string EscapePipe(string s) => s.Replace("|", "\\|");

    private static string EscapeCsv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
}
