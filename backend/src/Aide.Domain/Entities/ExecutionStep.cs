using Aide.Domain.Execution;

namespace Aide.Domain.Entities;

/// <summary>One step of an approved plan version (FR-30–FR-34).</summary>
public sealed class ExecutionStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Guid PlanArtifactId { get; set; }

    public int StepNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Task { get; set; } = string.Empty;

    public IReadOnlyList<DeclaredFile> DeclaredFiles { get; set; } = [];

    /// <summary>Earlier step numbers this step depends on.</summary>
    public IReadOnlyList<int> Dependencies { get; set; } = [];

    public string? UserInstructions { get; set; }

    public StepStatus Status { get; set; } = StepStatus.Pending;

    public int Attempt { get; set; }

    public IReadOnlyList<GuardianViolation>? Violations { get; set; }

    public IReadOnlyList<FileManifestEntry>? FileManifest { get; set; }

    /// <summary>The branch the step was committed on (FR-34).</summary>
    public string? BranchName { get; set; }

    public string? CommitSha { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public uint RowVersion { get; set; }
}
