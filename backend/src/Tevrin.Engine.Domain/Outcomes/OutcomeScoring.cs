using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Domain.Outcomes;

/// <summary>FR-353: what an outcome score is about: one agent run, or one artifact version (and variant) of the run.</summary>
public sealed record OutcomeTarget(Guid? AgentRunId, string? ArtifactKey, int? Version, string? Variant);

/// <summary>FR-353: an outcome score request, before validation.</summary>
public sealed record OutcomeScoreInput(
    OutcomeTarget? Target, string? Metric, OutcomeScoreKind? Kind, double? Value, double? Prediction, DateTimeOffset? ObservedAt, string? Source);

/// <summary>FR-353: a valid outcome score request with its computed score.</summary>
public sealed record ValidOutcomeScore(
    OutcomeTarget Target, string Metric, OutcomeScoreKind Kind, double Value, double? Prediction, double Score, DateTimeOffset ObservedAt, string Source);

/// <summary>FR-353: one (agent key, prompt version, metric) group of scores: the plain mean over the scored targets.</summary>
public sealed record PromptOutcomeMean(string Agent, string PromptVersion, string Metric, double MeanScore, int Count);

/// <summary>FR-353: one stored score mapped to the agent run it rates (directly, or through the artifact's producer).</summary>
public sealed record AttributedOutcomeScore(string Agent, string PromptVersion, string Metric, string TargetKey, double Score);

/// <summary>
/// FR-353, §4.74: the pure rules of external outcome scores. A request targets exactly one of an agent run or an artifact
/// version; <c>metric</c> and <c>source</c> are required; a <c>probability_brier</c> score needs <c>prediction</c> in
/// [0, 1] and <c>value</c> in {0, 1} and scores (prediction − value)²; a <c>number</c> score is stored as given.
/// </summary>
public static class OutcomeScoring
{
    /// <summary>Max length of a metric name.</summary>
    public const int MetricMaxLength = 100;

    /// <summary>Max length of a source.</summary>
    public const int SourceMaxLength = 200;

    /// <summary>Max length of an artifact key.</summary>
    public const int ArtifactKeyMaxLength = 100;

    /// <summary>Max length of a variant label.</summary>
    public const int VariantMaxLength = 50;

    /// <summary>§4.74: the Brier score of a probability <paramref name="prediction"/> against the outcome <paramref name="outcome"/>.</summary>
    public static double Brier(double prediction, double outcome)
    {
        var diff = prediction - outcome;
        return diff * diff;
    }

    /// <summary>FR-353: the valid request with its score, or <c>VALIDATION</c> with every problem in the details.</summary>
    public static Result<ValidOutcomeScore> Validate(OutcomeScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var problems = new List<string>();
        string? field = null;
        void Add(string name, string message)
        {
            field ??= name;
            problems.Add(message);
        }

        CheckTarget(input.Target, Add);

        if (string.IsNullOrWhiteSpace(input.Metric) || input.Metric.Trim().Length > MetricMaxLength)
        {
            Add("metric", $"metric is required (at most {MetricMaxLength} characters).");
        }

        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Trim().Length > SourceMaxLength)
        {
            Add("source", $"source is required (at most {SourceMaxLength} characters).");
        }

        if (input.ObservedAt is null)
        {
            Add("observedAt", "observedAt is required.");
        }

        if (input.Value is not { } value || !double.IsFinite(value))
        {
            Add("value", "value is required and must be a finite number.");
        }

        CheckKind(input, Add);

        if (problems.Count > 0)
        {
            return Result.Failure<ValidOutcomeScore>(new DomainError(EngineErrorCodes.Validation, problems[0])
            {
                Field = field,
                Details = problems,
            });
        }

        var kind = input.Kind!.Value;
        var observed = input.Value!.Value;
        var score = kind == OutcomeScoreKind.ProbabilityBrier ? Brier(input.Prediction!.Value, observed) : observed;
        return Result.Success(new ValidOutcomeScore(
            input.Target!, input.Metric!.Trim(), kind, observed, input.Prediction, score, input.ObservedAt!.Value, input.Source!.Trim()));
    }

    private static void CheckTarget(OutcomeTarget? target, Action<string, string> add)
    {
        if (target is null)
        {
            add("target", "target is required: { agentRunId } or { artifactKey, version, variant? }.");
            return;
        }

        var hasRun = target.AgentRunId is not null;
        var hasArtifact = target.ArtifactKey is not null || target.Version is not null || target.Variant is not null;
        if (hasRun == hasArtifact)
        {
            add("target", "target must be exactly one of { agentRunId } or { artifactKey, version, variant? }.");
            return;
        }

        if (hasRun)
        {
            if (target.AgentRunId == Guid.Empty)
            {
                add("target.agentRunId", "target.agentRunId must be an agent run id.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(target.ArtifactKey) || target.ArtifactKey.Length > ArtifactKeyMaxLength)
        {
            add("target.artifactKey", $"target.artifactKey is required (at most {ArtifactKeyMaxLength} characters).");
        }

        if (target.Version is not >= 1)
        {
            add("target.version", "target.version must be 1 or more.");
        }

        if (target.Variant is not null && (string.IsNullOrWhiteSpace(target.Variant) || target.Variant.Length > VariantMaxLength))
        {
            add("target.variant", $"target.variant must be a label of at most {VariantMaxLength} characters.");
        }
    }

    private static void CheckKind(OutcomeScoreInput input, Action<string, string> add)
    {
        switch (input.Kind)
        {
            case null:
                add("kind", "kind is required: number or probability_brier.");
                break;
            case OutcomeScoreKind.ProbabilityBrier:
                if (input.Prediction is not { } p || !double.IsFinite(p) || p < 0 || p > 1)
                {
                    add("prediction", "prediction is required for probability_brier and must be in [0, 1].");
                }

                if (input.Value is { } v && double.IsFinite(v) && v is not (0 or 1))
                {
                    add("value", "value must be 0 or 1 for probability_brier.");
                }

                break;
            case OutcomeScoreKind.Number:
                if (input.Prediction is not null)
                {
                    add("prediction", "prediction is only allowed for probability_brier.");
                }

                break;
            default:
                add("kind", "kind must be number or probability_brier.");
                break;
        }
    }

    /// <summary>
    /// FR-353, §4.74: the mean score per (agent key, prompt version, metric), a plain mean over the scored targets: a
    /// target scored more than once on a metric counts once, with the mean of its scores. Sorted by agent, version, metric.
    /// </summary>
    public static IReadOnlyList<PromptOutcomeMean> Means(IEnumerable<AttributedOutcomeScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        return [.. scores
            .GroupBy(s => (s.Agent, s.PromptVersion, s.Metric))
            .Select(g =>
            {
                var perTarget = g.GroupBy(s => s.TargetKey, StringComparer.Ordinal).Select(t => t.Average(s => s.Score)).ToList();
                return new PromptOutcomeMean(g.Key.Agent, g.Key.PromptVersion, g.Key.Metric, perTarget.Average(), perTarget.Count);
            })
            .OrderBy(m => m.Agent, StringComparer.Ordinal)
            .ThenBy(m => m.PromptVersion, StringComparer.Ordinal)
            .ThenBy(m => m.Metric, StringComparer.Ordinal)];
    }
}
