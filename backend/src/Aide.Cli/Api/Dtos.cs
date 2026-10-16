using System.Text.Json.Nodes;

namespace Aide.Cli.Api;

// Typed views of the API contract (backend/openapi/api.json), limited to what the CLI reads or sends.
// Response enum fields are kept as their snake_case wire strings, so a newer server that adds a value
// still works with an older CLI; request enums use the shared snake_case converter (ApiJson.Options).

internal sealed record ProblemDetailsDto(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? ErrorCode,
    string? TraceId,
    IReadOnlyList<string>? Details,
    IReadOnlyDictionary<string, string[]>? Errors);

internal sealed record UserDto(Guid Id, string Email, string Name, string SystemRole);

internal sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Limit, int Total);

internal sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    string CurrentPhase,
    string Status,
    int Checkpoint,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    string ApprovalMode,
    string? MyRole,
    DateTimeOffset UpdatedAt);

internal sealed record ProjectDto(
    Guid Id,
    string Name,
    string Slug,
    string CurrentPhase,
    string Status,
    int Checkpoint,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    string ApprovalMode,
    string? MyRole);

internal enum ApprovalMode
{
    Strict,
    Advisory,
    Autonomous,
}

internal enum GitProvider
{
    GithubPat,
    Local,
}

internal sealed record CreateProjectGitRequest(GitProvider Provider, string? RepoUrl, string? DefaultBranch, string? Token);

internal sealed record CreateProjectRequest(
    string Name,
    string Idea,
    ApprovalMode? ApprovalMode,
    double? ComplianceThreshold,
    int? MaxFilesPerStep,
    int? MaxReviewCycles,
    long? TokenBudget,
    CreateProjectGitRequest Git);

internal sealed record ArtifactStateDto(string Type, int? LatestVersion, string? LatestStatus, int? CurrentLockedVersion);

internal sealed record ExecutionCountsDto(int Total, int Pending, int InProgress, int Completed, int Rejected, int Blocked);

internal sealed record ReviewStateDto(int Cycle, int MaxCycles, double? LatestScore, string? LatestDecision, string? DriftLevel, string Status);

internal sealed record GateSummaryDto(Guid Id, string Type, DateTimeOffset CreatedAt, bool Overdue);

internal sealed record PullRequestSummaryDto(string Provider, string BaseBranch, string HeadBranch, int? Number, string? Url);

internal sealed record ReleaseSummaryDto(Guid Id, int Checkpoint, string Status, string Tag, DateTimeOffset StartedAt);

internal sealed record ProjectStateDto(
    Guid ProjectId,
    string Phase,
    string Status,
    int Checkpoint,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    bool ExecutionPaused,
    IReadOnlyList<ArtifactStateDto> Artifacts,
    ExecutionCountsDto Execution,
    ReviewStateDto Review,
    IReadOnlyList<GateSummaryDto> PendingGates,
    long TokensUsed,
    long? TokenBudget,
    string ExecutionStatus,
    PullRequestSummaryDto? PullRequest,
    ReleaseSummaryDto? LatestRelease);

internal sealed record ActorDto(Guid? Id, string Name, bool IsSystem);

internal sealed record ArtifactAuthorDto(ActorDto? User, Guid? AgentRunId);

internal sealed record ArtifactDto(
    Guid Id,
    string Type,
    int Version,
    int? ParentVersion,
    string Content,
    string ContentHash,
    string Status,
    int CheckpointAtCreation,
    ArtifactAuthorDto? Author,
    ActorDto? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? RejectionComment,
    DateTimeOffset CreatedAt,
    string Integrity);

internal sealed record ArtifactSummaryDto(Guid Id, string Type, int Version, string Status, string Integrity, DateTimeOffset CreatedAt, ArtifactAuthorDto? Author);

internal sealed record CreateArtifactDraftRequest(string Content, int? BaseVersion);

internal sealed record CommentRequest(string? Comment);

internal sealed record GateDto(Guid Id, string Type, string Status, DateTimeOffset CreatedAt);

internal sealed record ExecutionStateDto(string Status, bool Paused, int? CurrentStepNumber, ExecutionCountsDto Counts);

internal sealed record ReviewStartedDto(Guid JobId, int Cycle);

internal sealed record ReleaseDto(
    Guid Id,
    int Checkpoint,
    string Status,
    string Tag,
    string? MergeCommitSha,
    string? PullRequestUrl,
    IReadOnlyList<string>? ConflictingFiles,
    string? ErrorCode,
    string? ErrorMessage,
    int Attempt);

internal sealed record RollbackRequest(int TargetCheckpoint, string Reason);

internal sealed record RollbackDto(
    Guid Id,
    int FromCheckpoint,
    int ToCheckpoint,
    string RestoredPhase,
    string Reason,
    IReadOnlyList<Guid>? InvalidatedArtifactIds,
    IReadOnlyList<Guid>? ResetStepIds,
    IReadOnlyList<Guid>? CancelledGateIds);

internal sealed record ActivityItemDto(Guid Id, string Type, string? Category, DateTimeOffset OccurredAt, ActorDto? Actor, string Summary, JsonObject? Payload);

internal sealed record ActivityPageDto(IReadOnlyList<ActivityItemDto> Items, string? NextBefore);
