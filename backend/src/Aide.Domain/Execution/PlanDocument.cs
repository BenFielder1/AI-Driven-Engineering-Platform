using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>
/// FR-29, D6: the stored form of a plan artifact (<c>implementation_plan</c>, <c>optimization_plan</c>). The Markdown
/// content is rendered from the structured plan and ends with one fenced <c>```aide-plan</c> block holding the plan as
/// JSON. That block is the source of truth: execution steps (FR-30) are created from it, a human-edited draft (FR-18)
/// is validated from it and re-rendered, and the readable part above it is never parsed.
/// <para>
/// The JSON is the agent's own schema (<c>{"steps": [{"number", "title", "task", "files": [{"path", "action"}],
/// "dependencies", "userInstructions"}]}</c>, <c>declaredFiles</c> accepted for <c>files</c>), written with backticks
/// escaped (<c>`</c>) so text inside it can never close the fence.
/// </para>
/// </summary>
public static partial class PlanDocument
{
    public const string BlockLanguage = "aide-plan";

    /// <summary>The <c>format</c> written into the block.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The plan generator's reply (FR-29): its JSON object, read and then checked with <see cref="PlanValidator"/>.
    /// Unreadable JSON and rule violations are both <c>INVALID_PLAN</c> with every reason. A generated plan may not
    /// declare protected paths (§4.6); only an Owner's edit (<see cref="ParseMarkdown"/>) may.
    /// </summary>
    public static Result<IReadOnlyList<PlanStep>> ParseAgentOutput(string text, int? maxFilesPerStep, PlanAgents? agents = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = AgentOutput.ExtractJsonObject(text);
        return json is null
            ? Invalid("The reply is not a JSON object with a \"steps\" array.")
            : ParseAndValidate(json, maxFilesPerStep, allowProtectedPaths: false, agents);
    }

    /// <summary>
    /// A stored or edited plan document (FR-18, FR-30): the JSON of its single <c>```aide-plan</c> block, validated.
    /// No block, more than one, or an invalid plan is <c>INVALID_PLAN</c>.
    /// </summary>
    public static Result<IReadOnlyList<PlanStep>> ParseMarkdown(string markdown, int? maxFilesPerStep, PlanAgents? agents = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var blocks = BlockPattern().Matches(markdown.ReplaceLineEndings("\n"));
        return blocks.Count switch
        {
            0 => Invalid($"The plan has no ```{BlockLanguage} block with the structured steps."),
            > 1 => Invalid($"The plan has {blocks.Count} ```{BlockLanguage} blocks; keep exactly one."),
            _ => ParseAndValidate(blocks[0].Groups["json"].Value, maxFilesPerStep, allowProtectedPaths: true, agents),
        };
    }

    /// <summary>
    /// <see cref="Render"/>, then proof that the stored document reads back (<see cref="ParseMarkdown"/>) to exactly these
    /// steps. A plan whose text could forge or break the <c>```aide-plan</c> block is refused here with
    /// <c>INVALID_PLAN</c> instead of becoming a draft that can never be approved.
    /// </summary>
    public static Result<string> RenderChecked(ArtifactType type, IReadOnlyList<PlanStep> steps, int? maxFilesPerStep, PlanAgents? agents = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var markdown = Render(type, steps);
        var reread = ParseMarkdown(markdown, maxFilesPerStep, agents);
        if (reread.IsFailure)
        {
            return Result.Failure<string>(reread.Error!);
        }

        // Compared in validated form (the default agent, normalised covers and paths), as the reread steps are.
        var expected = PlanValidator.Validate(steps, maxFilesPerStep: null, allowProtectedPaths: true, agents);
        if (!string.Equals(ToJson(reread.Value), ToJson(expected.IsSuccess ? expected.Value : steps), StringComparison.Ordinal))
        {
            const string reason = "The rendered plan does not read back to the same steps; remove code fences from the step text.";
            return Result.Failure<string>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reason}") { Details = [reason] });
        }

        return Result.Success(markdown);
    }

    /// <summary>
    /// Renders the plan as Markdown: a readable step list followed by the <c>```aide-plan</c> block. Step text is free
    /// text, so runs of three or more backticks or tildes in it are backslash-escaped (<see cref="Neutralise"/>): it can
    /// never open, close or forge a fenced block. Declared paths can't hold backticks or line breaks (<see cref="PathRules"/>).
    /// </summary>
    public static string Render(ArtifactType type, IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var title = type == ArtifactType.OptimizationPlan ? "Optimization plan" : "Implementation plan";
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");
        sb.Append(steps.Count.ToString(CultureInfo.InvariantCulture)).Append(steps.Count == 1 ? " step" : " steps")
            .Append(". The `").Append(BlockLanguage).Append("` block at the end is the source of truth: to change the plan, edit that block; ")
            .Append("the step list above it is rendered from it again when the draft is saved.\n");

        foreach (var step in steps)
        {
            sb.Append("\n## Step ").Append(step.Number.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(Neutralise(OneLine(step.Title))).Append("\n\n");
            sb.Append(Neutralise(step.Task.Trim())).Append("\n\n");
            sb.Append("**Files**\n\n");
            foreach (var file in step.Files)
            {
                sb.Append("- `").Append(file.Path).Append("` (").Append(file.Action.ToWire()).Append(")\n");
            }

            sb.Append("\n**Depends on:** ")
                .Append(step.Dependencies.Count == 0
                    ? "none"
                    : string.Join(", ", step.Dependencies.Select(d => "step " + d.ToString(CultureInfo.InvariantCulture))))
                .Append('\n');
            if (step.Covers.Count > 0)
            {
                sb.Append("\n**Covers:** ").Append(string.Join(", ", step.Covers.Select(c => Neutralise(OneLine(c))))).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(step.Agent) && step.Agent != PlanAgents.Generalist)
            {
                sb.Append("\n**Agent:** ").Append(Neutralise(OneLine(step.Agent))).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(step.UserInstructions))
            {
                sb.Append("\n**User instructions:** ").Append(Neutralise(step.UserInstructions.Trim())).Append('\n');
            }
        }

        sb.Append("\n## Plan data\n\n```").Append(BlockLanguage).Append('\n').Append(ToJson(steps)).Append("\n```\n");
        return sb.ToString();
    }

    /// <summary>The block JSON: <c>{"format": 1, "steps": [...]}</c>, indented, with backticks escaped.</summary>
    public static string ToJson(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var document = new Dictionary<string, object?>
        {
            ["format"] = FormatVersion,
            ["steps"] = steps.Select(s => new Dictionary<string, object?>
            {
                ["number"] = s.Number,
                ["title"] = s.Title,
                ["task"] = s.Task,
                ["files"] = s.Files.Select(f => new Dictionary<string, object?> { ["path"] = f.Path, ["action"] = f.Action.ToWire() }).ToList(),
                ["dependencies"] = s.Dependencies,
                ["userInstructions"] = string.IsNullOrWhiteSpace(s.UserInstructions) ? null : s.UserInstructions.Trim(),
            }.WithAgentAndCovers(s)).ToList(),
        };

        // Backticks only occur inside JSON strings, where ` means the same character.
        return JsonSerializer.Serialize(document, WriteOptions).ReplaceLineEndings("\n").Replace("`", "\\u0060", StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-76: <c>agent</c> is written only when it isn't the default <c>generalist</c> and <c>covers</c> only when not empty,
    /// so v1 plan documents render byte-identically (reading them back defaults the agent again).
    /// </summary>
    private static Dictionary<string, object?> WithAgentAndCovers(this Dictionary<string, object?> step, PlanStep source)
    {
        if (!string.IsNullOrWhiteSpace(source.Agent) && source.Agent != PlanAgents.Generalist)
        {
            step["agent"] = source.Agent;
        }

        if (source.Covers.Count > 0)
        {
            step["covers"] = source.Covers;
        }

        return step;
    }

    /// <summary>The feedback for the corrective re-run (FR-29): every reason the plan was rejected.</summary>
    public static string CorrectiveFeedback(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var reasons = error.Details.Count > 0 ? error.Details : [error.Message];
        return $"Your previous plan was rejected with {error.Code} for these reasons:\n"
            + string.Join("\n", reasons.Select(r => "- " + r))
            + "\nReturn the complete corrected plan as one JSON object.";
    }

    private static Result<IReadOnlyList<PlanStep>> ParseAndValidate(string json, int? maxFilesPerStep, bool allowProtectedPaths, PlanAgents? agents)
    {
        var parsed = ParseJson(json);
        return parsed.IsFailure ? parsed : PlanValidator.Validate(parsed.Value, maxFilesPerStep, allowProtectedPaths, agents);
    }

    /// <summary>Reads the JSON plan shape into steps (not yet validated). Malformed JSON is <c>INVALID_PLAN</c>.</summary>
    public static Result<IReadOnlyList<PlanStep>> ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var stepsElement = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array => s,
                _ => (JsonElement?)null,
            };
            if (stepsElement is not { } list)
            {
                return Invalid("The plan JSON needs a \"steps\" array.");
            }

            var reasons = new List<string>();
            var steps = new List<PlanStep>();
            var position = 0;
            foreach (var item in list.EnumerateArray())
            {
                position++;
                var step = ReadStep(item, position, reasons);
                if (step is not null)
                {
                    steps.Add(step);
                }
            }

            return reasons.Count == 0
                ? Result.Success<IReadOnlyList<PlanStep>>(steps)
                : Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reasons[0]}") { Details = reasons });
        }
        catch (JsonException ex)
        {
            return Invalid($"The plan is not valid JSON ({ex.Message}).");
        }
    }

    private static PlanStep? ReadStep(JsonElement item, int position, List<string> reasons)
    {
        var at = $"Step at position {position.ToString(CultureInfo.InvariantCulture)}";
        if (item.ValueKind != JsonValueKind.Object)
        {
            reasons.Add($"{at} is not an object.");
            return null;
        }

        if (!item.TryGetProperty("number", out var numberElement) || numberElement.ValueKind != JsonValueKind.Number || !numberElement.TryGetInt32(out var number))
        {
            reasons.Add($"{at} has no integer \"number\".");
            return null;
        }

        var name = $"Step {number.ToString(CultureInfo.InvariantCulture)}";
        var title = StringOf(item, "title") ?? string.Empty;
        var task = StringOf(item, "task") ?? string.Empty;
        var userInstructions = StringOf(item, "userInstructions");

        var files = new List<DeclaredFile>();
        var filesElement = item.TryGetProperty("files", out var f) ? f : item.TryGetProperty("declaredFiles", out var d) ? d : default;
        if (filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                var path = file.ValueKind == JsonValueKind.Object ? StringOf(file, "path") : null;
                var action = file.ValueKind == JsonValueKind.Object ? StringOf(file, "action")?.Trim().ToLowerInvariant() : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    reasons.Add($"{name} declares a file without a \"path\".");
                    continue;
                }

                if (!EnumStrings.TryParse<FileAction>(action, out var fileAction))
                {
                    reasons.Add($"{name} declares '{path}' with action '{action}'; use \"create\" or \"modify\".");
                    continue;
                }

                files.Add(new DeclaredFile(path, fileAction));
            }
        }
        else if (filesElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            reasons.Add($"{name}: \"files\" must be an array.");
        }

        var dependencies = new List<int>();
        if (item.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in deps.EnumerateArray())
            {
                if (dep.ValueKind == JsonValueKind.Number && dep.TryGetInt32(out var value))
                {
                    dependencies.Add(value);
                }
                else
                {
                    reasons.Add($"{name} has a dependency that is not a step number.");
                }
            }
        }
        else if (item.TryGetProperty("dependencies", out var other) && other.ValueKind != JsonValueKind.Null)
        {
            reasons.Add($"{name}: \"dependencies\" must be an array of step numbers.");
        }

        var agent = StringOf(item, "agent");
        if (item.TryGetProperty("agent", out var agentElement) && agentElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            reasons.Add($"{name}: \"agent\" must be a string.");
        }

        var covers = new List<string>();
        if (item.TryGetProperty("covers", out var coversElement) && coversElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in coversElement.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text)
                {
                    covers.Add(text.Trim());
                }
                else
                {
                    reasons.Add($"{name} has a \"covers\" entry that is not a requirement ID string.");
                }
            }
        }
        else if (item.TryGetProperty("covers", out var otherCovers) && otherCovers.ValueKind != JsonValueKind.Null)
        {
            reasons.Add($"{name}: \"covers\" must be an array of requirement IDs (FR-n).");
        }

        return new PlanStep(
            number,
            title,
            task,
            files,
            dependencies,
            string.IsNullOrWhiteSpace(userInstructions) ? null : userInstructions.Trim(),
            string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
            covers);
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Backslash-escapes every character of a run of three or more backticks or tildes (a fence).</summary>
    public static string Neutralise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FencePattern().Replace(text, m => string.Concat(m.Value.Select(c => "\\" + c)));
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static Result<IReadOnlyList<PlanStep>> Invalid(string reason) =>
        Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reason}") { Details = [reason] });

    [GeneratedRegex(@"^```aide-plan[ \t]*\n(?<json>.*?)\n```[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockPattern();

    [GeneratedRegex("[`~]{3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FencePattern();
}
