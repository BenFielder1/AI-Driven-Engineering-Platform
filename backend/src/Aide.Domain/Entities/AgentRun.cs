using Aide.Domain.Agents;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Entities;

/// <summary>One agent invocation (FR-23); replaces the old stack's agent_logs.</summary>
public sealed class AgentRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>The release version the run belongs to (§4.10); null only for runs outside any version.</summary>
    public Guid? ReleaseVersionId { get; set; }

    public AgentKind Agent { get; set; }

    /// <summary>The specialist key of a plan step run (FR-121), e.g. <c>data-engineer</c> (v2 keys such as <c>db-engineer</c> on runs before FR-129); null for base agents.</summary>
    public string? AgentKey { get; set; }

    public string? PresetId { get; set; }

    public string? PresetVersion { get; set; }

    /// <summary>
    /// The pack skills the specialist loaded (FR-121) as raw JSON: <c>[{id, version, sections[]}]</c>, where an empty
    /// <c>sections</c> means the full skill. Null for runs recorded before v4.
    /// </summary>
    public string? Packs { get; set; }

    /// <summary>The stack catalog version the run resolved its skills from (FR-121); null for runs recorded before v4.</summary>
    public string? CatalogVersion { get; set; }

    /// <summary>The LLM connection that paid for the run (never the key itself).</summary>
    public Guid? CredentialId { get; set; }

    public Phase Phase { get; set; }

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

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
