using Aide.Domain.Common;
using Aide.Domain.Presets;

namespace Aide.Domain.Execution;

/// <summary>A file in the code generator's output.</summary>
public sealed record ProducedFile(string Path, string Content);

/// <summary>
/// Everything the Guardian checks a step's output against. With a <see cref="Preset"/> (§4.11) its protected paths join
/// v1's; <see cref="ExistsOnBase"/> answers "does this file exist on the version's base commit" for its
/// <c>existingOnBase</c> entries (unknown: <c>false</c>).
/// </summary>
public sealed record GuardianInput(
    int RequestedStep,
    int OutputStep,
    IReadOnlyList<StepState> PlanSteps,
    IReadOnlyList<ProducedFile> Files,
    int MaxFilesPerStep,
    Preset? Preset = null,
    Func<string, bool>? ExistsOnBase = null);

/// <summary>One violation (§4.6). Messages never include secret values.</summary>
public sealed record GuardianViolation(GuardianCheck Check, string Code, ViolationSeverity Severity, string Message, string? Path = null);

public sealed record GuardianResult(IReadOnlyList<GuardianViolation> Violations)
{
    /// <summary>Any <c>error</c> rejects the step; warnings are only recorded.</summary>
    public bool Accepted => Violations.All(v => v.Severity != ViolationSeverity.Error);

    public IEnumerable<GuardianViolation> Errors => Violations.Where(v => v.Severity == ViolationSeverity.Error);

    public IEnumerable<GuardianViolation> Warnings => Violations.Where(v => v.Severity == ViolationSeverity.Warning);
}

/// <summary>
/// The Execution Guardian (§4.6, FR-32, D18): runs all six checks and reports every violation.
/// Paths are normalised and compared exactly. FR-76, §4.11: a preset adds its protected paths to scope creep.
/// </summary>
public static class ExecutionGuardian
{
    public static GuardianResult Validate(GuardianInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var step = input.PlanSteps.FirstOrDefault(s => s.Number == input.RequestedStep);
        var declared = (step?.DeclaredPaths ?? []).Select(PathRules.Normalize).ToHashSet(StringComparer.Ordinal);
        var produced = input.Files.Select(f => PathRules.Normalize(f.Path)).Distinct(StringComparer.Ordinal).ToList();

        var violations = new List<GuardianViolation>();
        violations.AddRange(SingleStep(input, step));
        violations.AddRange(FileLimit(produced.Count, input.MaxFilesPerStep));
        violations.AddRange(DependencyOrder(input, step));
        violations.AddRange(PlanAlignment(declared, produced));
        violations.AddRange(ScopeCreep(input, declared, produced));
        violations.AddRange(SecretScan(input.Files));
        return new GuardianResult(violations);
    }

    private static IEnumerable<GuardianViolation> SingleStep(GuardianInput input, StepState? step)
    {
        if (input.OutputStep != input.RequestedStep)
        {
            yield return Error(GuardianCheck.SingleStep, ErrorCodes.Guardian.SingleStep, $"The output targets step {input.OutputStep}, but step {input.RequestedStep} was requested.");
        }

        if (step is null)
        {
            yield return Error(GuardianCheck.SingleStep, ErrorCodes.Guardian.SingleStep, $"Step {input.RequestedStep} is not in the plan.");
        }
        else if (step.Status is not (StepStatus.Pending or StepStatus.InProgress))
        {
            yield return Error(GuardianCheck.SingleStep, ErrorCodes.Guardian.SingleStep, $"Step {step.Number} is {step.Status.ToWire()}, not pending or in_progress.");
        }

        if (input.Files.Count == 0)
        {
            yield return Error(GuardianCheck.SingleStep, ErrorCodes.Guardian.SingleStep, "The output contains no files.");
        }

        var duplicates = input.Files
            .GroupBy(f => PathRules.Normalize(f.Path), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal);
        foreach (var path in duplicates)
        {
            yield return Error(GuardianCheck.SingleStep, ErrorCodes.Guardian.DuplicatePath, $"'{path}' appears more than once in the output.", path);
        }
    }

    private static IEnumerable<GuardianViolation> FileLimit(int count, int max)
    {
        if (count > max)
        {
            yield return Error(GuardianCheck.FileLimit, ErrorCodes.Guardian.FileLimit, $"The output touches {count} files; the limit is {max}.");
        }
    }

    private static IEnumerable<GuardianViolation> DependencyOrder(GuardianInput input, StepState? step)
    {
        if (step is null)
        {
            yield break;
        }

        var statuses = input.PlanSteps.ToDictionary(s => s.Number, s => s.Status);
        foreach (var dep in step.Dependencies)
        {
            if (!statuses.TryGetValue(dep, out var status) || status != StepStatus.Completed)
            {
                yield return Error(GuardianCheck.DependencyOrder, ErrorCodes.Guardian.DependencyOrder, $"Dependency step {dep} is not completed.");
            }
        }
    }

    private static IEnumerable<GuardianViolation> PlanAlignment(HashSet<string> declared, List<string> produced)
    {
        foreach (var path in produced.Where(p => !declared.Contains(p)))
        {
            yield return Error(GuardianCheck.PlanAlignment, ErrorCodes.Guardian.PlanAlignment, $"'{path}' is not declared for this step.", path);
        }

        foreach (var path in declared.Where(d => !produced.Contains(d)).Order(StringComparer.Ordinal))
        {
            yield return new GuardianViolation(
                GuardianCheck.PlanAlignment,
                ErrorCodes.Guardian.PlanAlignment,
                ViolationSeverity.Warning,
                $"'{path}' is declared for this step but wasn't produced.",
                path);
        }
    }

    private static IEnumerable<GuardianViolation> ScopeCreep(GuardianInput input, HashSet<string> declared, List<string> produced)
    {
        var ownerByPath = input.PlanSteps
            .Where(s => s.Number != input.RequestedStep)
            .SelectMany(s => s.DeclaredPaths.Select(p => (Path: PathRules.Normalize(p), s.Number)))
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Number), StringComparer.Ordinal);

        foreach (var path in produced)
        {
            if (PathRules.IsUnsafe(path))
            {
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.UnsafePath, $"'{path}' is not a safe repository-relative path.", path);
                continue;
            }

            if (PathRules.IsAgentForbidden(path))
            {
                // Decisions: .aide/ holds the project's rules for AIDE; only humans edit it, so no declaration allows it.
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.ProtectedPath, $"'{path}' is under .aide/, which agents never write.", path);
                continue;
            }

            // §4.11: a preset's immutable files (existingOnBase, e.g. committed migrations) can't be written even when declared.
            if (input.Preset is { } immutablePreset && input.ExistsOnBase is { } existsOnBase
                && immutablePreset.ProtectedPaths.FirstOrDefault(p => p.ExistingOnBase && p.Protects(path, existsOnBase)) is { } immutable)
            {
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.ProtectedPath, $"'{path}' already exists on the version's base commit and is immutable ({immutable.Reason ?? immutable.Glob.Pattern}); write a new file instead.", path);
                continue;
            }

            if (declared.Contains(path))
            {
                continue;
            }

            if (ownerByPath.TryGetValue(path, out var owner))
            {
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.ScopeCreep, $"'{path}' belongs to step {owner}.", path);
            }

            if (PathRules.IsProtected(path))
            {
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.ProtectedPath, $"'{path}' is a protected path and isn't declared for this step.", path);
            }
            else if (input.Preset is { } preset && preset.IsProtected(path, input.ExistsOnBase ?? (_ => false)))
            {
                // §4.11: the preset's protected paths, with the same "unless declared in this step" exception.
                yield return Error(GuardianCheck.ScopeCreep, ErrorCodes.Guardian.ProtectedPath, $"'{path}' is protected by the {preset.Id} preset and isn't declared for this step.", path);
            }
        }
    }

    private static IEnumerable<GuardianViolation> SecretScan(IEnumerable<ProducedFile> files) =>
        files.SelectMany(f => SecretScanner.Scan(f.Content, PathRules.Normalize(f.Path)))
            .Select(s => Error(
                GuardianCheck.SecretScan,
                ErrorCodes.Guardian.SecretDetected,
                $"Possible {s.Pattern} in '{s.Path}' at line {s.Line}.",
                s.Path));

    private static GuardianViolation Error(GuardianCheck check, string code, string message, string? path = null) =>
        new(check, code, ViolationSeverity.Error, message, path);
}
