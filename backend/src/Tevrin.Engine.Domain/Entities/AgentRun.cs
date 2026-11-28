namespace Tevrin.Engine.Domain.Entities;

/// <summary>One agent invocation (FR-23); replaces the old stack's agent_logs. FR-268: moved to the engine's run log, keyed by
/// the agent key and the stage id (D-0.7.0-32).</summary>
public sealed class AgentRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>The release version the run belongs to (§4.10); null only for runs outside any version.</summary>
    public Guid? ReleaseVersionId { get; set; }

    /// <summary>FR-294: the pipeline run the agent run belongs to (nullable, D-0.7.0-28).</summary>
    public Guid? PipelineRunId { get; set; }

    public string Agent { get; set; } = string.Empty;

    /// <summary>The specialist key of a plan step run (FR-121), e.g. <c>data-engineer</c> (0.2.0 keys such as <c>db-engineer</c> on runs before FR-129); null for base agents.</summary>
    public string? AgentKey { get; set; }

    public string? PresetId { get; set; }

    public string? PresetVersion { get; set; }

    /// <summary>
    /// The pack skills the specialist loaded (FR-121) as raw JSON: <c>[{id, version, sections[]}]</c>, where an empty
    /// <c>sections</c> means the full skill. Null for runs recorded before 0.4.0.
    /// </summary>
    public string? Packs { get; set; }

    /// <summary>The stack catalog version the run resolved its skills from (FR-121); null for runs recorded before 0.4.0.</summary>
    public string? CatalogVersion { get; set; }

    /// <summary>The LLM connection that paid for the run (never the key itself).</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>The stage the run belongs to (the stage id; for the SDLC the phase wire value). Stored in the
    /// <c>phase</c> column (D-0.7.0-32).</summary>
    public string Stage { get; set; } = string.Empty;

    public Guid? StepId { get; set; }

    public LlmProvider Provider { get; set; }

    public string Model { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;

    public IReadOnlyList<InputArtifactRef> InputArtifacts { get; set; } = [];

    /// <summary>Gate/artifact rejection comment, Guardian feedback or clarifying answers given to the agent (FR-13, FR-27).</summary>
    public string? UserFeedback { get; set; }

    /// <summary>Structured agent output as raw JSON (e.g. clarifying questions, FR-27). Schemaless.</summary>
    public string? Output { get; set; }

    /// <summary>Tool calls made during the run (FR-71) as raw JSON (array); <c>[]</c> when none.</summary>
    public string ToolCalls { get; set; } = "[]";

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Queued;

    public int Attempts { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public decimal CostEstimate { get; set; }

    public long DurationMs { get; set; }

    /// <summary>FR-297: the rendered prompt and messages sent to the model, redacted and capped at
    /// <see cref="Agents.AgentRunInput.MaxBytes"/> UTF-8 bytes; null on runs recorded before 0.7.0.</summary>
    public string? Input { get; set; }

    /// <summary>FR-297: <see cref="Input"/> was cut at the cap.</summary>
    public bool InputTruncated { get; set; }

    /// <summary>FR-350: the validator findings on this attempt's output (JSON array of
    /// <c>{ path?, claim?, reason, severity }</c>); <c>null</c> when the stage has no validator.</summary>
    public string? ValidatorFindings { get; set; }

    /// <summary>FR-351: the agent feedback entries injected into this run's prompt (<c>agent_feedback</c> ids, newest
    /// first); <c>null</c> when the stage doesn't inject feedback.</summary>
    public IReadOnlyList<Guid>? FeedbackIds { get; set; }

    /// <summary>FR-355: the 0-based map item index the run worked on; null outside a map.</summary>
    public int? ItemIndex { get; set; }

    /// <summary>FR-356, §4.69: the 1-based conversation turn this run produced; null outside a conversation.</summary>
    public int? TurnIndex { get; set; }

    /// <summary>FR-356: the participant role that spoke this turn (the definition's <c>role</c>); null outside a
    /// conversation.</summary>
    public string? TurnRole { get; set; }

    /// <summary>FR-359, §4.73: the saved tool loop (messages so far and the pending destructive call) as redacted JSON, at
    /// most 1 MB; null when no destructive call is waiting. The call's state (pending_approval, approved/denied, resumed)
    /// is read from this and the run's <c>tool_approval</c> gate.</summary>
    public string? ToolLoopState { get; set; }

    /// <summary>FR-297: milliseconds from the call to the first streamed token of the last attempt; null when the provider
    /// didn't stream (or before 0.7.0).</summary>
    public long? LatencyMs { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>An artifact version an agent run used as input (FR-23).</summary>
public sealed record InputArtifactRef(Guid ArtifactId, string Type, int Version);
