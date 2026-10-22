using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Specs;

namespace Aide.Domain.Presets;

/// <summary>
/// FR-76, §4.11: the preset plan rules, checked after the v1 rules on the normalised steps (<see cref="PlanValidator"/>
/// calls <see cref="Errors"/> when <see cref="PlanAgents.Preset"/> is set). Errors (part of <c>INVALID_PLAN</c>):
/// <list type="bullet">
/// <item><c>planRules.order</c> (not soft): a step declaring a file matching <c>before</c> must come before every step
/// declaring a file matching <c>after</c> (steps assigned the scaffolder are exempt: the skeleton comes first);</item>
/// <item><c>planRules.exclusive</c>: a step assigned <c>agent</c> must not declare files matching <c>notWithGlobs</c>;</item>
/// <item><c>planRules.scaffoldFirstWhenEmpty</c>: on an empty repository step 1 is assigned the scaffolder;</item>
/// <item><c>planRules.maxFilesPerStep</c> (and its shorthand <c>oneMigrationPerStep</c>): a step declares at most
/// <c>max</c> files matching the glob.</item>
/// </list>
/// Warnings (<see cref="Warnings"/>, never blocking): <c>AGENT_SCOPE</c> for declared files outside the step agent's
/// owned paths and <c>PLAN_ORDER</c> for soft ordering rules. <see cref="AllWarnings"/> adds <c>UNCOVERED_REQUIREMENTS</c>.
/// </summary>
public static class PresetPlanValidator
{
    /// <summary>The preset rule errors of a plan (empty when it follows them).</summary>
    public static IReadOnlyList<string> Errors(IReadOnlyList<PlanStep> steps, Preset preset, bool repoEmpty)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(preset);
        var reasons = new List<string>();
        var rules = preset.PlanRules;

        if (rules.ScaffoldFirstWhenEmpty && repoEmpty && steps.Count > 0 && steps[0].Agent != rules.Scaffolder)
        {
            reasons.Add($"The repository is empty, so step 1 must be assigned \"{rules.Scaffolder}\" (the {preset.Id} preset scaffolds first); it is assigned \"{steps[0].Agent ?? "nothing"}\".");
        }

        foreach (var rule in rules.Order.Where(r => !r.Soft))
        {
            reasons.AddRange(OrderViolations(steps, rule, rules.Scaffolder));
        }

        foreach (var step in steps)
        {
            foreach (var rule in rules.Exclusive.Where(r => r.Agent == step.Agent))
            {
                var offending = step.Files.Select(f => f.Path).Where(rule.NotWith.IsMatch).ToList();
                if (offending.Count > 0)
                {
                    reasons.Add($"Step {step.Number} is assigned \"{rule.Agent}\", which must not declare {Join(offending)} ({Explain(rule.Reason, $"not with {string.Join(", ", rule.NotWith.Patterns)}")}).");
                }
            }

            foreach (var limit in rules.FileLimits)
            {
                var matching = step.Files.Select(f => f.Path).Where(limit.Glob.IsMatch).ToList();
                if (matching.Count > limit.Max)
                {
                    reasons.Add($"Step {step.Number} declares {matching.Count} files matching {limit.Glob.Pattern} ({Join(matching)}); the {preset.Id} preset allows at most {limit.Max} per step.");
                }
            }
        }

        return reasons;
    }

    /// <summary>The preset warnings of a plan: <c>AGENT_SCOPE</c> and <c>PLAN_ORDER</c> (at most one of each, in that order).</summary>
    public static IReadOnlyList<ValidationWarning> Warnings(IReadOnlyList<PlanStep> steps, Preset preset)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(preset);
        var warnings = new List<ValidationWarning>();

        var outside = new List<string>();
        foreach (var step in steps)
        {
            if (preset.Agent(step.Agent) is not { } agent)
            {
                continue; // an unknown agent is an INVALID_PLAN error, not a scope warning
            }

            var files = step.Files.Select(f => f.Path).Where(p => !agent.Owns.IsMatch(p)).ToList();
            if (files.Count > 0)
            {
                outside.Add($"Step {step.Number} ({agent.Key}) declares {Join(files)} outside its owned paths ({string.Join(", ", agent.Owns.Patterns)}).");
            }
        }

        if (outside.Count > 0)
        {
            warnings.Add(new ValidationWarning(
                ErrorCodes.AgentScope,
                $"{outside.Count} step(s) declare files outside their specialist agent's owned paths.",
                outside));
        }

        var soft = preset.PlanRules.Order.Where(r => r.Soft).SelectMany(r => OrderViolations(steps, r, preset.PlanRules.Scaffolder)).ToList();
        if (soft.Count > 0)
        {
            warnings.Add(new ValidationWarning(ErrorCodes.PlanOrder, $"{soft.Count} step(s) break a recommended order of the {preset.Id} preset.", soft));
        }

        return warnings;
    }

    /// <summary>
    /// Every plan warning (FR-76): <c>UNCOVERED_REQUIREMENTS</c> (§4.13, when <paramref name="requirements"/> are given),
    /// then the preset warnings (when <paramref name="preset"/> is set), then FR-89's <c>MISSING_VERCEL_CONFIG</c>
    /// (<see cref="Deployments.VercelConfigRules.PlanWarning"/>; only when <paramref name="baseFiles"/> are known), checked in
    /// the Vercel root directory <paramref name="vercelRootDirectory"/> (<c>null</c>: the repository root).
    /// </summary>
    public static IReadOnlyList<ValidationWarning> AllWarnings(
        IReadOnlyList<PlanStep> steps, IEnumerable<SpecRequirement>? requirements, Preset? preset, IReadOnlySet<string>? baseFiles = null, string? vercelRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var warnings = new List<ValidationWarning>();
        if (requirements is not null && PlanScope.Check(steps, requirements) is { } uncovered)
        {
            warnings.Add(uncovered);
        }

        if (preset is not null)
        {
            warnings.AddRange(Warnings(steps, preset));
            if (Deployments.VercelConfigRules.PlanWarning(steps, preset, baseFiles, vercelRootDirectory) is { } vercel)
            {
                warnings.Add(vercel);
            }
        }

        return warnings;
    }

    private static IEnumerable<string> OrderViolations(IReadOnlyList<PlanStep> steps, PresetOrderRule rule, string scaffolder)
    {
        // For each step matching "after", every later step matching "before" is out of order (reported once per pair).
        // Scaffolder steps are exempt: the skeleton (app/layout.tsx, lib/supabase/**) comes first by design.
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
                    yield return $"Step {late.Number} declares {Join(beforeFiles)}, which must come before step {early.Number} ({Join(afterFiles)}): {Explain(rule.Reason, $"{string.Join(", ", rule.Before.Patterns)} before {string.Join(", ", rule.After.Patterns)}")}.";
                }
            }
        }
    }

    private static string Explain(string? reason, string fallback) => reason ?? fallback;

    private static string Join(List<string> paths) =>
        paths.Count <= 3 ? string.Join(", ", paths.Select(p => $"'{p}'")) : $"{string.Join(", ", paths.Take(3).Select(p => $"'{p}'"))} and {paths.Count - 3} more";
}
