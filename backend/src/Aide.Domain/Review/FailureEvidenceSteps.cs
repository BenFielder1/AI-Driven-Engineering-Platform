using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Stack;

namespace Aide.Domain.Review;

/// <summary>FR-239: a failure finding as the optimisation plan needs it: its id, message and excerpt (§4.38).</summary>
public sealed record FailureEvidence(string Id, string Message, string Excerpt);

/// <summary>
/// FR-239: what attaching evidence needs: this cycle's failures, the files on the reviewed branch (<c>null</c> when unknown:
/// then no new step is added, since its files can't be proven to exist) and the project's max files per step.
/// </summary>
public sealed record FailureEvidenceCheck(IReadOnlyList<FailureEvidence> Failures, IReadOnlySet<string>? RepoFiles, int? MaxFilesPerStep = null);

/// <summary>FR-239: the plan with the evidence attached, and the failure ids whose evidence couldn't be attached (to log).</summary>
public sealed record FailureAttachment(IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Dropped);

/// <summary>
/// FR-239: after the review's optimisation plan is parsed (and the agent's own steps validated), Tevrin appends a
/// <b>Failure evidence</b> block (failure id, source, excerpt) to each step that names a failure id. A failure no step
/// names gets a new step "Fix: &lt;finding message&gt;" at the end of the plan with the block; its owner follows §4.40's last
/// paragraph (a preview or deploy failure: <c>deploy-engineer</c>; a CI check: the specialist owning the files in its
/// annotations or log, else <c>ci-engineer</c>; <c>generalist</c> without a stack or when the specialist isn't in the
/// composition), and its files are the branch files the excerpt names. When no file can be named, the step would break a
/// plan rule, or the plan already has <see cref="PlanValidator.MaxSteps"/> steps, the block goes to the plan's first step
/// instead, so the plan stays valid. A block that would push a step's text over <see cref="MaxTaskLength"/> has its excerpt
/// cut from the top (<c>… N lines omitted</c>). Blocks an agent copied from an earlier plan are removed first. Pure.
/// </summary>
public static partial class FailureEvidenceSteps
{
    /// <summary>The first line of every evidence block; the agent's text ends before the first one.</summary>
    public const string Marker = "**Failure evidence**";

    /// <summary>The line before a block Tevrin put on step 1 because no step named the failure.</summary>
    public const string AlsoFix = "Also fix failure:";

    /// <summary>The longest step text (task) once evidence is attached.</summary>
    public const int MaxTaskLength = 16_000;

    /// <summary>The most files a new fix step declares.</summary>
    public const int MaxNewStepFiles = 5;

    /// <summary>The failure findings of <paramref name="findings"/> (those with a failure id and evidence).</summary>
    public static IReadOnlyList<FailureEvidence> From(IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var result = new List<FailureEvidence>();
        foreach (var f in findings)
        {
            if (f.FailureId is { } id && result.All(r => r.Id != id))
            {
                result.Add(new FailureEvidence(id, f.Message, f.Evidence ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>The step text without Tevrin's evidence blocks (everything from the first <see cref="Marker"/> line on).</summary>
    public static string StripBlocks(string task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var cuts = new[] { Marker, AlsoFix }
            .Select(m => task.StartsWith(m, StringComparison.Ordinal) ? 0 : task.IndexOf("\n" + m, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .ToList();
        return cuts.Count == 0 ? task : task[..cuts.Min()].TrimEnd();
    }

    /// <summary>
    /// The block for <paramref name="failure"/> in at most <paramref name="maxLength"/> characters (the excerpt cut from the
    /// top when needed), or <c>null</c> when not even the block's header fits.
    /// </summary>
    public static string? Block(FailureEvidence failure, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var header = $"{Marker} (added by Tevrin)\nFailure id: {failure.Id}\nSource: {FailureIds.SourceLabel(failure.Id)}\nExcerpt:\n";
        var room = maxLength - header.Length - 1;
        if (room < 40)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(failure.Excerpt))
        {
            return header + "\n    (no excerpt)";
        }

        // Indented four spaces: a code block in Markdown that no step text rule (fences) touches; a cut is noted on the first line.
        var excerpt = failure.Excerpt.ReplaceLineEndings("\n").Trim('\n');
        var indented = Indent(excerpt);
        if (indented.Length > room)
        {
            var lines = excerpt.Split('\n');
            var start = 0;
            do
            {
                start++;
                indented = Indent(FailureExcerpt.Omitted(start) + "\n" + string.Join('\n', lines.Skip(start)));
            }
            while (indented.Length > room && start < lines.Length - 1);

            if (indented.Length > room)
            {
                indented = indented[..room];
            }
        }

        return header + "\n" + indented;
    }

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? string.Empty : "    " + l));

    /// <summary>
    /// The plan with the evidence blocks attached (see the type summary). <paramref name="valid"/> is the agent's validated
    /// plan; <paramref name="validate"/> validates a changed plan. An empty plan, or no failures, is returned unchanged.
    /// </summary>
    public static FailureAttachment Attach(
        IReadOnlyList<PlanStep> valid,
        FailureEvidenceCheck check,
        PlanAgents? agents,
        Func<IReadOnlyList<PlanStep>, Result<IReadOnlyList<PlanStep>>> validate)
    {
        ArgumentNullException.ThrowIfNull(valid);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(validate);
        if (valid.Count == 0 || check.Failures.Count == 0)
        {
            return new FailureAttachment(valid, []);
        }

        var steps = valid.Select(s => s.Task.Contains(Marker, StringComparison.Ordinal) || s.Task.Contains(AlsoFix, StringComparison.Ordinal) ? s with { Task = StripBlocks(s.Task) } : s).ToList();
        var named = steps.Select(s => s.Title + "\n" + s.Task).ToList();
        var unmatched = new List<FailureEvidence>();
        var dropped = new List<string>();
        foreach (var failure in check.Failures)
        {
            var matched = false;
            for (var i = 0; i < steps.Count; i++)
            {
                if (FailureIds.IsNamedIn(named[i], failure.Id))
                {
                    steps[i] = Append(steps[i], failure, dropped);
                    matched = true;
                }
            }

            if (!matched)
            {
                unmatched.Add(failure);
            }
        }

        foreach (var failure in unmatched)
        {
            var step = steps.Count < PlanValidator.MaxSteps ? NewStep(failure, steps.Count + 1, check, agents) : null;
            if (step is not null && validate([.. steps, step]).IsSuccess)
            {
                steps.Add(step);
            }
            else
            {
                steps[0] = Append(steps[0], failure, dropped, alsoFix: true);
            }
        }

        var result = validate(steps);
        return result.IsSuccess
            ? new FailureAttachment(result.Value, [.. dropped.Distinct(StringComparer.Ordinal)])
            : new FailureAttachment(valid, [.. check.Failures.Select(f => f.Id)]);
    }

    /// <summary>FR-244: the reason shown with the <b>Added by Tevrin</b> badge of a fix step Tevrin added.</summary>
    public static string Reason(string failureId) => $"No step addressed {failureId}";

    /// <summary>The paths the excerpt names that exist on the branch (exact, or as a suffix of a branch path), in order.</summary>
    public static IReadOnlyList<string> NamedFiles(string excerpt, IReadOnlySet<string> repoFiles, int max = MaxNewStepFiles)
    {
        ArgumentNullException.ThrowIfNull(excerpt);
        ArgumentNullException.ThrowIfNull(repoFiles);
        var normalised = repoFiles.Select(PathRules.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var result = new List<string>();
        foreach (Match m in PathToken().Matches(excerpt))
        {
            var candidate = PathRules.Normalize(m.Groups["path"].Value.TrimStart('.', '/'));
            if (candidate.Length == 0 || candidate.Contains("node_modules/", StringComparison.OrdinalIgnoreCase) || PathRules.IsUnsafe(candidate))
            {
                continue;
            }

            // A bare file name counts only when exactly one branch file has it.
            var suffixed = normalised.Where(p => p.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            var found = normalised.FirstOrDefault(p => p.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                ?? (candidate.Contains('/', StringComparison.Ordinal) || suffixed.Count == 1 ? suffixed.FirstOrDefault() : null);
            if (found is not null && !result.Contains(found, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(found);
                if (result.Count >= max)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// §4.40: the agent of a new fix step: a preview or deploy failure goes to the deploy-engineer, a CI check to the
    /// specialist owning its first file, else the ci-engineer; the generalist when there is no stack or that agent isn't in
    /// the composition.
    /// </summary>
    public static string? Owner(string failureId, IReadOnlyList<string> files, PlanAgents? agents)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (agents?.Stack is not { } stack)
        {
            return agents?.DefaultAgent;
        }

        string? wanted;
        if (FailureIds.SourceOf(failureId) == FailureIds.Check)
        {
            wanted = files.Select(f => stack.Specialists.FirstOrDefault(s => s.Owns.IsMatch(f))?.Key).FirstOrDefault(k => k is not null)
                ?? StackAgents.CiEngineer;
        }
        else
        {
            wanted = StackAgents.DeployEngineer;
        }

        return agents.Allows(wanted) ? wanted : agents.DefaultAgent;
    }

    private static PlanStep Append(PlanStep step, FailureEvidence failure, List<string> dropped, bool alsoFix = false)
    {
        var task = step.Task.TrimEnd();
        var lead = alsoFix ? $"Also fix {failure.Id}: {OneLine(failure.Message)}\n\n" : string.Empty;
        var block = Block(failure, MaxTaskLength - task.Length - 2 - lead.Length);
        if (block is null)
        {
            dropped.Add(failure.Id);
            return step;
        }

        return step with { Task = task + "\n\n" + lead + block };
    }

    private static PlanStep? NewStep(FailureEvidence failure, int number, FailureEvidenceCheck check, PlanAgents? agents)
    {
        if (check.RepoFiles is not { } repo)
        {
            return null;
        }

        var files = NamedFiles(failure.Excerpt, repo, Math.Min(MaxNewStepFiles, check.MaxFilesPerStep ?? MaxNewStepFiles));
        if (files.Count == 0)
        {
            return null;
        }

        var title = "Fix: " + OneLine(failure.Message);
        if (title.Length > PlanValidator.MaxTitleLength)
        {
            title = title[..(PlanValidator.MaxTitleLength - 1)] + "…";
        }

        var task = new StringBuilder()
            .Append("Fix the failure ").Append(failure.Id).Append(" (").Append(OneLine(failure.Message)).Append("). ")
            .Append("Tevrin added this step because no step of the review's plan named it. Read the first error line of the evidence below, ")
            .Append("fix its cause in the files of this step, and verify that the failing build or check passes.")
            .ToString();
        var step = new PlanStep(number, title, task, [.. files.Select(f => new DeclaredFile(f, FileAction.Modify))], [], Agent: Owner(failure.Id, files, agents))
        {
            AddedBy = PlanStep.AddedByTevrin,
            AddedReason = Reason(failure.Id),
        };
        return Append(step, failure, []);
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [GeneratedRegex(@"(?<![\w@/.-])(?<path>(?:\.{0,2}/)?[\w@.\[\]()-]+(?:/[\w@.\[\]()-]+)*\.[A-Za-z][A-Za-z0-9]{0,7})(?![\w/-])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathToken();
}
