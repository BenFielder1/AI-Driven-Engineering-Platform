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
    IReadOnlyDictionary<string, string[]>? Errors,
    IReadOnlyList<StackViolationDto>? Violations = null);

/// <summary>FR-116: one broken compatibility rule of a <c>STACK_INCOMPATIBLE</c> problem.</summary>
internal sealed record StackViolationDto(string Layer, string Option, string Rule, string Reason);

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
    string? MyRole,
    string? PresetId = null,
    string? PresetName = null,
    string? PresetVersion = null);

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

/// <summary>FR-159: the new project's LLM provider (wire value), default model and, optionally, the caller's connection.</summary>
internal sealed record CreateProjectLlmRequest(string Provider, string DefaultModel, Guid? CredentialId);

internal sealed record CreateProjectRequest(
    string Name,
    string Idea,
    ApprovalMode? ApprovalMode,
    double? ComplianceThreshold,
    int? MaxFilesPerStep,
    int? MaxReviewCycles,
    long? TokenBudget,
    CreateProjectGitRequest Git,
    CreateProjectLlmRequest? Llm = null);

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
    ReleaseSummaryDto? LatestRelease,
    DeploymentDto? LatestDeployment = null,
    IReadOnlyList<ComponentProductionUrlDto>? ProductionUrls = null);

/// <summary>FR-156: a deployment target's production URL, as on the project state.</summary>
internal sealed record ComponentProductionUrlDto(string Component, string Provider, string? Url);

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

internal sealed record DeploymentUserDto(Guid Id, string Name);

internal sealed record DeploymentDto(
    Guid Id,
    string Target,
    string Provider,
    string Status,
    string? Url,
    string GitRef,
    string GitSha,
    string ReleaseVersion,
    string? ErrorCode,
    string Trigger,
    DeploymentUserDto? TriggeredBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadyAt,
    string? Component = null);

internal sealed record ReleaseVersionCheckpointsDto(int Current, int Max);

internal sealed record ReleaseVersionDto(
    Guid Id,
    string Version,
    string Status,
    string? Tag,
    string? BaseTag,
    string? BranchName,
    string? PullRequestUrl,
    ReleaseVersionCheckpointsDto Checkpoints,
    string? PresetId,
    string? PresetVersion,
    string? PresetName,
    ActorDto? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt);

internal sealed record StartReleaseVersionResultDto(ReleaseVersionDto ReleaseVersion, ProjectStateDto State);

// FR-156: stack catalog and compositions (FR-113, FR-116, FR-117, FR-118).

internal sealed record StackLayerDto(string Id, string Name, string? Description);

internal sealed record StackModeDto(string Id, string Name);

internal sealed record StackOptionDto(string Id, string Name, string? Description, IReadOnlyList<StackModeDto>? Modes, string? Mode);

internal sealed record StackChoiceDto(string Id, string Layer, string Name, IReadOnlyList<StackOptionDto> Options);

internal sealed record StackCatalogDto(string Version, string ContentHash, IReadOnlyList<StackLayerDto> Layers, IReadOnlyList<StackChoiceDto> Choices);

internal sealed record StackComponentDto(string Pack, string Path);

internal sealed record StackDataDto(string Pack, string? Mode);

internal sealed record StackVcsDto(string Pack, Guid? InstanceId, bool SelfManaged);

internal sealed record StackDeployDto(string? FrontendHost, string? BackendHost, string? DatabaseHost, string? Local);

internal sealed record StackCaveatDto(string Layer, string Option, string Rule, string Text);

internal sealed record StackCompositionDto(
    StackComponentDto? Frontend,
    StackComponentDto? Backend,
    StackDataDto? Data,
    StackVcsDto? Vcs,
    StackDeployDto? Deploy,
    string? OriginModel,
    IReadOnlyList<StackCaveatDto>? Caveats);

internal sealed record LegacyPresetDto(string Label);

internal sealed record ProjectStackDto(
    string? Version,
    string? VersionStatus,
    StackCompositionDto? Composition,
    string? CatalogVersion,
    bool Locked,
    string? LockedReason,
    LegacyPresetDto? LegacyPreset,
    string? RepoVcs);

internal sealed record StackComponentSelection(string Pack, string? Path);

internal sealed record StackDataSelection(string Pack, string? Mode);

internal sealed record StackVcsSelection(string Pack, Guid? InstanceId, bool? SelfManaged);

internal sealed record StackDeploySelection(string? FrontendHost, string? BackendHost, string? DatabaseHost, string? Local);

/// <summary>The body of <c>PUT /api/projects/:id/stack</c> and the <c>stack</c> of <c>POST /versions</c>: a left-out layer is None.</summary>
internal sealed record StackCompositionRequest(
    StackComponentSelection? Frontend,
    StackComponentSelection? Backend,
    StackDataSelection? Data,
    StackVcsSelection? Vcs,
    StackDeploySelection? Deploy,
    string? OriginModel);

internal sealed record DetectStackGitRequest(string Provider, Guid? InstanceId, string Repo, string? Ref, string? Token, string? ConnectionId);

internal sealed record DetectStackRequest(DetectStackGitRequest Git);

internal sealed record StackDetectedChoiceDto(string Choice, string Option, string? Path, string? Mode, IReadOnlyList<string> Evidence);

internal sealed record StackDetectionCandidateDto(string Option, string? Path, IReadOnlyList<string> Evidence);

internal sealed record StackDetectionConflictDto(string Choice, IReadOnlyList<StackDetectionCandidateDto> Candidates);

internal sealed record StackDetectionDto(string Ref, string Commit, IReadOnlyList<StackDetectedChoiceDto> Suggestions, IReadOnlyList<StackDetectionConflictDto> Conflicts, bool Truncated);

// FR-156: `aide databases` (the databases of GET /settings/deployment) and `aide migrations`.

internal sealed record DeploymentDatabaseDto(
    string Environment,
    string Provider,
    string Status,
    string? Name,
    string? Region,
    string? Plan,
    DateTimeOffset? ExpiresAt,
    bool Paused,
    string? ResourceUrl,
    IReadOnlyList<string>? Warnings);

internal sealed record DeploymentDatabasesDto(IReadOnlyList<DeploymentDatabaseDto> Databases);

internal sealed record MigrationFileDto(string Version, string Name);

internal sealed record MigrationRunDto(
    Guid Id,
    string Environment,
    string? Provider,
    string GitSha,
    string Status,
    IReadOnlyList<MigrationFileDto>? Files,
    long TotalMs,
    string? ErrorCode,
    DateTimeOffset StartedAt);
