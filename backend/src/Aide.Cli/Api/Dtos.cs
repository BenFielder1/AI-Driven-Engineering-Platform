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

/// <summary><c>localSourcePath</c>: FR-73 Local import only, a directory under the server's import root.</summary>
internal sealed record CreateProjectGitRequest(GitProvider Provider, string? RepoUrl, string? DefaultBranch, string? Token, string? LocalSourcePath = null);

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
    CreateProjectLlmRequest? Llm = null,
    string? StackSource = null,
    string? Source = null,
    CreateProjectImportRequest? Import = null);

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

// FR-194: `aide analysis` (GET /analysis, POST /analysis/run, the generic approve and reject of the stack_profile).

internal sealed record CodebaseSummaryStatusDto(string Status, Guid? RunId, Guid? ArtifactId, int? ArtifactVersion);

internal sealed record StackAnalysisRunDto(
    Guid JobId,
    Guid? RunId,
    string Status,
    string Trigger,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<string>? Warnings,
    Guid? ArtifactId,
    int? ArtifactVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

internal sealed record StackProfileCandidateDto(string Option, string? Path, string Source);

internal sealed record StackProfileLayerDto(
    string? Option,
    string? Path,
    string? Mode,
    string Status,
    string? Confidence,
    string? Technology,
    IReadOnlyList<StackProfileCandidateDto>? Candidates);

internal sealed record StackProfileLayersDto(StackProfileLayerDto Frontend, StackProfileLayerDto Backend, StackProfileLayerDto Data, StackProfileLayerDto Vcs);

internal sealed record StackProfileHostsDto(string? FrontendHost, string? BackendHost, string? DatabaseHost, string? Local, string? OriginModel);

internal sealed record StackProfileDocumentDto(bool Generalist, StackProfileLayersDto Layers, StackProfileHostsDto? Hosts);

internal sealed record StackProfileViewDto(
    Guid ArtifactId,
    int Version,
    string Status,
    string Integrity,
    bool AuthoredByAgent,
    DateTimeOffset CreatedAt,
    StackProfileDocumentDto? Document,
    IReadOnlyList<string>? Errors);

internal sealed record StackProfileBlankDto(string Layer, string Status, string? Question, string? Technology, IReadOnlyList<StackProfileCandidateDto>? Candidates);

internal sealed record AnalysisDeploymentDto(bool Available, string? Reason);

internal sealed record AnalysisDto(
    Guid ReleaseVersionId,
    string ReleaseVersion,
    string Phase,
    string StackSource,
    bool HasExistingCode,
    string? BaseCommitSha,
    CodebaseSummaryStatusDto? Summary,
    StackAnalysisRunDto? Run,
    StackProfileViewDto? Profile,
    IReadOnlyList<StackProfileBlankDto>? NeedsInput,
    IReadOnlyList<string>? UnresolvedLayers,
    bool CanApprove,
    string? ApproveBlockedReason,
    bool CanContinue,
    StackCompositionDto? Composition,
    AnalysisDeploymentDto? Deployment);

internal sealed record StackAnalysisQueuedDto(Guid JobId, string Trigger, DateTimeOffset QueuedAt);

/// <summary>The body of approving a <c>stack_profile</c>: a comment, and <c>generalist</c> for "Continue with No stack (general)" (FR-181).</summary>
internal sealed record ApproveStackProfileRequest(string? Comment, bool? Generalist);

// FR-194: `aide sources` (GET/POST /sources, POST /sources/:importId/reimport, DELETE /sources/:importId, POST /sources/check).

internal sealed record TrackerSelectionRequest(IReadOnlyList<string>? Keys, string? Query, string? ContainerId);

internal sealed record TrackerImportOptionsRequest(bool IncludeChildren, bool IncludeComments, bool IncludeAssignees);

/// <summary>A <c>sourceImports[]</c> entry and the body of <c>POST /sources</c>.</summary>
internal sealed record SourceImportRequest(string Provider, Guid? ConnectionId, TrackerSelectionRequest Selection, TrackerImportOptionsRequest? Options);

internal sealed record TrackerSelectionDto(IReadOnlyList<string>? Keys, string? Query, string? ContainerId);

internal sealed record TrackerItemSummaryDto(string Key, string Title, string Url, string Status, string StatusCategory);

internal sealed record SourceImportDto(
    Guid Id,
    Guid ReleaseVersionId,
    string Provider,
    Guid? ConnectionId,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    TrackerSelectionDto? Selection,
    int ItemCount,
    int RenderedBytes,
    IReadOnlyList<TrackerItemSummaryDto>? Items,
    Guid? SourceArtifactId,
    ActorDto? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

internal sealed record SourceSnapshotDto(Guid ArtifactId, int Version, string ContentHash, DateTimeOffset CreatedAt);

internal sealed record SourceDraftStateDto(Guid ArtifactId, int Version, int? BuiltFromSnapshotVersion, bool BehindSnapshot);

internal sealed record ProjectSourcesDto(
    Guid? ReleaseVersionId,
    IReadOnlyList<SourceImportDto>? Imports,
    SourceSnapshotDto? CurrentSnapshot,
    SourceDraftStateDto? Draft,
    bool CanChange);

internal sealed record SourceDriftItemDto(Guid ImportId, string Provider, string Key, string Title, string Url, string Drift);

internal sealed record SourcesCheckDto(IReadOnlyList<SourceDriftItemDto>? Items, string? SnapshotContentHash, DateTimeOffset CheckedAt);

// FR-194: `aide deployments groups` (the groups of GET /settings/deployment).

internal sealed record DeploymentGroupPartDto(string Part, string? Environment, string Status, string? StatusReason, string? ResourceId);

internal sealed record DeploymentGroupBindingDto(Guid ConnectionId, string Label, DeploymentUserDto? BoundBy, string Status);

internal sealed record DeploymentGroupDto(
    Guid? Id,
    string Provider,
    string Capability,
    IReadOnlyList<string> Parts,
    string? Name,
    DeploymentGroupBindingDto? Binding,
    string? SharedResourceId,
    string? SharedResourceUrl,
    string Status,
    string? StatusReason,
    IReadOnlyList<DeploymentGroupPartDto>? PartStatuses,
    bool CanRetry);

internal sealed record DeploymentGroupsDto(IReadOnlyList<DeploymentGroupDto>? Groups);

/// <summary>FR-73: the versions of an imported repository (FR-194 <c>project create --infer-stack</c> needs code to analyse).</summary>
internal sealed record CreateProjectImportRequest(string DevelopmentVersion, string? ReleasedTag);
