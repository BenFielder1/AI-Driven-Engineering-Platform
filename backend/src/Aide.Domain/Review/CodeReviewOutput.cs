using System.Globalization;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>The Code Review agent's score for one category (§4.7): 0–1 with its evidence.</summary>
public sealed record CategoryAssessment(ReviewCategory Category, decimal Score, IReadOnlyList<ScoreEvidence> Evidence);

/// <summary>
/// The Code Review agent's structured reply (FR-38, D6): a score with evidence for every category, its violations, missing
/// features, drift areas and the optimisation plan (empty when nothing needs fixing) with the plan's rendered Markdown.
/// </summary>
public sealed record CodeReviewResult(
    IReadOnlyList<CategoryAssessment> Categories,
    IReadOnlyList<ReviewFinding> Violations,
    IReadOnlyList<string> MissingFeatures,
    IReadOnlyList<DriftAreaEntry> DriftAreas,
    IReadOnlyList<PlanStep> OptimizationPlan,
    string? OptimizationPlanMarkdown,
    string? Summary);

/// <summary>
/// FR-38: reads the Code Review agent's JSON reply. Every one of the six categories needs a score from 0 to 1; violations
/// and drift areas need a known category/severity; the optimisation plan follows the implementation plan rules
/// (<see cref="PlanValidator"/> with the project's max files per step, no protected paths — an agent may not declare them,
/// §4.6) and must render to a document that reads back to the same steps. An unreadable reply is <c>INVALID_OUTPUT</c>; a
/// readable reply with an invalid plan is <c>INVALID_PLAN</c>. Either way every reason is in <see cref="DomainError.Details"/>
/// so the one corrective re-run can fix them all.
/// </summary>
public static class CodeReviewOutput
{
    /// <summary>Caps on what is kept from one reply (the prompt asks for far fewer).</summary>
    public const int MaxItems = 200;

    public const int MaxTextLength = 2000;

    public static Result<CodeReviewResult> Parse(string text, int maxFilesPerStep)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = AgentOutput.ExtractJsonObject(text);
        if (json is null)
        {
            return Invalid(["The reply is not a JSON object."]);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return Invalid([$"The reply is not valid JSON ({ex.Message})."]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(["The reply is not a JSON object."]);
            }

            var reasons = new List<string>();
            var categories = ReadCategories(root, reasons);
            var violations = ReadViolations(root, reasons);
            var missing = ReadMissingFeatures(root, reasons);
            var drift = ReadDriftAreas(root, reasons);
            var summary = StringOf(root, "summary") is { Length: > 0 } s ? Clip(s.Trim()) : null;
            if (reasons.Count > 0)
            {
                return Invalid(reasons);
            }

            var plan = ReadPlan(root, maxFilesPerStep);
            if (plan.IsFailure)
            {
                return Result.Failure<CodeReviewResult>(plan.Error!);
            }

            return Result.Success(new CodeReviewResult(categories, violations, missing, drift, plan.Value.Steps, plan.Value.Markdown, summary));
        }
    }

    /// <summary>The feedback for the corrective re-run: every reason the reply was rejected.</summary>
    public static string CorrectiveFeedback(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var reasons = error.Details.Count > 0 ? error.Details : [error.Message];
        return $"Your previous review was rejected with {error.Code} for these reasons:\n"
            + string.Join("\n", reasons.Select(r => "- " + r))
            + "\nReturn the complete review again as one JSON object in the format described above, with all six categories.";
    }

    private static List<CategoryAssessment> ReadCategories(JsonElement root, List<string> reasons)
    {
        var result = new List<CategoryAssessment>();
        if (!root.TryGetProperty("categoryScores", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("\"categoryScores\" must be an array with a score for each of the six categories.");
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            var name = item.ValueKind == JsonValueKind.Object ? StringOf(item, "category") : null;
            if (!EnumStrings.TryParse<ReviewCategory>(name?.Trim(), out var category))
            {
                reasons.Add($"Unknown category '{name}'; use {string.Join(", ", EnumStrings.Values<ReviewCategory>())}.");
                continue;
            }

            if (!item.TryGetProperty("score", out var scoreElement) || scoreElement.ValueKind != JsonValueKind.Number
                || !scoreElement.TryGetDecimal(out var score) || score < 0m || score > 1m)
            {
                reasons.Add($"The {category.ToWire()} score must be a number from 0 to 1.");
                continue;
            }

            if (result.Any(c => c.Category == category))
            {
                reasons.Add($"The {category.ToWire()} category is scored more than once.");
                continue;
            }

            result.Add(new CategoryAssessment(category, Math.Round(score, 3, MidpointRounding.AwayFromZero), ReadEvidence(item)));
        }

        var missing = Enum.GetValues<ReviewCategory>().Where(c => result.All(r => r.Category != c)).ToList();
        if (missing.Count > 0)
        {
            reasons.Add($"Scores are missing for: {string.Join(", ", missing.Select(c => c.ToWire()))}.");
        }

        return [.. result.OrderBy(c => c.Category)];
    }

    private static List<ScoreEvidence> ReadEvidence(JsonElement item)
    {
        var evidence = new List<ScoreEvidence>();
        if (!item.TryGetProperty("evidence", out var list))
        {
            return evidence;
        }

        if (list.ValueKind == JsonValueKind.String && list.GetString() is { Length: > 0 } single)
        {
            evidence.Add(new ScoreEvidence(null, Clip(single.Trim())));
            return evidence;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            return evidence;
        }

        foreach (var e in list.EnumerateArray().Take(MaxItems))
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String when e.GetString() is { Length: > 0 } reason:
                    evidence.Add(new ScoreEvidence(null, Clip(reason.Trim())));
                    break;
                case JsonValueKind.Object when StringOf(e, "reason") is { Length: > 0 } reason:
                    evidence.Add(new ScoreEvidence(CleanPath(StringOf(e, "file") ?? StringOf(e, "path")), Clip(reason.Trim())));
                    break;
                default:
                    break;
            }
        }

        return evidence;
    }

    private static List<ReviewFinding> ReadViolations(JsonElement root, List<string> reasons)
    {
        var result = new List<ReviewFinding>();
        if (!root.TryGetProperty("violations", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("\"violations\" must be an array.");
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            if (item.ValueKind != JsonValueKind.Object || StringOf(item, "message") is not { Length: > 0 } message)
            {
                reasons.Add("Every violation needs a \"message\".");
                continue;
            }

            if (!EnumStrings.TryParse<ReviewCategory>(StringOf(item, "category")?.Trim(), out var category)
                || !EnumStrings.TryParse<Severity>(StringOf(item, "severity")?.Trim().ToLowerInvariant(), out var severity))
            {
                reasons.Add($"The violation '{Clip(message, 80)}' needs a known category and a severity (critical, major or minor).");
                continue;
            }

            result.Add(new ReviewFinding(
                category,
                severity,
                Clip(message.Trim()),
                CleanPath(StringOf(item, "path") ?? StringOf(item, "file")),
                FindingSource.Agent,
                StringOf(item, "evidence") is { Length: > 0 } evidence ? Clip(evidence.Trim()) : null));
        }

        return result;
    }

    private static List<string> ReadMissingFeatures(JsonElement root, List<string> reasons)
    {
        var result = new List<string>();
        if (!root.TryGetProperty("missingFeatures", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("\"missingFeatures\" must be an array.");
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } plain)
            {
                result.Add(Clip(plain.Trim()));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = StringOf(item, "id")?.Trim();
            var title = StringOf(item, "title")?.Trim();
            var reason = StringOf(item, "reason")?.Trim();
            var head = string.Join(" ", new[] { id, title }.Where(p => !string.IsNullOrEmpty(p)));
            var line = string.IsNullOrEmpty(reason) ? head : string.IsNullOrEmpty(head) ? reason : $"{head}: {reason}";
            if (!string.IsNullOrEmpty(line))
            {
                result.Add(Clip(line));
            }
        }

        return result;
    }

    private static List<DriftAreaEntry> ReadDriftAreas(JsonElement root, List<string> reasons)
    {
        var result = new List<DriftAreaEntry>();
        if (!root.TryGetProperty("driftAreas", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("\"driftAreas\" must be an array.");
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            var area = item.ValueKind == JsonValueKind.Object ? StringOf(item, "area")?.Trim() : null;
            if (string.IsNullOrEmpty(area) || !EnumStrings.TryParse<Severity>(StringOf(item, "severity")?.Trim().ToLowerInvariant(), out var severity))
            {
                reasons.Add("Every drift area needs an \"area\" and a severity (critical, major or minor).");
                continue;
            }

            result.Add(new DriftAreaEntry(Clip(area, 200), severity, Clip(StringOf(item, "description")?.Trim() ?? string.Empty)));
        }

        return result;
    }

    private static Result<(IReadOnlyList<PlanStep> Steps, string? Markdown)> ReadPlan(JsonElement root, int maxFilesPerStep)
    {
        if (!root.TryGetProperty("optimizationPlan", out var plan) || plan.ValueKind == JsonValueKind.Null)
        {
            return Result.Success<(IReadOnlyList<PlanStep>, string?)>(([], null));
        }

        var stepsElement = plan.ValueKind switch
        {
            JsonValueKind.Array => plan,
            JsonValueKind.Object when plan.TryGetProperty("steps", out var s) => s,
            _ => default,
        };
        if (stepsElement.ValueKind == JsonValueKind.Array && stepsElement.GetArrayLength() == 0)
        {
            return Result.Success<(IReadOnlyList<PlanStep>, string?)>(([], null));
        }

        var parsed = PlanDocument.ParseJson(plan.GetRawText());
        var validated = parsed.IsSuccess ? PlanValidator.Validate(parsed.Value, maxFilesPerStep, allowProtectedPaths: false) : parsed;
        if (validated.IsFailure)
        {
            return Result.Failure<(IReadOnlyList<PlanStep>, string?)>(validated.Error!);
        }

        var markdown = PlanDocument.RenderChecked(ArtifactType.OptimizationPlan, validated.Value, maxFilesPerStep);
        return markdown.IsSuccess
            ? Result.Success<(IReadOnlyList<PlanStep>, string?)>((validated.Value, markdown.Value))
            : Result.Failure<(IReadOnlyList<PlanStep>, string?)>(markdown.Error!);
    }

    private static string? CleanPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Clip(PathRules.Normalize(path.Trim()), 500);

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clip(string text, int max = MaxTextLength) => text.Length > max ? text[..max] + "…" : text;

    private static Result<CodeReviewResult> Invalid(List<string> reasons) =>
        Result.Failure<CodeReviewResult>(new DomainError(
            ErrorCodes.InvalidOutput,
            $"The review is invalid: {reasons[0]}" + (reasons.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (+{reasons.Count - 1} more)") : string.Empty))
        {
            Details = reasons,
        });

    /// <summary>A short Markdown summary of a report for the next cycle's <c>{{PREVIOUS_REVIEW?}}</c> input.</summary>
    public static string PreviousReviewSummary(
        int cycle, decimal score, ReviewDecision decision, IEnumerable<ReviewFinding> violations, IEnumerable<string> missingFeatures, IEnumerable<DriftAreaEntry> driftAreas)
    {
        ArgumentNullException.ThrowIfNull(violations);
        ArgumentNullException.ThrowIfNull(missingFeatures);
        ArgumentNullException.ThrowIfNull(driftAreas);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Cycle {cycle}: overall score {score:0.000}, decision {decision.ToWire()}.\n");
        foreach (var v in violations.Take(30))
        {
            sb.Append(CultureInfo.InvariantCulture, $"- violation ({v.Category.ToWire()}, {v.Severity.ToWire()}): {v.Message}{(v.Path is null ? string.Empty : $" [{v.Path}]")}\n");
        }

        foreach (var m in missingFeatures.Take(30))
        {
            sb.Append("- missing feature: ").Append(m).Append('\n');
        }

        foreach (var d in driftAreas.Take(30))
        {
            sb.Append(CultureInfo.InvariantCulture, $"- drift ({d.Severity.ToWire()}): {d.Area} — {d.Description}\n");
        }

        return sb.ToString().TrimEnd();
    }
}
