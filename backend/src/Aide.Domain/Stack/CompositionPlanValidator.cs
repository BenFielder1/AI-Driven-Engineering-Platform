using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Specs;

namespace Aide.Domain.Stack;

/// <summary>A plan rule error: <c>INVALID_PLAN</c> or <c>STACK_CHANGE_UNPLANNED</c>, with its reason.</summary>
public sealed record PlanIssue(string Code, string Message);

/// <summary>
/// FR-124, FR-127, v2 §4.11 with the rules generated from the composition (<see cref="StackResolution.PlanRules"/>),
/// checked on the normalised steps after the v1 rules (<see cref="PlanValidator"/>). Errors:
/// <list type="bullet">
/// <item><see cref="AgentErrors"/>: an unknown or missing <c>agent</c> (<c>INVALID_PLAN</c>; <see cref="PlanValidator"/>
/// reports the same through <see cref="PlanAgents"/>, so callers use one or the other);</item>
/// <item><see cref="Errors"/> (<c>INVALID_PLAN</c>): the scaffolder runs step 1 of an empty repository and no other step
/// (except the first step of a stack change); hard order rules (scaffolder steps exempt); exclusivity (never soft; the
/// scaffolder is exempt, resolved out of the rules); per-step file limits (one migration per step);</item>
/// <item><see cref="Errors"/> (<c>STACK_CHANGE_UNPLANNED</c>): a stack change without a step assigned to each changed
/// layer's specialist (when that specialist is in play).</item>
/// </list>
/// Warnings (<see cref="Warnings"/>, never blocking): <c>AGENT_SCOPE</c> (files outside the step agent's owned paths),
/// <c>PLAN_ORDER</c> (soft order rules), <c>UNDECLARED_GENERATED_FILE</c> (a step declaring an API-surface file under the
/// backend with no step at or after it declaring the contract file). <see cref="AllWarnings"/> adds <c>UNCOVERED_REQUIREMENTS</c> first.
/// </summary>
public static class CompositionPlanValidator
{
    /// <summary>Unknown or missing agents (<c>INVALID_PLAN</c> reasons).</summary>
    public static IReadOnlyList<string> AgentErrors(IReadOnlyList<PlanStep> steps, StackResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(resolution);
        var keys = string.Join(", ", resolution.AgentKeys);
        var reasons = new List<string>();
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Agent))
            {
                reasons.Add($"Step {step.Number} has no \"agent\"; use one of {keys}.");
            }
            else if (resolution.Specialist(step.Agent.Trim()) is null)
            {
                reasons.Add($"Step {step.Number} names the unknown agent '{step.Agent.Trim()}'; this stack's specialists are {keys}.");
            }
        }

        return reasons;
    }

    /// <summary>The composition's plan rule errors (empty when the plan follows them). <paramref name="repoEmpty"/>: the base has no code.</summary>
    public static IReadOnlyList<PlanIssue> Errors(IReadOnlyList<PlanStep> steps, StackResolution resolution, bool repoEmpty)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(resolution);
        var issues = new List<PlanIssue>();
        void Invalid(string message) => issues.Add(new PlanIssue(ErrorCodes.InvalidPlan, message));

        var rules = resolution.PlanRules;
        var scaffolder = rules.Scaffolder;
        if (repoEmpty && steps.Count > 0 && steps[0].Agent != scaffolder)
        {
            Invalid($"The repository is empty, so step 1 must be assigned \"{scaffolder}\" (the skeleton comes first); it is assigned \"{steps[0].Agent ?? "nothing"}\".");
        }

        foreach (var step in steps.Where(s => s.Agent == scaffolder))
        {
            if (step.Number != 1)
            {
                Invalid($"Step {step.Number} is assigned \"{scaffolder}\", which only runs step 1 (of an empty repository or a stack change).");
            }
            else if (!repoEmpty && !resolution.Change.IsStackChange)
            {
                Invalid($"Step 1 is assigned \"{scaffolder}\", but the repository already has code and the stack doesn't change; assign the step to the specialist of its layer.");
            }
        }

        foreach (var rule in rules.Order.Where(r => !r.Soft))
        {
            foreach (var violation in OrderViolations(steps, rule, scaffolder))
            {
                Invalid(violation);
            }
        }

        foreach (var step in steps)
        {
            foreach (var rule in rules.Exclusive.Where(r => r.Agent == step.Agent))
            {
                var offending = step.Files.Select(f => f.Path).Where(rule.NotWith.IsMatch).ToList();
                if (offending.Count > 0)
                {
                    Invalid($"Step {step.Number} is assigned \"{rule.Agent}\", which must not declare {Join(offending)} ({rule.Reason ?? $"not with {string.Join(", ", rule.NotWith.Patterns)}"}).");
                }
            }

            foreach (var limit in rules.FileLimits)
            {
                var matching = step.Files.Select(f => f.Path).Where(limit.Glob.IsMatch).ToList();
                if (matching.Count > limit.Max)
                {
                    Invalid($"Step {step.Number} declares {matching.Count} files matching {limit.Glob.Pattern} ({Join(matching)}); {limit.Pack} allows at most {limit.Max} per step.");
                }
            }
        }

        if (resolution.Change.IsStackChange)
        {
            foreach (var layer in resolution.Change.LayersChanged)
            {
                if (StackSpecialistCatalogue.ForLayer(layer) is { } key && resolution.Specialist(key) is not null && steps.All(s => s.Agent != key))
                {
                    issues.Add(new PlanIssue(ErrorCodes.StackChangeUnplanned, $"The plan must include work for the {layer} change: no step is assigned \"{key}\"."));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// The error for <paramref name="issues"/>, or <c>null</c> when there are none: <c>INVALID_PLAN</c> when any issue is one,
    /// else <c>STACK_CHANGE_UNPLANNED</c>; every reason is in the details.
    /// </summary>
    public static DomainError? ToError(IReadOnlyList<PlanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0)
        {
            return null;
        }

        var code = issues.Any(i => i.Code == ErrorCodes.InvalidPlan) ? ErrorCodes.InvalidPlan : ErrorCodes.StackChangeUnplanned;
        var first = issues.First(i => i.Code == code);
        return new DomainError(code, $"The plan is invalid: {first.Message}") { Details = [.. issues.Select(i => i.Message)] };
    }

    /// <summary>
    /// The composition warnings: <c>AGENT_SCOPE</c>, <c>PLAN_ORDER</c>, <c>UNDECLARED_GENERATED_FILE</c> (at most one of each, in
    /// that order). §4.23: a step mapped from a v2 key (<see cref="PlanStep.LegacyAgent"/>) is checked against the frozen v2
    /// globs of <paramref name="legacy"/> (the migrated version's preset) instead of its specialist's.
    /// </summary>
    public static IReadOnlyList<ValidationWarning> Warnings(IReadOnlyList<PlanStep> steps, StackResolution resolution, LegacyPreset? legacy = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(resolution);
        var warnings = new List<ValidationWarning>();

        var outside = new List<string>();
        foreach (var step in steps)
        {
            if (step.LegacyAgent is { } legacyKey && legacy?.OwnsOf(legacyKey) is { } frozen)
            {
                var outsideFrozen = step.Files.Select(f => f.Path).Where(p => !frozen.IsMatch(p)).ToList();
                if (outsideFrozen.Count > 0)
                {
                    outside.Add($"Step {step.Number} ({legacyKey}) declares {Join(outsideFrozen)} outside its owned paths ({string.Join(", ", frozen.Patterns)}).");
                }

                continue;
            }

            if (resolution.Specialist(step.Agent) is not { } agent)
            {
                continue; // an unknown agent is an INVALID_PLAN error, not a scope warning
            }

            var files = step.Files.Select(f => f.Path).Where(p => !agent.Owns.IsMatch(p)).ToList();
            if (files.Count > 0)
            {
                outside.Add($"Step {step.Number} ({agent.Key}) declares {Join(files)} outside its owned paths ({string.Join(", ", agent.OwnedGlobs)}).");
            }
        }

        if (outside.Count > 0)
        {
            warnings.Add(new ValidationWarning(ErrorCodes.AgentScope, $"{outside.Count} step(s) declare files outside their specialist's owned paths.", outside));
        }

        var soft = resolution.PlanRules.Order.Where(r => r.Soft)
            .SelectMany(r => OrderViolations(steps, r, resolution.PlanRules.Scaffolder))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (soft.Count > 0)
        {
            warnings.Add(new ValidationWarning(ErrorCodes.PlanOrder, $"{soft.Count} step(s) break a recommended order of the stack's packs.", soft));
        }

        if (UndeclaredGenerated(steps, resolution) is { } undeclared)
        {
            warnings.Add(undeclared);
        }

        return warnings;
    }

    /// <summary>Every plan warning: <c>UNCOVERED_REQUIREMENTS</c> (§4.13, when <paramref name="requirements"/> are given), then <see cref="Warnings"/>.</summary>
    public static IReadOnlyList<ValidationWarning> AllWarnings(IReadOnlyList<PlanStep> steps, IEnumerable<SpecRequirement>? requirements, StackResolution resolution, LegacyPreset? legacy = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(resolution);
        var warnings = new List<ValidationWarning>();
        if (requirements is not null && PlanScope.Check(steps, requirements) is { } uncovered)
        {
            warnings.Add(uncovered);
        }

        warnings.AddRange(Warnings(steps, resolution, legacy));
        return warnings;
    }

    /// <summary>FR-127: steps changing the API surface with no step at or after them declaring the contract file.</summary>
    private static ValidationWarning? UndeclaredGenerated(IReadOnlyList<PlanStep> steps, StackResolution resolution)
    {
        if (resolution.ContractFiles.Count == 0)
        {
            return null;
        }

        var details = new List<string>();
        var files = new List<string>();
        foreach (var contract in resolution.ContractFiles)
        {
            var glob = StackGlobSet.Glob(contract);
            bool Declares(PlanStep s) => s.Files.Any(f => glob?.IsMatch(f.Path) ?? f.Path == contract);
            foreach (var step in steps)
            {
                var surface = step.Files.Select(f => f.Path).Where(resolution.ApiSurface.IsMatch).ToList();
                if (surface.Count == 0 || steps.Any(s => s.Number >= step.Number && Declares(s)))
                {
                    continue;
                }

                details.Add($"Step {step.Number} changes the API ({Join(surface)}) but no step at or after it declares {contract}.");
                if (!files.Contains(contract, StringComparer.Ordinal))
                {
                    files.Add(contract);
                }
            }
        }

        return details.Count == 0
            ? null
            : new ValidationWarning(
                ErrorCodes.UndeclaredGeneratedFile,
                $"{string.Join(", ", files)} is generated from this change but no step declares it.",
                details);
    }

    private static IEnumerable<string> OrderViolations(IReadOnlyList<PlanStep> steps, StackOrderRule rule, string scaffolder)
    {
        // As v2: for each step matching "after", every later step matching "before" is out of order (once per pair).
        // Scaffolder steps are exempt: the skeleton comes first by design.
        foreach (var early in steps.Where(s => s.Agent != scaffolder))
        {
            var afterFiles = early.Files.Select(f => f.Path).Where(rule.After.IsMatch).ToList();
            if (afterFiles.Count == 0)
            {
                continue;
            }

            foreach (var late in steps.Where(s => s.Number > early.Number && s.Agent != scaffolder))
            {
                var beforeFiles = late.Files.Select(f => f.Path).Where(rule.Before.IsMatch).ToList();
                if (beforeFiles.Count > 0)
                {
                    yield return $"Step {late.Number} declares {Join(beforeFiles)}, which must come before step {early.Number} ({Join(afterFiles)}): {rule.Reason ?? $"{string.Join(", ", rule.Before.Patterns)} before {string.Join(", ", rule.After.Patterns)}"}";
                }
            }
        }
    }

    private static string Join(List<string> paths) =>
        paths.Count <= 3 ? string.Join(", ", paths.Select(p => $"'{p}'")) : $"{string.Join(", ", paths.Take(3).Select(p => $"'{p}'"))} and {paths.Count - 3} more";
}
