using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Aide.Api.Features.Git;
using Aide.Api.Features.Release;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.Api.Features.Projects;

/// <summary>
/// A project as shown on its overview and settings pages (FR-5, FR-7). <c>myRole</c> is the caller's project role (<c>null</c>
/// for an Admin who isn't a member); a <c>null</c> <c>tokenBudget</c> means unlimited. <c>checkpoint</c> is the lifecycle
/// checkpoint within the current release version <c>releaseVersionId</c> (§4.10, D-V2-11: was <c>projectVersion</c>);
/// <c>releasedVersion</c> is the highest released version (<c>null</c>: not released) and <c>developmentVersion</c> the
/// version in development (<c>null</c>: none) (FR-62). <c>idea</c> is the request of the version in development.
/// <c>presetId</c>, <c>presetName</c> and <c>presetVersion</c> are the tech stack preset pinned on the current release
/// version (FR-74; all <c>null</c>: no preset). <c>deploymentsEnabled</c>: the project has deployment settings (FR-88), so
/// the Deployments page is in its navigation (§7.2).
/// </summary>
public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Slug,
    string Idea,
    Phase CurrentPhase,
    ProjectStatus Status,
    int Checkpoint,
    Guid? ReleaseVersionId,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    ApprovalMode ApprovalMode,
    decimal ComplianceThreshold,
    int MaxFilesPerStep,
    int MaxReviewCycles,
    long? TokenBudget,
    long TokensUsed,
    bool ExecutionPaused,
    ProjectRole? MyRole,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PresetId,
    string? PresetName,
    string? PresetVersion,
    bool DeploymentsEnabled);

/// <summary>
/// A row of the projects list (FR-6, FR-62). <c>myRole</c> is <c>null</c> for an Admin who isn't a member;
/// <c>releasedVersion</c> / <c>developmentVersion</c> are the two version badges (<c>null</c> when not released / nothing in
/// development); <c>checkpoint</c> was <c>projectVersion</c> (D-V2-11); <c>presetId</c>/<c>presetName</c>/<c>presetVersion</c>
/// are the preset chip (FR-74, §7.2; <c>null</c>: no preset).
/// </summary>
public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    Phase CurrentPhase,
    ProjectStatus Status,
    int Checkpoint,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    ApprovalMode ApprovalMode,
    ProjectRole? MyRole,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PresetId,
    string? PresetName,
    string? PresetVersion);

/// <summary>Sort keys of the projects list (FR-6).</summary>
public enum ProjectSortBy
{
    Created = 0,
    Updated = 1,
    Name = 2,
}

public enum SortOrder
{
    Asc = 0,
    Desc = 1,
}

/// <summary>
/// Repository step of the new-project wizard (FR-5, FR-35, FR-82, FR-86). <c>mode</c> picks the Git provider.
/// <c>github_app</c>: a repo picked or created through the AIDE GitHub App; send <c>installationId</c> and
/// <c>repoFullName</c> (<c>owner/name</c>), no <c>repoUrl</c> or <c>token</c> (409 <c>GITHUB_APP_NOT_INSTALLED</c> when the
/// App can't access the repo). <c>github_pat</c>: <c>repoUrl</c> plus an optional <c>token</c> (FR-83, the v1 behaviour).
/// <c>local</c>: no remote. <c>provider</c> is the v1 name of <c>mode</c>, kept so v1 clients work: send one of the two;
/// when both are sent they must be equal (else 400 <c>VALIDATION</c> on <c>git.mode</c>).
/// </summary>
public sealed record CreateProjectGitRequest
{
    private readonly GitProvider _mode;
    private readonly GitProvider _provider;

    /// <summary>The Git provider (FR-82): <c>github_app</c>, <c>github_pat</c> or <c>local</c>.</summary>
    public GitProvider Mode
    {
        get => _mode;
        init
        {
            _mode = value;
            ModeSent = true;
        }
    }

    /// <summary>v1 alias of <c>mode</c> (deprecated; send <c>mode</c>).</summary>
    public GitProvider Provider
    {
        get => _provider;
        init
        {
            _provider = value;
            ProviderSent = true;
        }
    }

    /// <summary>Whether <c>mode</c> was in the request (JSON setters run only for sent properties).</summary>
    [JsonIgnore]
    public bool ModeSent { get; private init; }

    /// <summary>Whether <c>provider</c> was in the request.</summary>
    [JsonIgnore]
    public bool ProviderSent { get; private init; }

    /// <summary><c>github_app</c>: the App installation that grants access to the repo (from the repo picker).</summary>
    [Range(1, ProjectLimits.MaxTokenBudget)]
    public long? InstallationId { get; init; }

    /// <summary><c>github_app</c>: the repository as <c>owner/name</c>.</summary>
    [MaxLength(ProjectLimits.RepoFullNameMaxLength)]
    public string? RepoFullName { get; init; }

    /// <summary>Required for <c>github_pat</c>: <c>https://github.com/OWNER/REPO</c>, optionally ending in <c>.git</c>.</summary>
    [MaxLength(ProjectLimits.UrlMaxLength)]
    public string? RepoUrl { get; init; }

    [StringLength(ProjectLimits.BranchMaxLength, MinimumLength = 1)]
    public string DefaultBranch { get; init; } = "main";

    /// <summary>GitHub access token (<c>github_pat</c> only). Stored encrypted and never returned.</summary>
    [MaxLength(ProjectLimits.SecretMaxLength)]
    public string? Token { get; init; }

    /// <summary>
    /// FR-73, §8 Testing: <c>local</c> + <c>source: import</c> only, and only where the server sets <c>Git:Local:ImportRoot</c>
    /// (Development and E2E): the existing Git repository directory to import, absolute or relative to that root, which it
    /// must resolve under (else 400 <c>VALIDATION</c> on <c>git.localSourcePath</c>). Omit it otherwise.
    /// </summary>
    [MaxLength(ProjectLimits.UrlMaxLength)]
    public string? LocalSourcePath { get; init; }
}

/// <summary>
/// FR-73: the versions of an imported repository. <c>releasedTag</c> is the tag of the currently released version, picked
/// from the repository's GitHub Releases and semver tags (<c>null</c> or omitted: "Never released"); it must exist in the
/// repository (400 <c>TAG_NOT_FOUND</c>) and name a semver version (400 <c>VERSION_INVALID</c>). <c>developmentVersion</c>
/// is the version to develop (semver, a leading <c>v</c> is stripped; 400 <c>VERSION_INVALID</c>), greater than the
/// released one (409 <c>VERSION_NOT_GREATER</c>).
/// </summary>
public sealed record CreateProjectImportRequest
{
    [MaxLength(ImportRules.TagMaxLength)]
    public string? ReleasedTag { get; init; }

    [Required]
    [MaxLength(Versions.VersionLimits.VersionMaxLength)]
    public required string DevelopmentVersion { get; init; }
}

/// <summary>The new-project wizard (FR-5). Omitted settings take the FR-5 defaults.</summary>
public sealed record CreateProjectRequest
{
    [Required]
    [StringLength(ProjectLimits.NameMaxLength, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    [StringLength(ProjectLimits.IdeaMaxLength, MinimumLength = 1)]
    public required string Idea { get; init; }

    public ApprovalMode ApprovalMode { get; init; } = ProjectDefaults.Mode;

    [Range(ProjectLimits.MinComplianceThreshold, ProjectLimits.MaxComplianceThreshold)]
    public decimal ComplianceThreshold { get; init; } = ProjectDefaults.ComplianceThreshold;

    [Range(ProjectLimits.MinFilesPerStep, ProjectLimits.MaxFilesPerStep)]
    public int MaxFilesPerStep { get; init; } = ProjectDefaults.MaxFilesPerStep;

    [Range(ProjectLimits.MinReviewCycles, ProjectLimits.MaxReviewCycles)]
    public int MaxReviewCycles { get; init; } = ProjectDefaults.MaxReviewCycles;

    /// <summary>Token budget (at least 1000); <c>null</c> or omitted means unlimited.</summary>
    [Range(ProjectLimits.MinTokenBudget, ProjectLimits.MaxTokenBudget)]
    public long? TokenBudget { get; init; }

    public required CreateProjectGitRequest Git { get; init; }

    /// <summary>
    /// FR-66 (<c>source: scratch</c> only): the first release version (semver, a leading <c>v</c> is stripped; default
    /// <c>0.1.0</c>). The first release tags it.
    /// </summary>
    [MaxLength(Versions.VersionLimits.VersionMaxLength)]
    public string? InitialVersion { get; init; }

    /// <summary>
    /// FR-73: <c>scratch</c> (default) starts from scratch with <c>initialVersion</c>; <c>import</c> imports the existing
    /// repository named by <c>git</c> with <c>import</c> (required) and <c>idea</c> as the request for the version to develop.
    /// </summary>
    public ProjectSource Source { get; init; } = ProjectSource.Scratch;

    /// <summary>FR-73: required with <c>source: import</c>, not allowed otherwise.</summary>
    public CreateProjectImportRequest? Import { get; init; }

    /// <summary>
    /// FR-74 (wizard Stack step): a preset id from <c>GET /api/presets</c>, pinned (with its version and content hash) on
    /// the version to develop; omitted or <c>null</c> = no preset (general). Unknown: 400 <c>PRESET_NOT_FOUND</c>.
    /// </summary>
    [MaxLength(Aide.Domain.Presets.PresetRules.IdMaxLength)]
    public string? PresetId { get; init; }

    /// <summary>
    /// FR-88 (wizard Deployment step): deploy to Vercel with one of the caller's own Vercel connections, linking an existing
    /// Vercel project or creating one; omitted or <c>null</c> = no deployment (it can be set later in Settings →
    /// Deployment). Needs a GitHub-backed project (400 <c>VALIDATION</c> on <c>deployment.provider</c> for Local).
    /// </summary>
    public Deployments.CreateProjectDeploymentRequest? Deployment { get; init; }
}

/// <summary>Partial update of a project (FR-7). Omitted fields are unchanged.</summary>
public sealed record UpdateProjectRequest
{
    [StringLength(ProjectLimits.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    public ApprovalMode? ApprovalMode { get; init; }

    [Range(ProjectLimits.MinComplianceThreshold, ProjectLimits.MaxComplianceThreshold)]
    public decimal? ComplianceThreshold { get; init; }

    [Range(ProjectLimits.MinFilesPerStep, ProjectLimits.MaxFilesPerStep)]
    public int? MaxFilesPerStep { get; init; }

    [Range(ProjectLimits.MinReviewCycles, ProjectLimits.MaxReviewCycles)]
    public int? MaxReviewCycles { get; init; }

    /// <summary>Sets the token budget (at least 1000). To remove the budget, send <c>clearTokenBudget: true</c>.</summary>
    [Range(ProjectLimits.MinTokenBudget, ProjectLimits.MaxTokenBudget)]
    public long? TokenBudget { get; init; }

    /// <summary><c>true</c> removes the token budget (unlimited). Can't be combined with <c>tokenBudget</c>.</summary>
    public bool ClearTokenBudget { get; init; }
}

/// <summary>Latest and current locked version of one artifact type (§6 project state).</summary>
public sealed record ArtifactStateDto(
    ArtifactType Type,
    int? LatestVersion,
    ArtifactStatus? LatestStatus,
    int? CurrentLockedVersion);

/// <summary>Step counts of the current plan version.</summary>
public sealed record ExecutionCountsDto(int Total, int Pending, int InProgress, int Completed, int Rejected, int Blocked);

/// <summary>Review cycle progress and the latest compliance result (FR-38). <c>maxCycles</c> includes extra cycles granted by escalations (FR-14 continue). <c>status</c> is the review job's state: <c>running</c> while a review is queued or running, then <c>completed</c> or <c>failed</c>; <c>idle</c> before the first cycle or after a rollback cleared the review state (§4.8).</summary>
public sealed record ReviewStateDto(
    int Cycle,
    int MaxCycles,
    decimal? LatestScore,
    ReviewDecision? LatestDecision,
    DriftLevel? DriftLevel,
    ReviewRunStatus Status);

/// <summary>A pending approval gate.</summary>
public sealed record GateSummaryDto(Guid Id, GateType Type, DateTimeOffset CreatedAt, bool Overdue);

/// <summary>
/// Deterministic project state for the overview page and the CLI (§6 <c>/api/projects/:id/state</c>). <c>tokensUsed</c>
/// counts every agent run's tokens; a <c>null</c> <c>tokenBudget</c> means unlimited (FR-26). <c>executionStatus</c> is the
/// execution runner's state over the current plan (FR-31, FR-37). <c>pullRequest</c> is the project PR once AIDE has opened
/// it (FR-36), else <c>null</c>. <c>latestRelease</c> is the latest release attempt of the current release version (FR-42,
/// FR-43), else <c>null</c>. Artifacts, steps, review and gates are those of the current release version
/// <c>releaseVersionId</c> (the version in development, else the latest released; §4.10). <c>checkpoint</c> was
/// <c>projectVersion</c> (D-V2-11). <c>suggestedNextVersion</c> prefills "Start new version" (FR-60: the next minor of the
/// released version). <c>latestDeployment</c> is the project's newest deployment of any target (FR-90, FR-91; <c>null</c>:
/// none), for the overview and <c>aide status</c>.
/// </summary>
public sealed record ProjectStateDto(
    Guid ProjectId,
    Phase Phase,
    ProjectStatus Status,
    int Checkpoint,
    Guid? ReleaseVersionId,
    string? ReleasedVersion,
    string? DevelopmentVersion,
    string SuggestedNextVersion,
    bool ExecutionPaused,
    IReadOnlyList<ArtifactStateDto> Artifacts,
    ExecutionCountsDto Execution,
    ReviewStateDto Review,
    IReadOnlyList<GateSummaryDto> PendingGates,
    long TokensUsed,
    long? TokenBudget,
    ExecutionRunStatus ExecutionStatus,
    PullRequestSummaryDto? PullRequest,
    ReleaseSummaryDto? LatestRelease,
    Deployments.DeploymentDto? LatestDeployment);

/// <summary>A project member (FR-8).</summary>
public sealed record MemberDto(Guid UserId, string Email, string Name, string? AvatarUrl, ProjectRole Role, DateTimeOffset CreatedAt);

/// <summary>Adds an existing user by email (FR-8).</summary>
public sealed record AddMemberRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(ProjectLimits.EmailMaxLength)]
    public required string Email { get; init; }

    public required ProjectRole Role { get; init; }
}

public sealed record UpdateMemberRoleRequest
{
    public required ProjectRole Role { get; init; }
}

/// <summary>
/// Git settings (FR-9, FR-35, FR-82, FR-83). The token is never returned, only <c>hasToken</c>. <c>branchName</c> is the
/// project branch, once the working copy has been prepared. <c>mode</c> is the v2 name of <c>provider</c> (always equal).
/// For <c>github_app</c>, <c>installationId</c>, <c>repoId</c> and <c>repoFullName</c> identify the repo (else
/// <c>null</c>). <c>appInstalledOnRepo</c> says whether the AIDE GitHub App can access a <c>github_pat</c> project's repo
/// (<c>null</c>: not checked, no App, or not a PAT project); <c>canSwitchToApp</c> is true when it can and the caller is an
/// Owner ("Switch to GitHub App"). <c>mergeStrategy</c> (FR-65: <c>aide_merges</c>, the default, or <c>manual</c>) and
/// <c>tagPrefix</c> (§4.10, FR-64: default <c>v</c>, so tags read <c>v1.2.0</c>) are the project's release settings.
/// </summary>
public sealed record GitSettingsDto(
    GitProvider Provider,
    string? RepoUrl,
    string DefaultBranch,
    string? BranchName,
    bool HasToken,
    GitProvider Mode,
    long? InstallationId,
    long? RepoId,
    string? RepoFullName,
    bool? AppInstalledOnRepo,
    bool CanSwitchToApp,
    MergeStrategy MergeStrategy,
    string TagPrefix);

/// <summary>Model id per agent; an omitted or <c>null</c> agent uses the default model.</summary>
public sealed record AgentModelsDto
{
    [MaxLength(ProjectLimits.ModelMaxLength)]
    public string? IdeaRefinement { get; init; }

    [MaxLength(ProjectLimits.ModelMaxLength)]
    public string? SpecGenerator { get; init; }

    [MaxLength(ProjectLimits.ModelMaxLength)]
    public string? PlanGenerator { get; init; }

    [MaxLength(ProjectLimits.ModelMaxLength)]
    public string? CodeGenerator { get; init; }

    [MaxLength(ProjectLimits.ModelMaxLength)]
    public string? CodeReview { get; init; }
}

/// <summary>Whether a provider can be used: an allowed system key is configured or the project has a bound connection for it (Ollama and Fake are always configured).</summary>
public sealed record LlmProviderStatusDto(LlmProvider Provider, bool Configured, string DefaultModel);

/// <summary>The Owner who bound a project's LLM connection (FR-79).</summary>
public sealed record LlmBindingUserDto(Guid Id, string Name);

/// <summary>
/// The connection bound to a project (FR-79), shown to every member as e.g. "Anthropic · connected by Ana (••••a1b2)".
/// Never contains the key. <c>credentialId</c> only lets the binding Owner recognise their own connection; nobody else can
/// bind it (<c>CREDENTIAL_NOT_OWNER</c>).
/// </summary>
public sealed record LlmBindingDto(
    Guid CredentialId,
    LlmProvider Provider,
    string Label,
    string Last4,
    LlmCredentialStatus Status,
    LlmBindingUserDto BoundBy);

/// <summary>
/// LLM settings (FR-9, FR-22, FR-79). Keys are never returned. <c>hasApiKey</c> is true while a connection is bound
/// (kept from v1); <c>binding</c> describes it (<c>null</c> when nothing is bound). <c>canBind</c> is true when the caller
/// is a project Owner and so may bind one of their own connections or unbind. <c>providers</c> lists every provider with
/// whether it's usable and its system default model.
/// </summary>
public sealed record LlmSettingsDto(
    LlmProvider Provider,
    string DefaultModel,
    AgentModelsDto ModelsByAgent,
    bool HasApiKey,
    IReadOnlyList<LlmProviderStatusDto> Providers,
    LlmBindingDto? Binding,
    bool CanBind);

/// <summary>
/// A notification channel (FR-47). Secrets are never returned, only <c>hasSecret</c>. For <c>email</c> and
/// <c>webhook</c>, <c>target</c> is the saved address or URL and the secret is a webhook's HMAC signing key. For
/// <c>slack</c> and <c>teams</c>, the incoming-webhook URL is itself the secret (a bearer credential): it is stored
/// encrypted, <c>target</c> is only a masked display value (host only, e.g. <c>hooks.slack.com/…</c>) and
/// <c>hasSecret</c> is <c>true</c> once a URL is saved.
/// </summary>
public sealed record NotificationChannelDto(
    Guid Id,
    NotificationChannelType Type,
    string Target,
    bool HasSecret,
    IReadOnlyList<NotificationEvent> Events);

public sealed record NotificationSettingsDto(IReadOnlyList<NotificationChannelDto> Channels);

/// <summary>All project settings with secrets as set / not set flags (FR-9).</summary>
public sealed record ProjectSettingsDto(GitSettingsDto Git, LlmSettingsDto Llm, NotificationSettingsDto Notifications);

/// <summary>Partial update of the Git settings. Omitted fields are unchanged.</summary>
public sealed record UpdateGitSettingsRequest
{
    /// <summary><c>github_pat</c> or <c>local</c>. Moving onto <c>github_app</c> goes through
    /// <c>POST /settings/git/switch-to-app</c> (400 <c>VALIDATION</c> here).</summary>
    public GitProvider? Provider { get; init; }

    /// <summary>Required when the provider is (or becomes) <c>github_pat</c>.</summary>
    [MaxLength(ProjectLimits.UrlMaxLength)]
    public string? RepoUrl { get; init; }

    [StringLength(ProjectLimits.BranchMaxLength, MinimumLength = 1)]
    public string? DefaultBranch { get; init; }

    /// <summary>Sets the access token. To remove it, send <c>clearToken: true</c>.</summary>
    [MaxLength(ProjectLimits.SecretMaxLength)]
    public string? Token { get; init; }

    public bool ClearToken { get; init; }

    /// <summary>FR-65: <c>aide_merges</c> (AIDE merges the PR on release) or <c>manual</c> (AIDE never merges; the release
    /// marks the PR ready for review and waits for the merge on GitHub). Applies to the next release attempt.</summary>
    public MergeStrategy? MergeStrategy { get; init; }

    /// <summary>§4.10, FR-64: the tag prefix (<c>^[A-Za-z0-9._/-]{0,20}$</c>, not starting with <c>.</c>, <c>/</c> or
    /// <c>-</c>; empty for bare <c>1.2.0</c> tags; else 400 <c>VALIDATION</c>). It also applies to the version in
    /// development. 409 <c>CONFLICT</c> while that version is <c>awaiting_merge</c> or a release attempt is in progress.</summary>
    [MaxLength(ReleaseVersionRules.TagPrefixMaxLength)]
    public string? TagPrefix { get; init; }
}

/// <summary>
/// Partial update of the LLM settings (Owner). Omitted fields are unchanged; <c>modelsByAgent</c>, when sent, replaces the
/// map. FR-79: <c>credentialId</c> binds one of the caller's own connections for the (new) provider; <c>null</c> unbinds.
/// Changing the provider without sending <c>credentialId</c> drops a binding of the old provider. v1's per-project
/// <c>apiKey</c>/<c>clearApiKey</c> are gone: keys are added under Account → Connections (FR-78).
/// </summary>
public sealed record UpdateLlmSettingsRequest
{
    private readonly Guid? _credentialId;

    public LlmProvider? Provider { get; init; }

    [StringLength(ProjectLimits.ModelMaxLength, MinimumLength = 1)]
    public string? DefaultModel { get; init; }

    public AgentModelsDto? ModelsByAgent { get; init; }

    /// <summary>The caller's connection to bind, or <c>null</c> to unbind. Omit it to keep the binding.</summary>
    public Guid? CredentialId
    {
        get => _credentialId;
        init
        {
            _credentialId = value;
            CredentialIdSent = true;
        }
    }

    /// <summary>Whether <c>credentialId</c> was in the request (JSON setters run only for sent properties).</summary>
    [JsonIgnore]
    public bool CredentialIdSent { get; private init; }
}

/// <summary>A channel in a notification settings update.</summary>
public sealed record NotificationChannelInput
{
    /// <summary>The id of an existing channel to keep (its secret, or Slack/Teams URL, is kept unless a new one or <c>clearSecret</c> is sent, or the type changes); omit for a new channel.</summary>
    public Guid? Id { get; init; }

    public required NotificationChannelType Type { get; init; }

    /// <summary>
    /// Email address (email) or https URL (webhook); required for both. For slack and teams the URL is sent in
    /// <c>secret</c> and <c>target</c> may be omitted or echo the masked value; an absolute URL sent here is still
    /// accepted as the incoming-webhook URL (older clients) and treated as a secret.
    /// </summary>
    [MaxLength(ProjectLimits.UrlMaxLength)]
    public string? Target { get; init; }

    /// <summary>
    /// The channel secret, stored encrypted and never returned: the HMAC signing key (webhook) or the https
    /// incoming-webhook URL (slack, teams). Required for a new webhook, slack or teams channel; omit it to keep the saved
    /// one of the same channel (same id and type). Webhook, slack and teams channels can't clear it without sending a new
    /// one. Email channels have none.
    /// </summary>
    [MaxLength(ProjectLimits.UrlMaxLength)]
    public string? Secret { get; init; }

    public bool ClearSecret { get; init; }

    [MaxLength(ProjectLimits.MaxNotificationEvents)]
    public required IReadOnlyList<NotificationEvent> Events { get; init; }
}

/// <summary>Replaces the project's notification channels (FR-47).</summary>
public sealed record UpdateNotificationSettingsRequest
{
    [MaxLength(ProjectLimits.MaxNotificationChannels)]
    public required IReadOnlyList<NotificationChannelInput> Channels { get; init; }
}

/// <summary>Result of testing the saved Git settings (FR-10); <c>reason</c> says why when <c>ok</c> is false.</summary>
public sealed record GitTestResultDto(bool Ok, bool Reachable, bool Writable, bool DefaultBranchExists, string? Reason);

/// <summary>Request limits of the projects feature (FR-5, FR-7, FR-9).</summary>
public static class ProjectLimits
{
    public const int NameMaxLength = 100;
    public const int IdeaMaxLength = 10_000;
    public const double MinComplianceThreshold = 0.50;
    public const double MaxComplianceThreshold = 1.00;
    public const int MinFilesPerStep = 1;
    public const int MaxFilesPerStep = 100;
    public const int MinReviewCycles = 1;
    public const int MaxReviewCycles = 10;
    public const long MinTokenBudget = 1000;

    /// <summary>The largest integer a JavaScript client can represent exactly (<c>Number.MAX_SAFE_INTEGER</c>).</summary>
    public const long MaxTokenBudget = 9_007_199_254_740_991;
    public const int UrlMaxLength = 2048;
    public const int BranchMaxLength = 255;

    /// <summary><c>owner/name</c>: a 39-character login, a slash and a 100-character name.</summary>
    public const int RepoFullNameMaxLength = 140;
    public const int SecretMaxLength = 1024;
    public const int ModelMaxLength = 200;
    public const int EmailMaxLength = 256;
    public const int MaxNotificationChannels = 20;
    public const int MaxNotificationEvents = 14;
    public const int MaxPageLimit = 100;
    public const int SearchMaxLength = 200;
}
