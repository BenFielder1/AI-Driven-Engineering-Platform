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
    string? Summary)
{
    /// <summary>
    /// §4.13: requirements the agent reports as broken by this version (optional <c>"regressions"</c> array of
    /// <c>{requirementId, category, description, evidence?}</c>). <see cref="IterationReviewScope"/> keeps those tagged
    /// <c>[unchanged]</c> as regressions.
    /// </summary>
    public IReadOnlyList<RegressionEntry> Regressions { get; init; } = [];

    /// <summary>FR-239: failure ids whose evidence block couldn't be attached to the plan (logged as a warning).</summary>
    public IReadOnlyList<string> DroppedFailureEvidence { get; init; } = [];

    /// <summary>FR-248, FR-250, §4.43: how the plan handled each deterministic finding id (empty without a finding check).</summary>
    public IReadOnlyDictionary<string, FindingOutcome> FindingOutcomes { get; init; } = new Dictionary<string, FindingOutcome>();

    /// <summary>FR-249: the waivers this reply was granted (valid <c>waivedFindings</c> entries).</summary>
    public IReadOnlyList<FindingWaiver> NewWaivers { get; init; } = [];

    /// <summary>§4.43 step 5: the titles of the steps Tevrin couldn't add (they broke a plan limit; logged as a warning).</summary>
    public IReadOnlyList<string> DroppedFindingSteps { get; init; } = [];
}

/// <summary>
/// FR-38: reads the Code Review agent's JSON reply. Every one of the six categories needs a score from 0 to 1; violations
/// and drift areas need a known category/severity; the optimisation plan follows the implementation plan rules
/// (<see cref="PlanValidator"/> with the project's max files per step, no protected paths — an agent may not declare them,
/// §4.6; with a composition, the <c>agents</c> of <see cref="PlanAgents.ForStackOptimization"/>, FR-124) and must render to a
/// document that reads back to the same steps. An unreadable reply is <c>INVALID_OUTPUT</c>; a
/// readable reply with an invalid plan is <c>INVALID_PLAN</c>. Either way every reason is in <see cref="DomainError.Details"/>
/// so the one corrective re-run can fix them all. FR-243: with <c>deployFiles</c>, a step for each missing deployment file
/// is added only after the agent's own steps validate, then the plan is validated again (<see cref="DeployFileSteps.AddValidated"/>).
/// FR-239: with <c>failures</c>, the failure evidence blocks are attached before that (<see cref="FailureEvidenceSteps.Attach"/>).
/// FR-247, FR-248: with <c>findings</c>, the reply's optional <c>waivedFindings</c> (<c>[{"id", "reason"}]</c>; malformed entries
/// are ignored) are read and §4.43 runs after the failure evidence (<see cref="FindingSteps.Apply"/>), also on an empty plan.
/// </summary>
public static class CodeReviewOutput
{
    /// <summary>Caps on what is kept from one reply (the prompt asks for far fewer).</summary>
    public const int MaxItems = 200;

    public const int MaxTextLength = 2000;

    public static Result<CodeReviewResult> Parse(
        string text,
        int maxFilesPerStep,
        PlanAgents? agents = null,
        DeployFileCheck? deployFiles = null,
        FailureEvidenceCheck? failures = null,
        FindingStepCheck? findings = null)
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
            var regressions = ReadRegressions(root, reasons);
            var summary = StringOf(root, "summary") is { Length: > 0 } s ? Clip(s.Trim()) : null;
            if (reasons.Count > 0)
            {
                return Invalid(reasons);
            }

            var findingCheck = findings is null ? null : findings with { Requests = ReadWaivers(root) };
            var plan = ReadPlan(root, maxFilesPerStep, agents, deployFiles, failures, findingCheck);
            if (plan.IsFailure)
            {
                return Result.Failure<CodeReviewResult>(plan.Error!);
            }

            return Result.Success(new CodeReviewResult(categories, violations, missing, drift, plan.Value.Steps, plan.Value.Markdown, summary)
            {
                Regressions = regressions,
                DroppedFailureEvidence = plan.Value.Dropped,
                FindingOutcomes = plan.Value.Findings?.Outcomes ?? new Dictionary<string, FindingOutcome>(),
                NewWaivers = plan.Value.Findings?.NewWaivers ?? [],
                DroppedFindingSteps = plan.Value.Findings?.DroppedSteps ?? [],
            });
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

    /// <summary>§4.13: the optional regressions; the category defaults to <c>feature_coverage</c> when omitted.</summary>
    private static List<RegressionEntry> ReadRegressions(JsonElement root, List<string> reasons)
    {
        var result = new List<RegressionEntry>();
        if (!root.TryGetProperty("regressions", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("\"regressions\" must be an array.");
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            var id = item.ValueKind == JsonValueKind.Object ? Specs.RequirementParser.NormaliseId(StringOf(item, "requirementId")) : null;
            var description = item.ValueKind == JsonValueKind.Object ? StringOf(item, "description")?.Trim() : null;
            if (id is null || string.IsNullOrEmpty(description))
            {
                reasons.Add("Every regression needs a \"requirementId\" (FR-n) and a \"description\".");
                continue;
            }

            var categoryText = StringOf(item, "category")?.Trim();
            var category = ReviewCategory.FeatureCoverage;
            if (!string.IsNullOrEmpty(categoryText) && !EnumStrings.TryParse(categoryText, out category))
            {
                reasons.Add($"The regression of {id} has the unknown category '{categoryText}'; use {string.Join(", ", EnumStrings.Values<ReviewCategory>())}.");
                continue;
            }

            result.Add(new RegressionEntry(
                id,
                category,
                Clip(description),
                StringOf(item, "evidence") is { Length: > 0 } evidence ? Clip(evidence.Trim()) : null));
        }

        return result;
    }

    /// <summary>FR-247: the reply's <c>waivedFindings</c>; entries without a string <c>id</c> are ignored (the reason is checked by §4.43).</summary>
    private static List<WaiverRequest> ReadWaivers(JsonElement root)
    {
        var result = new List<WaiverRequest>();
        if (!root.TryGetProperty("waivedFindings", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in list.EnumerateArray().Take(MaxItems))
        {
            if (item.ValueKind == JsonValueKind.Object && StringOf(item, "id")?.Trim() is { Length: > 0 } id)
            {
                result.Add(new WaiverRequest(id, StringOf(item, "reason") ?? string.Empty));
            }
        }

        return result;
    }

    private sealed record PlanRead(IReadOnlyList<PlanStep> Steps, string? Markdown, IReadOnlyList<string> Dropped, FindingStepResult? Findings);

    private static Result<PlanRead> ReadPlan(
        JsonElement root, int maxFilesPerStep, PlanAgents? agents, DeployFileCheck? deployFiles, FailureEvidenceCheck? failures, FindingStepCheck? findings)
    {
        // §4.43 on an empty plan: waivers still apply and every other finding is "No step".
        PlanRead Empty() => new([], null, [], findings is null ? null : FindingSteps.Apply([], findings, agents, steps => Result.Success(steps)));
        if (!root.TryGetProperty("optimizationPlan", out var plan) || plan.ValueKind == JsonValueKind.Null)
        {
            return Result.Success(Empty());
        }

        var stepsElement = plan.ValueKind switch
        {
            JsonValueKind.Array => plan,
            JsonValueKind.Object when plan.TryGetProperty("steps", out var s) => s,
            _ => default,
        };
        if (stepsElement.ValueKind == JsonValueKind.Array && stepsElement.GetArrayLength() == 0)
        {
            return Result.Success(Empty());
        }

        var parsed = PlanDocument.ParseJson(plan.GetRawText());
        // FR-243: the agent's own steps are validated first (a retry names its step numbers); then an optimisation plan that
        // leaves out a required deployment file gets the step first and is validated again.
        var validated = parsed.IsSuccess ? PlanValidator.Validate(parsed.Value, maxFilesPerStep, allowProtectedPaths: false, agents) : parsed;

        // FR-239: then the failure evidence blocks (and a fix step for a failure no step names), keeping the plan valid.
        IReadOnlyList<string> dropped = [];
        if (validated.IsSuccess && failures is not null)
        {
            var attached = FailureEvidenceSteps.Attach(validated.Value, failures, agents, steps => PlanValidator.Validate(steps, maxFilesPerStep, allowProtectedPaths: false, agents));
            validated = Result.Success(attached.Steps);
            dropped = attached.Dropped;
        }

        // FR-248, §4.43: then every deterministic finding gets a step or a valid waiver, keeping the plan valid.
        FindingStepResult? handled = null;
        if (validated.IsSuccess && findings is not null)
        {
            handled = FindingSteps.Apply(validated.Value, findings, agents, steps => PlanValidator.Validate(steps, maxFilesPerStep, allowProtectedPaths: false, agents));
            validated = Result.Success(handled.Steps);
        }

        if (validated.IsSuccess && deployFiles is not null)
        {
            validated = DeployFileSteps.AddValidated(validated.Value, deployFiles, steps => PlanValidator.Validate(steps, maxFilesPerStep, allowProtectedPaths: false, agents));
        }

        if (validated.IsFailure)
        {
            return Result.Failure<PlanRead>(validated.Error!);
        }

        // Added deployment-file steps go first in an optimisation plan: the findings' step numbers are read again.
        if (handled is not null)
        {
            handled = handled with { Outcomes = FindingSteps.Renumber(handled.Outcomes, validated.Value) };
        }

        var markdown = PlanDocument.RenderChecked(ArtifactType.OptimizationPlan, validated.Value, maxFilesPerStep, agents);
        return markdown.IsSuccess
            ? Result.Success(new PlanRead(validated.Value, markdown.Value, dropped, handled))
            : Result.Failure<PlanRead>(markdown.Error!);
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
