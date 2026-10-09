using System.Globalization;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Execution;

/// <summary>The Code Generator's reply (FR-31): the step it implemented, the complete files and optional notes.</summary>
public sealed record CodeGeneratorReply(int StepNumber, IReadOnlyList<ProducedFile> Files, string? Notes);

/// <summary>How a declared file was found in the working copy when the step's inputs were gathered.</summary>
public enum WorkingFileState
{
    /// <summary>The file doesn't exist yet.</summary>
    Missing = 0,

    /// <summary>The file exists; its text is included.</summary>
    Present = 1,

    /// <summary>The file exists but is larger than <see cref="CodeGeneration.MaxExistingFileBytes"/>; not included.</summary>
    TooLarge = 2,

    /// <summary>The file exists but isn't text (it holds NUL bytes); not included.</summary>
    Binary = 3,
}

/// <summary>A declared file's current content in the working copy (FR-31). <see cref="Content"/> is set only when present.</summary>
public sealed record WorkingFileContent(string Path, WorkingFileState State, string? Content = null, long Length = 0);

/// <summary>A plan step with its status, as the Code Generator sees the plan (FR-31 "plan with step statuses").</summary>
public sealed record PlanStepView(int Number, string Title, string Task, StepStatus Status, IReadOnlyList<DeclaredFile> Files, IReadOnlyList<int> Dependencies);

/// <summary>A step commit's message (FR-34): the subject line and the body (files, then the step trailer).</summary>
public sealed record StepCommitMessage(string Subject, string Body);

/// <summary>
/// The Code Generator's side of execution (FR-31–FR-34): reading its reply, building its inputs (plan with step
/// statuses, project rules, the declared files' current content, the Guardian's feedback on a retry) and the commit of an
/// accepted step.
/// </summary>
public static class CodeGeneration
{
    /// <summary>
    /// The trailer that keys a commit to its step (D17): a redelivered job finds the step's commit by it and never commits
    /// twice.
    /// </summary>
    public const string StepTrailer = "AIDE-Step-Id";

    /// <summary>A declared file larger than this (UTF-8 bytes) isn't included in the prompt.</summary>
    public const int MaxExistingFileBytes = 100_000;

    /// <summary>The longest step title kept in a commit subject.</summary>
    public const int MaxSubjectTitleLength = 200;

    /// <summary>
    /// Reads the reply: one JSON object <c>{"stepNumber": n, "files": [{"path", "content"}], "notes"}</c> (a wrapping
    /// fence or surrounding prose is tolerated). Anything else is <c>INVALID_OUTPUT</c>. The paths are checked by the
    /// Guardian, not here.
    /// </summary>
    public static Result<CodeGeneratorReply> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = AgentOutput.ExtractJsonObject(text);
        if (json is null)
        {
            return Invalid("The reply is not a JSON object with \"stepNumber\" and \"files\".");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The reply is not a JSON object.");
            }

            if (!root.TryGetProperty("stepNumber", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var stepNumber))
            {
                return Invalid("\"stepNumber\" is missing or not a whole number.");
            }

            if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("\"files\" is missing or not an array.");
            }

            var files = new List<ProducedFile>();
            var index = 0;
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                {
                    return Invalid($"files[{index}] needs a string \"path\" and a string \"content\".");
                }

                files.Add(new ProducedFile(path.GetString()!, content.GetString()!));
                index++;
            }

            var notes = root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.String
                ? notesElement.GetString()
                : null;
            return Result.Success(new CodeGeneratorReply(stepNumber, files, string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()));
        }
        catch (JsonException ex)
        {
            return Invalid($"The reply is not valid JSON ({ex.Message}).");
        }
    }

    /// <summary>
    /// An unreadable reply is a rejected attempt like any Guardian rejection (FR-33): this violation (single-step check,
    /// code <c>INVALID_OUTPUT</c>) is stored on the step and fed back on the retry.
    /// </summary>
    public static GuardianViolation UnreadableViolation(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new GuardianViolation(GuardianCheck.SingleStep, ErrorCodes.InvalidOutput, ViolationSeverity.Error, error.Message);
    }

    /// <summary>
    /// FR-33: the feedback for a retry, from the violations of the previous rejected attempt; <c>null</c> when there were
    /// no errors (nothing to fix). Messages never contain secret values (the Guardian never echoes them).
    /// </summary>
    public static string? RetryFeedback(IReadOnlyList<GuardianViolation>? violations)
    {
        if (violations is null || !violations.Any(v => v.Severity == ViolationSeverity.Error))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("Your previous attempt at this step was rejected by the Execution Guardian. Fix every violation below and return the complete files again.\n");
        foreach (var v in violations.Where(v => v.Severity == ViolationSeverity.Error))
        {
            sb.Append("- [").Append(v.Code).Append("] ").Append(v.Message).Append('\n');
        }

        var warnings = violations.Where(v => v.Severity == ViolationSeverity.Warning).ToList();
        if (warnings.Count > 0)
        {
            sb.Append("Warnings (not blocking):\n");
            foreach (var v in warnings)
            {
                sb.Append("- [").Append(v.Code).Append("] ").Append(v.Message).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The step's declared files, one per line: <c>- path (create|modify)</c>.</summary>
    public static string DeclaredFiles(IEnumerable<DeclaredFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return string.Join('\n', files.Select(f => $"- {PathRules.Normalize(f.Path)} ({f.Action.ToWire()})"));
    }

    /// <summary>
    /// FR-31: the plan with each step's status, in step order: number, status, title, task, files and dependencies.
    /// </summary>
    public static string PlanWithStatuses(IEnumerable<PlanStepView> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var sb = new StringBuilder();
        foreach (var step in steps.OrderBy(s => s.Number))
        {
            sb.Append("## Step ").Append(step.Number.ToString(CultureInfo.InvariantCulture))
                .Append(" [").Append(step.Status.ToWire()).Append("]: ").Append(OneLine(step.Title)).Append('\n');
            sb.Append(step.Task.Trim()).Append('\n');
            sb.Append("Files: ").Append(step.Files.Count == 0
                ? "none"
                : string.Join(", ", step.Files.Select(f => $"{PathRules.Normalize(f.Path)} ({f.Action.ToWire()})"))).Append('\n');
            sb.Append("Depends on: ").Append(step.Dependencies.Count == 0
                ? "none"
                : string.Join(", ", step.Dependencies.Select(d => "step " + d.ToString(CultureInfo.InvariantCulture)))).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// FR-31: the current content of the step's declared files, each between <c>----- BEGIN path -----</c> and
    /// <c>----- END path -----</c>, or a note when it doesn't exist yet, is too large or isn't text. <c>null</c> for no files.
    /// </summary>
    public static string? ExistingCode(IEnumerable<WorkingFileContent> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var list = files.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var file in list)
        {
            var path = PathRules.Normalize(file.Path);
            switch (file.State)
            {
                case WorkingFileState.Present:
                    sb.Append("----- BEGIN ").Append(path).Append(" -----\n")
                        .Append(ContentHash.NormaliseLineEndings(file.Content ?? string.Empty).TrimEnd('\n')).Append('\n')
                        .Append("----- END ").Append(path).Append(" -----\n\n");
                    break;
                case WorkingFileState.TooLarge:
                    sb.Append("----- ").Append(path).Append(": exists, but is too large to include (")
                        .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes) -----\n\n");
                    break;
                case WorkingFileState.Binary:
                    sb.Append("----- ").Append(path).Append(": exists, but is not a text file -----\n\n");
                    break;
                default:
                    sb.Append("----- ").Append(path).Append(": does not exist yet -----\n\n");
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The project rules given to the Code Generator (FR-31): the project's limits and protected paths, followed by the
    /// repository's own rules file (<c>.aide/rules.md</c>, which agents can never write: the Guardian rejects any output under <c>.aide/</c>).
    /// </summary>
    public static string ProjectRules(int maxFilesPerStep, string? repositoryRules)
    {
        var sb = new StringBuilder();
        sb.Append("- Touch at most ").Append(maxFilesPerStep.ToString(CultureInfo.InvariantCulture)).Append(" files in this step.\n");
        sb.Append("- Write only the files declared for this step, at their exact paths.\n");
        sb.Append("- Never write anything under .git/ or .aide/.\n");
        sb.Append("- Never write other protected paths (CI workflows, lockfiles, .env files) unless this step declares them.\n");
        sb.Append("- Never write secrets (API keys, tokens, passwords, private keys).");
        if (!string.IsNullOrWhiteSpace(repositoryRules))
        {
            sb.Append("\n\nRepository rules (.aide/rules.md):\n").Append(ContentHash.NormaliseLineEndings(repositoryRules).Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// FR-34: subject <c>[AIDE][Phase:execution][Step:N] &lt;title&gt;</c> (title on one line, at most
    /// <see cref="MaxSubjectTitleLength"/> characters) and a body listing the files, ending with the
    /// <see cref="StepTrailer"/> trailer.
    /// </summary>
    public static StepCommitMessage CommitMessage(int stepNumber, string title, IEnumerable<string> paths, Guid stepId)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(paths);
        var cleanTitle = OneLine(title);
        if (cleanTitle.Length > MaxSubjectTitleLength)
        {
            cleanTitle = cleanTitle[..MaxSubjectTitleLength].TrimEnd();
        }

        var subject = $"[AIDE][Phase:execution][Step:{stepNumber.ToString(CultureInfo.InvariantCulture)}] {cleanTitle}".TrimEnd();
        var sorted = paths.Select(PathRules.Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var body = new StringBuilder("Files:\n");
        foreach (var path in sorted)
        {
            body.Append("- ").Append(path).Append('\n');
        }

        body.Append('\n').Append(StepTrailerLine(stepId));
        return new StepCommitMessage(subject, body.ToString());
    }

    /// <summary>The trailer line that keys a commit to <paramref name="stepId"/>.</summary>
    public static string StepTrailerLine(Guid stepId) => $"{StepTrailer}: {stepId:D}";

    /// <summary>
    /// FR-34: the file manifest of an accepted step — each file's normalised path, its declared action (a file the step
    /// doesn't declare counts as <c>modify</c>) and the SHA-256 of its content (§4.3 hashing), in path order.
    /// </summary>
    public static IReadOnlyList<FileManifestEntry> Manifest(IEnumerable<ProducedFile> files, IReadOnlyList<DeclaredFile> declared)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(declared);
        var actions = declared
            .GroupBy(d => PathRules.Normalize(d.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Action, StringComparer.Ordinal);
        return
        [
            .. files
                .Select(f => (Path: PathRules.Normalize(f.Path), f.Content))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => new FileManifestEntry(f.Path, actions.GetValueOrDefault(f.Path, FileAction.Modify), ContentHash.Compute(f.Content))),
        ];
    }

    /// <summary>Control characters (line breaks included) become spaces; runs of spaces collapse; trimmed.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static Result<CodeGeneratorReply> Invalid(string reason) =>
        Result.Failure<CodeGeneratorReply>(new DomainError(ErrorCodes.InvalidOutput, $"The code generator reply can't be read: {reason}") { Details = [reason] });
}
