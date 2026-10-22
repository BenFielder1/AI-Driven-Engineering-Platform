using Aide.Domain.Entities;
using Aide.Domain.Specs;

namespace Aide.Domain.Review;

/// <summary>The review agent's reply narrowed to an iteration's scope, and the regressions it found.</summary>
public sealed record ScopedReview(CodeReviewResult Agent, IReadOnlyList<RegressionEntry> Regressions);

/// <summary>A scored review cycle (<see cref="ReviewAssessment.Assess"/> of the scoped reply) with its regressions.</summary>
public sealed record IterationReviewResult(
    ReviewAssessmentResult Assessment,
    CodeReviewResult ScopedAgent,
    IReadOnlyList<RegressionEntry> Regressions);

/// <summary>
/// §4.13 review scope (amends §4.7), D-V2-4. In an iteration (a spec whose requirements carry tags):
/// <list type="bullet">
/// <item>Feature coverage is about <c>[new]</c> and <c>[changed]</c> requirements only: a missing feature naming one of
/// them stays; one naming an <c>[unchanged]</c> requirement is a regression instead; one naming a <c>[removed]</c>
/// requirement is dropped.</item>
/// <item>A reported regression of an <c>[unchanged]</c> requirement is kept (once per ID) and becomes an <b>agent</b>
/// violation (severity major) in its category plus a drift area <c>Regression: FR-n</c> with severity <c>major</c>.
/// Agent violations carry no deterministic penalty (§4.7), so a regression only moves the score through the agent's own
/// category scores and the drift level.</item>
/// <item>A reported "regression" of a <c>[new]</c>/<c>[changed]</c> requirement is a missing feature; one of a
/// <c>[removed]</c> or unknown ID is dropped.</item>
/// </list>
/// Weights, penalties, the decision table (§4.5) and drift levels are unchanged. Outside iterations (no tagged
/// requirements) the reply is used as it is and there are no regressions.
/// </summary>
public static class IterationReviewScope
{
    /// <summary>The drift area prefix of a regression.</summary>
    public const string RegressionAreaPrefix = "Regression: ";

    public static ScopedReview Scope(CodeReviewResult agent, IReadOnlyList<SpecRequirement>? requirements)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var tags = (requirements ?? [])
            .Where(r => r.Tag is not null)
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (tags.Count == 0)
        {
            return new ScopedReview(agent with { Regressions = [] }, []);
        }

        var regressions = new List<RegressionEntry>();
        var missing = new List<string>();
        foreach (var line in agent.MissingFeatures)
        {
            var id = RequirementParser.LeadingId(line);
            var tag = id is not null && tags.TryGetValue(id, out var requirement) ? requirement.Tag : null;
            switch (tag)
            {
                case RequirementTag.Unchanged:
                    AddRegression(new RegressionEntry(id!, ReviewCategory.FeatureCoverage, line, null));
                    break;
                case RequirementTag.Removed:
                    break;
                default:
                    missing.Add(line);
                    break;
            }
        }

        foreach (var reported in agent.Regressions)
        {
            var tag = tags.TryGetValue(reported.RequirementId, out var requirement) ? requirement.Tag : null;
            switch (tag)
            {
                case RequirementTag.Unchanged:
                    AddRegression(reported);
                    break;
                case RequirementTag.New or RequirementTag.Changed:
                    if (!missing.Any(m => string.Equals(RequirementParser.LeadingId(m), reported.RequirementId, StringComparison.Ordinal)))
                    {
                        missing.Add($"{reported.RequirementId}: {reported.Description}");
                    }

                    break;
                default:
                    break;
            }
        }

        var violations = agent.Violations.Concat(regressions.Select(r => new ReviewFinding(
            r.Category,
            Severity.Major,
            $"Regression of {r.RequirementId}{TitleOf(tags, r.RequirementId)}: {r.Description}",
            null,
            FindingSource.Agent,
            r.Evidence)));
        var drift = agent.DriftAreas.Concat(regressions.Select(r => new DriftAreaEntry(
            RegressionAreaPrefix + r.RequirementId,
            Severity.Major,
            r.Description)));
        var scoped = agent with
        {
            Violations = [.. violations],
            DriftAreas = [.. drift],
            MissingFeatures = missing,
            Regressions = regressions,
        };
        return new ScopedReview(scoped, regressions);

        void AddRegression(RegressionEntry entry)
        {
            if (!regressions.Any(r => string.Equals(r.RequirementId, entry.RequirementId, StringComparison.Ordinal)))
            {
                regressions.Add(entry);
            }
        }
    }

    /// <summary>
    /// <see cref="Scope"/> then <see cref="ReviewAssessment.Assess"/>: the cycle's scored report, the scoped reply (store its
    /// violations, missing features and drift areas) and the regressions (store them in <c>compliance_report.regressions</c>).
    /// </summary>
    public static IterationReviewResult Assess(ReviewAssessmentInput input, IReadOnlyList<SpecRequirement>? requirements)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scoped = Scope(input.Agent, requirements);
        var assessment = ReviewAssessment.Assess(input with { Agent = scoped.Agent });
        return new IterationReviewResult(assessment, scoped.Agent, scoped.Regressions);
    }

    private static string TitleOf(Dictionary<string, SpecRequirement> tags, string id) =>
        tags.TryGetValue(id, out var requirement) && requirement.Title.Length > 0 ? $" ({requirement.Title})" : string.Empty;
}
