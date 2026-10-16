using Aide.Domain.Common;
using Aide.Domain.Specs;

namespace Aide.Domain.Execution;

/// <summary>
/// §4.13 plan scope, FR-76: an iteration plan's steps should cover (<see cref="PlanStep.Covers"/>) every <c>[new]</c> and
/// <c>[changed]</c> requirement of the spec. Missing ones are an <c>UNCOVERED_REQUIREMENTS</c> <b>warning</b> (shown and
/// recorded, never blocking). Specs without tags (a first version) have no iteration scope, so nothing is reported.
/// </summary>
public static class PlanScope
{
    /// <summary>The <c>[new]</c>/<c>[changed]</c> IDs of <paramref name="requirements"/> that no step covers, in spec order.</summary>
    public static IReadOnlyList<string> Uncovered(IReadOnlyList<PlanStep> steps, IEnumerable<SpecRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(requirements);
        var covered = steps
            .SelectMany(s => s.Covers)
            .Select(RequirementParser.NormaliseId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return
        [
            .. requirements
                .Where(r => r.IsNewOrChanged && !covered.Contains(r.Id))
                .Select(r => r.Id)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>The <c>UNCOVERED_REQUIREMENTS</c> warning listing the uncovered IDs, or <c>null</c> when every one is covered.</summary>
    public static ValidationWarning? Check(IReadOnlyList<PlanStep> steps, IEnumerable<SpecRequirement> requirements)
    {
        var uncovered = Uncovered(steps, requirements);
        return uncovered.Count == 0
            ? null
            : new ValidationWarning(
                ErrorCodes.UncoveredRequirements,
                $"{uncovered.Count} new or changed requirement(s) are not covered by any plan step: {string.Join(", ", uncovered)}.",
                uncovered);
    }
}
