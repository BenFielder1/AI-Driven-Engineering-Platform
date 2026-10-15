using Aide.Domain.Execution;

namespace Aide.Domain.Entities;

/// <summary>One attempt of an execution step (FR-57): the agent run, the Guardian outcome and what it produced.</summary>
public sealed class ExecutionStepAttempt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid StepId { get; set; }

    public int Attempt { get; set; }

    public Guid? AgentRunId { get; set; }

    public StepAttemptOutcome Outcome { get; set; }

    /// <summary>Guardian errors that rejected the attempt.</summary>
    public IReadOnlyList<GuardianViolation> Violations { get; set; } = [];

    /// <summary>Guardian warnings recorded with the attempt.</summary>
    public IReadOnlyList<GuardianViolation> Warnings { get; set; } = [];

    /// <summary>Files committed by an accepted attempt.</summary>
    public IReadOnlyList<FileManifestEntry> ProducedFiles { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
