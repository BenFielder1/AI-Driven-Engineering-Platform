using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Stack;

namespace Aide.Domain.Execution;

/// <summary>
/// FR-243, §4.41: what the add-step rule needs: the composition's required deployment files (<see cref="StackRequiredFiles.For"/>,
/// in catalog order), the files already in the repository (the base commit for a plan, the branch for an optimisation plan;
/// <c>null</c> when unknown: then nothing is added, since a file can't be proven missing), and whether the plan is an
/// optimisation plan (added steps go first) or an implementation plan (added steps go after step 1, the scaffold step).
/// </summary>
public sealed record DeployFileCheck(IReadOnlyList<StackRequiredFile> Required, IReadOnlySet<string>? RepoFiles, bool Optimisation);

/// <summary>
/// FR-243, §4.41: adds a step for each required deployment file that is neither in the repository nor in any step's files.
/// Pure: the caller validates the result (<see cref="PlanValidator"/>, step 4: an added step that breaks a limit is
/// <c>INVALID_PLAN</c>, so an invalid plan is never stored).
/// <list type="number">
/// <item>A file counts as present when its path or an alternative (in its folder) is in the repository or declared by any
/// step (paths compared normalised, ignoring case like the base listing).</item>
/// <item>Each missing file becomes a step: the pack's owner agent, title "Add &lt;path&gt; for &lt;platform&gt;", a task listing
/// the required content, files = [path] (create), depending on the scaffold step (step 1) in a plan, on nothing in an
/// optimisation plan.</item>
/// <item>Added steps go straight after step 1 in a plan, or first in an optimisation plan, in catalog order. Later steps and
/// their dependencies are renumbered; added steps get <c>addedBy: "tevrin"</c> and <c>addedReason</c>.</item>
/// </list>
/// An empty plan (an optimisation plan with no steps) is returned unchanged.
/// </summary>
public static class DeployFileSteps
{
    /// <summary>The required files of <paramref name="check"/> that are neither in the repository nor declared by a step, in order.</summary>
    public static IReadOnlyList<StackRequiredFile> Missing(IReadOnlyList<PlanStep> steps, DeployFileCheck check)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(check);
        if (check.RepoFiles is not { } repo)
        {
            return [];
        }

        var present = new HashSet<string>(repo.Select(PathRules.Normalize), StringComparer.OrdinalIgnoreCase);
        present.UnionWith(steps.SelectMany(s => s.Files).Select(f => PathRules.Normalize(f.Path)));
        return [.. check.Required.Where(f => !f.AcceptedPaths.Any(p => present.Contains(PathRules.Normalize(p))))];
    }

    /// <summary>The plan with a step added for each missing required file (§4.41), or <paramref name="steps"/> unchanged.</summary>
    public static IReadOnlyList<PlanStep> Add(IReadOnlyList<PlanStep> steps, DeployFileCheck check)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(check);
        if (steps.Count == 0)
        {
            return steps;
        }

        var missing = Missing(steps, check);
        if (missing.Count == 0)
        {
            return steps;
        }

        // A plan keeps step 1 (the scaffold step) first; an optimisation plan starts with the added steps.
        var keep = check.Optimisation ? 0 : 1;
        var added = missing.Count;
        int Renumber(int number) => number > keep ? number + added : number;

        var result = new List<PlanStep>(steps.Count + added);
        result.AddRange(steps.Take(keep));
        IReadOnlyList<int> dependsOn = check.Optimisation ? [] : [1];
        result.AddRange(missing.Select((file, i) => Step(file, keep + i + 1, dependsOn)));
        result.AddRange(steps.Skip(keep).Select(s => s with
        {
            Number = Renumber(s.Number),
            Dependencies = [.. s.Dependencies.Select(Renumber)],
        }));
        return result;
    }

    /// <summary>
    /// FR-243, §4.41 step 4: <paramref name="valid"/> (the agent's own steps, already valid) with the missing-file steps added,
    /// validated again by <paramref name="validate"/>. When the added steps make the plan invalid, the <c>INVALID_PLAN</c>
    /// reasons are in the agent's terms: which file Tevrin must add and how to make room for it (its own step numbers are
    /// unchanged), followed by the validator's reasons for the plan with the step inserted.
    /// </summary>
    public static Result<IReadOnlyList<PlanStep>> AddValidated(
        IReadOnlyList<PlanStep> valid, DeployFileCheck check, Func<IReadOnlyList<PlanStep>, Result<IReadOnlyList<PlanStep>>> validate)
    {
        ArgumentNullException.ThrowIfNull(valid);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(validate);
        var missing = valid.Count == 0 ? [] : Missing(valid, check);
        if (missing.Count == 0)
        {
            return Result.Success(valid);
        }

        var result = validate(Add(valid, check));
        if (result.IsSuccess)
        {
            return result;
        }

        var files = string.Join(", ", missing.Select(f => f.Path));
        var include = string.Join("; ", missing.Select(f => $"declare {f.Path} in a {f.Owner} step"));
        var total = valid.Count + missing.Count;
        var cause = total > PlanValidator.MaxSteps
            ? $"the plan has {valid.Count} steps, so adding {(missing.Count == 1 ? "it" : "them")} would exceed {PlanValidator.MaxSteps}"
            : $"the plan with {(missing.Count == 1 ? "that step" : "those steps")} inserted {(check.Optimisation ? "first" : "after step 1")} is invalid";
        var reason = $"Tevrin must add a step for {files} (no step declares it and the repository doesn't have it), but {cause}. "
            + $"Include it yourself ({include}), or leave room for the added step.";
        var details = new List<string> { reason };
        details.AddRange(result.Error!.Details.Select(d => $"With Tevrin's step inserted: {d}"));
        return Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reason}") { Details = details });
    }

    /// <summary>
    /// FR-244: an edited plan keeps <c>addedBy</c> only on steps that match a step Tevrin added in <paramref name="previous"/>
    /// (the version it edits): same agent and same declared paths. The reason is the previous one; any other step's provenance
    /// is removed, so a human edit can't mark a step as added by Tevrin.
    /// </summary>
    public static IReadOnlyList<PlanStep> KeepProvenance(IReadOnlyList<PlanStep> edited, IReadOnlyList<PlanStep> previous)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(previous);
        var added = previous.Where(p => p.AddedBy == PlanStep.AddedByTevrin).ToList();
        return
        [
            .. edited.Select(step =>
            {
                if (step.AddedBy is null)
                {
                    return step;
                }

                var match = added.FirstOrDefault(p => Same(p, step));
                return match is null
                    ? step with { AddedBy = null, AddedReason = null }
                    : step with { AddedBy = PlanStep.AddedByTevrin, AddedReason = match.AddedReason };
            }),
        ];
    }

    private static bool Same(PlanStep a, PlanStep b) =>
        string.Equals(a.Agent ?? PlanAgents.Generalist, b.Agent ?? PlanAgents.Generalist, StringComparison.Ordinal)
        && a.Files.Select(f => PathRules.Normalize(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(b.Files.Select(f => PathRules.Normalize(f.Path)));

    /// <summary>FR-244: the reason shown with the <b>Added by Tevrin</b> badge.</summary>
    public static string Reason(StackRequiredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"{file.Path} was missing from the plan";
    }

    private static PlanStep Step(StackRequiredFile file, int number, IReadOnlyList<int> dependencies)
    {
        var task = new StringBuilder()
            .Append("Create `").Append(file.Path).Append("`, the ").Append(file.Platform).Append(" configuration this project needs to deploy. ")
            .Append("Tevrin added this step because no step of the plan creates it and the repository doesn't have it");
        if (file.Alternatives.Count > 0)
        {
            task.Append(" (or ").Append(string.Join(", ", file.Alternatives.Select(a => $"`{a}`"))).Append(')');
        }

        task.Append(". It must contain:");
        foreach (var line in file.Content)
        {
            task.Append("\n- ").Append(line);
        }

        return new PlanStep(
            number,
            $"Add {file.Path} for {file.Platform}",
            task.ToString(),
            [new DeclaredFile(file.Path, FileAction.Create)],
            dependencies,
            Agent: file.Owner)
        {
            AddedBy = PlanStep.AddedByTevrin,
            AddedReason = Reason(file),
        };
    }
}
