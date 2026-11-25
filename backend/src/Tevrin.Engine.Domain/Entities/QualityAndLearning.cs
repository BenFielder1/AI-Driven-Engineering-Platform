namespace Tevrin.Engine.Domain.Entities;

/// <summary>FR-351: where an agent feedback entry came from. Wire values: <c>human</c>, <c>stage</c>.</summary>
public enum AgentFeedbackSource
{
    Human = 0,
    Stage = 1,
}

/// <summary>FR-353, §4.74: how an outcome score is computed. Wire values: <c>number</c>, <c>probability_brier</c>.</summary>
public enum OutcomeScoreKind
{
    Number = 0,
    ProbabilityBrier = 1,
}

/// <summary>
/// FR-351, §5: one feedback entry for an agent key of a pipeline in a project (<c>agent_feedback</c>). Added by a person
/// through the API, or by a stage with <c>writes_feedback</c>. It belongs to the project and goes with it; a deleted entry
/// keeps its row (<see cref="DeletedAt"/>) so rendered prompts that used it still resolve.
/// </summary>
public sealed class AgentFeedback
{
    /// <summary>Max length of <see cref="Text"/> (FR-351).</summary>
    public const int TextMaxLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public string PipelineId { get; init; } = string.Empty;

    public string AgentKey { get; init; } = string.Empty;

    public Guid ProjectId { get; init; }

    public AgentFeedbackSource Source { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>The run that wrote the entry (stage entries) or that it was written about; <c>null</c> once deleted.</summary>
    public Guid? RunId { get; init; }

    /// <summary>The agent run the entry is about; <c>null</c> once deleted.</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>The user who added a human entry; <c>null</c> for stage entries and once that user is deleted.</summary>
    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Soft delete: set when the entry is removed; deleted entries are never injected.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// FR-353, §5, §4.74: one external outcome score for a run (<c>outcome_scores</c>). It targets exactly one of an agent run
/// (<see cref="AgentRunId"/>) or an artifact version (<see cref="ArtifactId"/>). For <see cref="OutcomeScoreKind.Number"/>
/// the score is the value; for <see cref="OutcomeScoreKind.ProbabilityBrier"/> it is the Brier score computed by the
/// engine from <see cref="Prediction"/> and <see cref="Value"/>. Rows are immutable and go with the run.
/// </summary>
public sealed class OutcomeScore
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PipelineRunId { get; init; }

    public Guid? AgentRunId { get; init; }

    public Guid? ArtifactId { get; init; }

    public string Metric { get; init; } = string.Empty;

    public OutcomeScoreKind Kind { get; init; }

    /// <summary>The observed value (for a Brier score, the outcome 0 or 1).</summary>
    public double Value { get; init; }

    /// <summary>The predicted probability (Brier scores only).</summary>
    public double? Prediction { get; init; }

    public double Score { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>Who or what reported the outcome (free text, e.g. a system name).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The user who posted the score; <c>null</c> for token callers and once that user is deleted.</summary>
    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
