using System.Text.RegularExpressions;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// Verdict of the grounding gate for one model answer.
/// </summary>
public sealed record ChatGroundingVerdict(bool Passed, string? Reason);

/// <summary>
/// Server-side grounding gate for the customer chat (DoD: answers must derive
/// from the injected WORKSPACE CONTEXT, never invent numbers).
///
/// The system prompt forbids fabrication, but prompt-only grounding is not
/// enforceable — a model asked about an out-of-context entity can emit
/// plausible numbers that the tool-call guard correctly passes. This gate is
/// the enforceable layer: it extracts the SPECIFIC claims in the draft answer
/// (figure-like numbers, resource-id shapes, multi-word proper nouns) and
/// requires them to resolve against the injected context block (or, for bare
/// figures, against the user's own message — echoing a user-supplied number is
/// not fabrication).
///
/// Policy:
///   - Any figure claim that does not resolve → ground out. "Never invent
///     numbers" is per-claim, not per-answer.
///   - Entity claims (resource ids, proper nouns) that ALL fail to resolve →
///     ground out: the answer is about something not in the workspace.
///   - Answers with no extractable claims pass (generic guidance), and the
///     known refusal shapes ("I don't know…", the fixed refusal texts) pass
///     unchanged as long as they carry no figure claims.
///
/// Deliberately conservative: a paraphrased or derived figure that is not in
/// the snapshot is grounded out rather than passed. For a no-tools customer
/// chat, a false refusal ("I don't have that in your data") is the safe
/// failure mode; a fabricated figure is the unsafe one.
/// </summary>
public static partial class ChatGroundingGate
{
    /// <summary>Fixed answer substituted when the gate grounds out.</summary>
    public const string GroundedOutText =
        "I don't have that in your workspace data. I can only report what your connected cloud accounts show — " +
        "check the Inventory or Security pages for the full detail.";

    // Numbers with figure significance: counts, dollar figures, percentages,
    // decimals. Matches "3,481", "86,400", "$1,200", "12.5", "20%", but not
    // bare integers inside ids/dates ("vm-prod-01", "2026-09-16" — dates are
    // pre-masked so their parts are not read as figures).
    [GeneratedRegex(@"(?<![\w./-])(?:\$\s?)?\d{1,3}(?:,\d{3})+|\d+\.\d+|\$\s?\d+|\d+\s?%|(?<![\w./-])\b\d{2,}\b(?![\w./-])")]
    private static partial Regex FigureClaim();

    // Resource-id shapes that carry a digit: vm-prod-01, rg-networking-42,
    // /subscriptions/…. Purely alphabetic hyphenated words ("auto-shutdown",
    // "right-sizing") are ordinary vocabulary, not workspace identifiers.
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)+|/[a-z][a-z0-9]*/")]
    private static partial Regex ResourceIdShape();

    // Multi-word proper nouns: "Company B", "Northwind Traders". Single
    // capitalized words are excluded (not a reliable entity signal).
    [GeneratedRegex(@"\b[A-Z][a-zA-Z0-9]+(?:\s+[A-Z][a-zA-Z0-9]+)+\b")]
    private static partial Regex ProperNoun();

    // Years are prose numbers, not data claims.
    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearShape();

    public static ChatGroundingVerdict Evaluate(string? answer, string? contextBlock, string? userMessage = null)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new ChatGroundingVerdict(true, null);

        var context = contextBlock ?? string.Empty;
        var contextHaystack = Normalize(context);
        var userHaystack = Normalize(userMessage ?? string.Empty);

        // Mask dates/times before figure extraction so their numeric parts are
        // not mistaken for data claims.
        var answerMasked = MaskDates(answer);
        var userMasked = MaskDates(userMessage ?? string.Empty);

        // --- Figure claims: every one must resolve. Years are prose. ---
        foreach (Match m in FigureClaim().Matches(answerMasked))
        {
            if (YearShape().IsMatch(m.Value) || FigureResolves(m.Value, contextHaystack, userHaystack))
                continue;

            return new ChatGroundingVerdict(false,
                $@"figure ""{m.Value.Trim()}"" not grounded in context or user message");
        }

        // --- Entity claims: resource-id shapes and proper nouns. ---
        var entityMatches = new List<string>();
        entityMatches.AddRange(ResourceIdShape().Matches(answerMasked)
            .Select(m => m.Value)
            .Where(e => e.Any(char.IsDigit) || e.StartsWith('/')));
        entityMatches.AddRange(ProperNoun().Matches(answerMasked).Select(m => m.Value));

        if (entityMatches.Count > 0)
        {
            // Skip generic multi-word constructs that are not entity claims.
            var entities = entityMatches
                .Where(e => !IsGenericPhrase(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entities.Count > 0)
            {
                var resolved = entities.Where(e => contextHaystack.Contains(Normalize(e))).ToList();
                if (resolved.Count == 0)
                    return new ChatGroundingVerdict(false,
                        $@"entity reference(s) ""{string.Join(""", """, entities.Take(3))}"" not found in context");
            }
        }

        return new ChatGroundingVerdict(true, null);
    }

    private static bool FigureResolves(string figure, string contextHaystack, string userHaystack)
    {
        var variants = new[]
        {
            figure,
            figure.TrimStart('$').TrimEnd('%').Replace(" ", ""),
            figure.TrimStart('$').TrimEnd('%').Replace(",", "").Replace(" ", ""),
        };

        foreach (var variant in variants)
        {
            var needle = Normalize(variant);
            if (contextHaystack.Contains(needle) || userHaystack.Contains(needle))
                return true;
        }

        // Range tolerance: a rounded anchor near the claim ("about 500" for
        // 499/501) resolves; coarse rounding, not invention.
        if (long.TryParse(figure.TrimStart('$').TrimEnd('%').Replace(",", "").Replace(" ", ""), out var n)
            && n >= 100
            && ContextContainsNearbyFigure(contextHaystack, n))
        {
            return true;
        }

        return false;
    }

    /// <summary>Does the context contain any figure within ~2% (min ±1) of n?</summary>
    private static bool ContextContainsNearbyFigure(string contextHaystack, long n)
    {
        foreach (Match m in FigureClaim().Matches(contextHaystack))
        {
            if (!long.TryParse(m.Value.Replace(",", "").TrimStart('$'), out var c))
                continue;
            var tolerance = Math.Max(1, n / 50); // ~2%
            if (Math.Abs(c - n) <= tolerance)
                return true;
        }
        return false;
    }

    private static string Normalize(string text) =>
        text.ToLowerInvariant().Replace("\u00a0", " ");

    private static string MaskDates(string text)
    {
        // yyyy-mm-dd / yyyy/mm/dd and hh:mm(:ss) — swap separators so the
        // numeric parts are not read as standalone figures.
        var masked = DateShape().Replace(text, m => m.Value.Replace('-', '/').Replace(':', '/'));
        masked = TimeShape().Replace(masked, m => m.Value.Replace(':', '/'));
        return masked;
    }

    [GeneratedRegex(@"\b(?:19|20)\d{2}[-/]\d{1,2}[-/]\d{1,2}\b")]
    private static partial Regex DateShape();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\b")]
    private static partial Regex TimeShape();

    /// <summary>Phrases that look like proper nouns but never name workspace data.</summary>
    private static bool IsGenericPhrase(string entity) =>
        entity.Equals("I don't know", StringComparison.OrdinalIgnoreCase)
        || entity.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
        || entity.EndsWith(" CONTEXT", StringComparison.OrdinalIgnoreCase)
        || entity.EndsWith(" CONTEXT.", StringComparison.Ordinal);
}
