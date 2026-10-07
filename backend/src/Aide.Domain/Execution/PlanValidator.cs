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

/// <summary>FR-29: structural validation of a generated plan. Failures are <c>INVALID_PLAN</c> with every reason.</summary>
public static class PlanValidator
{
    /// <summary>Validates and returns the plan with declared paths normalised.</summary>
    public static Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var reasons = new List<string>();

        if (steps.Count == 0)
        {
            reasons.Add("The plan has no steps.");
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

            var files = new List<DeclaredFile>(step.Files.Count);
            var seenInStep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in step.Files)
            {
                var path = PathRules.Normalize(file.Path);
                if (PathRules.IsUnsafe(path))
                {
                    reasons.Add($"Step {step.Number} declares an invalid path '{file.Path}'.");
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

            normalised.Add(step with { Files = files });
        }

        return reasons.Count == 0
            ? Result.Success<IReadOnlyList<PlanStep>>(normalised)
            : Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reasons[0]}") { Details = reasons });
    }
}
