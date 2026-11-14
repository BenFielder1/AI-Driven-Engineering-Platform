using System.Globalization;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Review;
using Aide.Domain.Stack;

namespace Aide.Domain.Execution;

/// <summary>A build-fix step as stored (an execution step with origin <c>build_fix</c>): number, title and text.</summary>
public sealed record BuildFixStepText(int Number, string Title, string Task);

/// <summary>
/// An earlier build check of the version (any review cycle), for repeat marks (§4.48): its cycle, attempt, status, the
/// failures it found and the fix steps planned from it (the execution steps whose <c>build_check_id</c> is its id).
/// </summary>
public sealed record BuildCheckHistory(int ReviewCycle, int Attempt, BuildCheckStatus Status, IReadOnlyList<BuildCheckFailure> Failures, IReadOnlyList<BuildFixStepText> FixSteps);

/// <summary>FR-256: a failure seen in consecutive build checks: how many (this one included) and the previous fix's step text.</summary>
public sealed record BuildFixRepeat(int Attempts, string? PreviousStepText);

/// <summary>A pack's build command for <c>STACK_GUIDANCE</c> (for example <c>frontend (Vercel)</c>, <c>npm run build</c>).</summary>
public sealed record BuildCommandHint(string Target, string Command);

/// <summary>
/// What processing a build-fix reply needs (§4.48 Processing): the version's highest step number (the fix steps are
/// numbered after it), the project's max files per step, the agents the steps may name (as for an optimisation plan),
/// the failures (for the evidence blocks, FR-239), the files on the branch (<c>null</c>: unknown) and the required
/// deployment files check (FR-243; <c>null</c>: none).
/// </summary>
public sealed record BuildFixPlanCheck(
    int HighestStepNumber,
    int? MaxFilesPerStep,
    PlanAgents? Agents,
    IReadOnlyList<BuildCheckFailure> Failures,
    IReadOnlySet<string>? RepoFiles,
    DeployFileCheck? DeployFiles = null);

/// <summary>
/// A processed fix plan: <see cref="Steps"/> numbered after the version's highest step (store these, origin
/// <c>build_fix</c>, with the check's id), <see cref="LocalSteps"/> the same plan numbered 1..n, the agent's summary, and
/// the failure ids whose evidence couldn't be attached (to log).
/// </summary>
public sealed record BuildFixPlan(IReadOnlyList<PlanStep> Steps, IReadOnlyList<PlanStep> LocalSteps, string? Summary, IReadOnlyList<string> DroppedEvidence);

/// <summary>
/// FR-256, §4.48: the build-fix agent (<c>build_fix.md</c> v1, agent key <c>build_fix</c>, the <c>code_review</c> model
/// settings, D-0.6.1-17): its prompt inputs and the processing of its reply. Pure.
/// </summary>
public static class BuildFixPlanning
{
    /// <summary>The agent key and prompt name (§4.48).</summary>
    public static string AgentKey => AgentKind.BuildFix.ToWire();

    /// <summary>The fewest steps a fix plan has.</summary>
    public const int MinSteps = 1;

    /// <summary>The most steps the agent may plan (Tevrin's added steps come on top, FR-239, FR-243).</summary>
    public const int MaxSteps = 5;

    /// <summary>The read-only repository tool calls the agent may make.</summary>
    public const int MaxToolCalls = 25;

    /// <summary>The longest previous fix step text in the prompt (as for review repeats).</summary>
    public const int MaxPreviousStepLength = FailureHistory.MaxPreviousStepLength;

    /// <summary>The most repository paths listed in <c>REPO_FILES</c>.</summary>
    public const int MaxRepoFiles = 2000;

    // ---------- Inputs ----------

    /// <summary>
    /// §4.48 <c>FAILURES</c> repeat marks: a current failure whose fingerprint (§4.37) was also found by the version's
    /// previous build check(s) (any review cycle, in (cycle, attempt) order, cancelled checks skipped) is repeated: its count
    /// is the number of consecutive checks with the fingerprint, this one included. The previous fix step text is the text
    /// of the fix steps planned from the most recent earlier check with the fingerprint that name the failure id (evidence
    /// blocks removed), or <c>null</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, BuildFixRepeat> Repeats(IReadOnlyList<BuildCheckFailure> current, IReadOnlyList<BuildCheckHistory> earlier)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(earlier);
        var history = earlier
            .Where(h => h.Status is not (BuildCheckStatus.Cancelled or BuildCheckStatus.Waiting))
            .OrderByDescending(h => h.ReviewCycle)
            .ThenByDescending(h => h.Attempt)
            .ToList();
        var result = new Dictionary<string, BuildFixRepeat>(StringComparer.Ordinal);
        foreach (var failure in current)
        {
            var count = 1;
            BuildCheckHistory? latest = null;
            foreach (var check in history)
            {
                if (!check.Failures.Any(f => string.Equals(f.Fingerprint, failure.Fingerprint, StringComparison.Ordinal)))
                {
                    break;
                }

                latest ??= check;
                count++;
            }

            if (count > 1 && !result.ContainsKey(failure.FailureId))
            {
                result[failure.FailureId] = new BuildFixRepeat(count, PreviousText(latest!.FixSteps, failure.FailureId));
            }
        }

        return result;
    }

    /// <summary>
    /// §4.48 <c>{{FAILURES}}</c>: per failure its id, source and message, a <c>repeated (n attempts)</c> mark with the
    /// previous fix's step text, and its excerpt (§4.38) in a fenced block. Excerpts are capped at 18,000 characters in
    /// total (preview failures first, then checks; the rest cut to their first error line).
    /// </summary>
    public static string RenderFailures(IReadOnlyList<BuildCheckFailure> failures, IReadOnlyDictionary<string, BuildFixRepeat>? repeats = null)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            return "No failures.";
        }

        // §4.38 step 5 via the review's cap: preview failures count as critical, checks as major.
        var asFindings = failures.Select(f => new ReviewFinding(
            ReviewCategory.Architecture,
            f.Source == FailureIds.Preview ? Severity.Critical : Severity.Major,
            f.Message,
            Source: FindingSource.Deterministic,
            Evidence: f.Excerpt)
        { FailureId = f.FailureId }).ToList();
        var capped = FailureExcerpt.CapAll(asFindings);
        var sb = new StringBuilder();
        for (var i = 0; i < failures.Count; i++)
        {
            var f = failures[i];
            sb.Append(CultureInfo.InvariantCulture, $"- failure id {f.FailureId} ({FailureIds.SourceLabel(f.FailureId)}): {OneLine(f.Message)}");
            if (repeats is not null && repeats.TryGetValue(f.FailureId, out var repeat))
            {
                sb.Append(CultureInfo.InvariantCulture, $" — repeated ({repeat.Attempts} attempts)\n");
                sb.Append("  The same error failed the previous build check too, so the previous fix did not work: try a different approach and say in the step what was tried before.\n");
                if (repeat.PreviousStepText is { } previous)
                {
                    var previousFence = Fence(previous);
                    sb.Append("  Previous fix step:\n").Append(previousFence).Append('\n').Append(previous.TrimEnd('\n')).Append('\n').Append(previousFence).Append('\n');
                }
            }
            else
            {
                sb.Append('\n');
            }

            if (capped[i].Evidence is { Length: > 0 } excerpt && !string.IsNullOrWhiteSpace(excerpt))
            {
                var fence = Fence(excerpt);
                sb.Append("  Evidence excerpt:\n").Append(fence).Append('\n').Append(excerpt.TrimEnd('\n')).Append('\n').Append(fence).Append('\n');
            }
            else
            {
                sb.Append("  (no log excerpt)\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>§4.48 <c>{{ERROR_SUMMARY}}</c> (<see cref="BuildErrorSummary.Render"/>).</summary>
    public static string RenderErrorSummary(IEnumerable<BuildCheckFailure> failures) => BuildErrorSummary.Render(failures);

    /// <summary>
    /// §4.48 <c>{{STACK_GUIDANCE?}}</c>: the required deployment files (§4.40) and the packs' build commands; <c>null</c>
    /// when there are neither.
    /// </summary>
    public static string? RenderStackGuidance(IReadOnlyList<StackRequiredFile> requiredFiles, IReadOnlyList<BuildCommandHint> buildCommands)
    {
        ArgumentNullException.ThrowIfNull(requiredFiles);
        ArgumentNullException.ThrowIfNull(buildCommands);
        if (requiredFiles.Count == 0 && buildCommands.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        if (buildCommands.Count > 0)
        {
            sb.Append("Build commands:\n");
            foreach (var c in buildCommands)
            {
                sb.Append("- ").Append(OneLine(c.Target)).Append(": `").Append(OneLine(c.Command)).Append("`\n");
            }

            sb.Append('\n');
        }

        if (requiredFiles.Count > 0)
        {
            sb.Append("Required deployment files:\n");
            foreach (var file in requiredFiles)
            {
                sb.Append("- ").Append(file.Path);
                if (file.Alternatives.Count > 0)
                {
                    sb.Append(" (or ").Append(string.Join(", ", file.Alternatives)).Append(')');
                }

                sb.Append(" for ").Append(file.Platform).Append(", owner ").Append(file.Owner).Append(":\n");
                foreach (var line in file.Content)
                {
                    sb.Append("  - ").Append(line).Append('\n');
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>§4.48: the version's step titles, one per line (<c>n. title</c>), in step order.</summary>
    public static string RenderPlanSteps(IEnumerable<BuildFixStepText> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var lines = steps.OrderBy(s => s.Number).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Number}. {OneLine(s.Title)}")).ToList();
        return lines.Count == 0 ? "No steps." : string.Join('\n', lines);
    }

    /// <summary>§4.48: the repository file list (sorted, at most <see cref="MaxRepoFiles"/>, with a note when cut); <c>null</c> when unknown.</summary>
    public static string? RenderRepoFiles(IEnumerable<string>? files)
    {
        if (files is null)
        {
            return null;
        }

        var all = files.Select(PathRules.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var shown = all.Take(MaxRepoFiles).ToList();
        var text = string.Join('\n', shown);
        return all.Count > shown.Count
            ? text + string.Create(CultureInfo.InvariantCulture, $"\n({all.Count - shown.Count} more files not listed; use list_files)")
            : text;
    }

    // ---------- Processing ----------

    /// <summary>
    /// §4.48 Processing 1–3 and 5: reads the reply <c>{"steps": [...], "summary": "..."}</c> (plan step schema), checks it
    /// has 1–5 steps, validates them with <see cref="PlanValidator"/> (generated plan: no protected paths), attaches the
    /// failure evidence blocks (<see cref="FailureEvidenceSteps.Attach"/>; a reply that names no failure id gets the
    /// "Fix:" step, FR-239), adds steps for missing deployment files first (<see cref="DeployFileSteps.AddValidated"/>, as
    /// in an optimisation plan) and numbers the steps after <see cref="BuildFixPlanCheck.HighestStepNumber"/> (dependencies
    /// renumbered; they only point within the fix plan). Any failure is <c>INVALID_PLAN</c> with every reason (step 4: retry
    /// once with <see cref="CorrectiveFeedback"/>, then the check is exhausted).
    /// </summary>
    public static Result<BuildFixPlan> Process(string reply, BuildFixPlanCheck check)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentOutOfRangeException.ThrowIfNegative(check.HighestStepNumber);
        var json = AgentOutput.ExtractJsonObject(reply);
        if (json is null)
        {
            return Invalid("The reply is not a JSON object with a \"steps\" array.");
        }

        var parsed = PlanDocument.ParseJson(json);
        if (parsed.IsFailure)
        {
            return Result.Failure<BuildFixPlan>(parsed.Error!);
        }

        if (parsed.Value.Count < MinSteps || parsed.Value.Count > MaxSteps)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"The fix plan has {parsed.Value.Count} steps; plan {MinSteps} to {MaxSteps} steps."));
        }

        Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps) =>
            PlanValidator.Validate(steps, check.MaxFilesPerStep, allowProtectedPaths: false, check.Agents);

        var validated = Validate(parsed.Value);
        if (validated.IsFailure)
        {
            return Result.Failure<BuildFixPlan>(validated.Error!);
        }

        var attached = FailureEvidenceSteps.Attach(
            validated.Value,
            new FailureEvidenceCheck(BuildCheckFailures.Evidence(check.Failures), check.RepoFiles, check.MaxFilesPerStep),
            check.Agents,
            Validate);
        IReadOnlyList<PlanStep> steps = attached.Steps;
        if (check.DeployFiles is { } deployFiles)
        {
            var added = DeployFileSteps.AddValidated(steps, deployFiles with { Optimisation = true }, Validate);
            if (added.IsFailure)
            {
                return Result.Failure<BuildFixPlan>(added.Error!);
            }

            steps = added.Value;
        }

        return Result.Success(new BuildFixPlan(Renumber(steps, check.HighestStepNumber), steps, Summary(json), attached.Dropped));
    }

    /// <summary>§4.48 step 5: the steps numbered after <paramref name="highestStepNumber"/>, with their dependencies moved by the same offset.</summary>
    public static IReadOnlyList<PlanStep> Renumber(IReadOnlyList<PlanStep> steps, int highestStepNumber)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegative(highestStepNumber);
        return [.. steps.Select(s => s with { Number = s.Number + highestStepNumber, Dependencies = [.. s.Dependencies.Select(d => d + highestStepNumber)] })];
    }

    /// <summary>§4.48 step 4: the feedback for the one retry: every reason the fix plan was rejected.</summary>
    public static string CorrectiveFeedback(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var reasons = error.Details.Count > 0 ? error.Details : [error.Message];
        return $"Your previous fix plan was rejected with {error.Code} for these reasons:\n"
            + string.Join("\n", reasons.Select(r => "- " + r))
            + string.Create(CultureInfo.InvariantCulture, $"\nReturn the complete corrected fix plan ({MinSteps} to {MaxSteps} steps) as one JSON object.");
    }

    private static string? Summary(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                && s.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? Clip(text.Trim(), 2000)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PreviousText(IReadOnlyList<BuildFixStepText> steps, string failureId)
    {
        var named = steps
            .Where(s => FailureIds.IsNamedIn(s.Title, failureId) || FailureIds.IsNamedIn(s.Task, failureId))
            .OrderBy(s => s.Number)
            .Select(s => $"Step {s.Number.ToString(CultureInfo.InvariantCulture)}: {s.Title}\n{FailureEvidenceSteps.StripBlocks(s.Task)}".Trim())
            .ToList();
        if (named.Count == 0)
        {
            return null;
        }

        var text = string.Join("\n\n", named);
        return text.Length > MaxPreviousStepLength ? text[..MaxPreviousStepLength] + "…" : text;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Fence(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    private static Result<BuildFixPlan> Invalid(string reason) =>
        Result.Failure<BuildFixPlan>(new DomainError(ErrorCodes.InvalidPlan, $"The fix plan is invalid: {reason}") { Details = [reason] });
}
