using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>A file a plan step declares it will create or modify.</summary>
public sealed record DeclaredFile(string Path, FileAction Action);

/// <summary>One step of a structured plan (FR-29, D6).</summary>
public sealed record PlanStep(
    int Number,
    string Title,
    string Task,
    IReadOnlyList<DeclaredFile> Files,
    IReadOnlyList<int> Dependencies,
    string? UserInstructions = null);

/// <summary>
/// FR-29: structural validation of a plan (generated, or edited by a human, FR-18). Failures are <c>INVALID_PLAN</c>
/// with every reason in the details. The rules: steps numbered 1..n in order (at most <see cref="MaxSteps"/>), each with
/// a title, a task and at least one declared file (at most <c>maxFilesPerStep</c> when given); dependencies point to
/// earlier steps, once each; paths are safe (<see cref="PathRules.IsUnsafe"/>: no escape, no <c>.git</c>, no control
/// characters, line breaks or backticks) and declared once per step; a file is created by one step only, and a step that
/// modifies a file created in the plan comes after, and depends (directly or indirectly) on, the step that creates it.
/// <para>
/// Protected paths (<see cref="PathRules.IsProtected"/>) depend on who wrote the plan: an Owner-edited plan may declare
/// them (§4.6 lets a declared step write one), a generated plan may not (<c>allowProtectedPaths: false</c>), so a
/// prompt-injected agent can't plan its way into CI workflows, lockfiles or env files. No plan may declare a path under
/// <c>.aide/</c> (<see cref="PathRules.IsAgentForbidden"/>): the Guardian rejects agent output there even when declared.
/// </para>
/// </summary>
public static class PlanValidator
{
    /// <summary>The most steps a plan may have.</summary>
    public const int MaxSteps = 100;

    /// <summary>The longest step title, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Validates and returns the plan with declared paths normalised.</summary>
    public static Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps, int? maxFilesPerStep = null, bool allowProtectedPaths = true)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var reasons = new List<string>();

        if (steps.Count == 0)
        {
            reasons.Add("The plan has no steps.");
        }

        if (steps.Count > MaxSteps)
        {
            reasons.Add($"The plan has {steps.Count} steps; at most {MaxSteps} are allowed.");
        }

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalised = new List<PlanStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var expected = i + 1;
            if (step.Number != expected)
            {
                reasons.Add($"Steps must be numbered 1..n in order: position {expected} has number {step.Number}.");
            }

            if (string.IsNullOrWhiteSpace(step.Title))
            {
                reasons.Add($"Step {step.Number} has no title.");
            }
            else if (step.Title.Trim().Length > MaxTitleLength)
            {
                reasons.Add($"Step {step.Number} has a title longer than {MaxTitleLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(step.Task))
            {
                reasons.Add($"Step {step.Number} has no task.");
            }

            foreach (var dep in step.Dependencies)
            {
                if (dep >= step.Number || dep < 1)
                {
                    reasons.Add($"Step {step.Number} depends on step {dep}; dependencies must point to earlier steps.");
                }
            }

            if (step.Dependencies.Distinct().Count() != step.Dependencies.Count)
            {
                reasons.Add($"Step {step.Number} lists a dependency more than once.");
            }

            if (step.Files.Count == 0)
            {
                reasons.Add($"Step {step.Number} declares no files; every step must create or modify at least one file.");
            }

            if (maxFilesPerStep is { } max && step.Files.Count > max)
            {
                reasons.Add($"Step {step.Number} declares {step.Files.Count} files; at most {max} are allowed per step.");
            }

            var files = new List<DeclaredFile>(step.Files.Count);
            var seenInStep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in step.Files)
            {
                var path = PathRules.Normalize(file.Path);
                if (PathRules.HasUnsafeCharacters(path))
                {
                    reasons.Add($"Step {step.Number} declares an invalid path '{Display(path)}': paths may not contain control characters, line breaks or backticks.");
                    continue;
                }

                if (PathRules.IsUnsafe(path))
                {
                    reasons.Add($"Step {step.Number} declares an invalid path '{file.Path}'.");
                    continue;
                }

                if (!allowProtectedPaths && PathRules.IsProtected(path))
                {
                    reasons.Add($"Step {step.Number} declares the protected path '{path}'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files).");
                    continue;
                }

                if (PathRules.IsAgentForbidden(path))
                {
                    reasons.Add($"Step {step.Number} declares '{path}' under .aide/, which only humans edit; agents never write it.");
                    continue;
                }

                if (!seenInStep.Add(path))
                {
                    reasons.Add($"Step {step.Number} declares '{path}' more than once.");
                    continue;
                }

                if (file.Action == FileAction.Create)
                {
                    if (created.TryGetValue(path, out var firstStep))
                    {
                        reasons.Add($"'{path}' is declared as created by step {firstStep} and again by step {step.Number}.");
                    }
                    else
                    {
                        created[path] = step.Number;
                    }
                }

                files.Add(file with { Path = path });
            }

            normalised.Add(step with { Title = step.Title.Trim(), Task = step.Task.Trim(), Files = files });
        }

        reasons.AddRange(ModifyOrderReasons(normalised, created));

        return reasons.Count == 0
            ? Result.Success<IReadOnlyList<PlanStep>>(normalised)
            : Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reasons[0]}") { Details = reasons });
    }

    /// <summary>The path with control characters shown as <c>\uXXXX</c>, so a reason stays on one line.</summary>
    private static string Display(string path) =>
        string.Concat(path.Select(c => char.IsControl(c) ? "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));

    /// <summary>A step that modifies a file created in the plan must come after, and depend on, the step creating it.</summary>
    private static IEnumerable<string> ModifyOrderReasons(IReadOnlyList<PlanStep> steps, Dictionary<string, int> created)
    {
        // Transitive dependencies, built in order (dependencies only point to earlier steps once the plan is valid).
        var closure = new Dictionary<int, HashSet<int>>();
        foreach (var step in steps)
        {
            var all = new HashSet<int>();
            foreach (var dep in step.Dependencies.Where(d => d >= 1 && d < step.Number))
            {
                all.Add(dep);
                if (closure.TryGetValue(dep, out var inherited))
                {
                    all.UnionWith(inherited);
                }
            }

            closure.TryAdd(step.Number, all);
            foreach (var file in step.Files.Where(f => f.Action == FileAction.Modify))
            {
                if (!created.TryGetValue(file.Path, out var creator) || creator == step.Number)
                {
                    continue;
                }

                if (creator > step.Number)
                {
                    yield return $"Step {step.Number} modifies '{file.Path}' before step {creator} creates it.";
                }
                else if (!all.Contains(creator))
                {
                    yield return $"Step {step.Number} modifies '{file.Path}', created by step {creator}, but doesn't depend on step {creator}.";
                }
            }
        }
    }
}
