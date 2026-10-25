namespace Aide.Domain.Common;

/// <summary>
/// Every <c>errorCode</c> from SPEC.md §6 (and the FRs), in one place so the backend, the spec and the
/// frontend's copy table stay aligned. Guardian violation codes (§4.6) are listed separately: they are
/// recorded on steps, not returned as HTTP errors.
/// </summary>
public static class ErrorCodes
{
    // Common (every route)
    public const string Validation = "VALIDATION";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";

    /// <summary>A cookie-authenticated mutation without a valid <c>X-XSRF-TOKEN</c> header (§8 antiforgery).</summary>
    public const string Antiforgery = "ANTIFORGERY";

    // Accounts and members
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string GitHubAuthFailed = "GITHUB_AUTH_FAILED";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string LastOwner = "LAST_OWNER";

    // Lifecycle, gates, artifacts
    public const string PhasePreconditionFailed = "PHASE_PRECONDITION_FAILED";
    public const string GatePending = "GATE_PENDING";
    public const string BudgetExceeded = "BUDGET_EXCEEDED";
    public const string WrongPhase = "WRONG_PHASE";
    public const string ArtifactNotDraft = "ARTIFACT_NOT_DRAFT";
    public const string ArtifactLocked = "ARTIFACT_LOCKED";
    public const string GateNotPending = "GATE_NOT_PENDING";
    public const string InvalidDecision = "INVALID_DECISION";

    /// <summary>
    /// FR-58: an artifact version can't be restored now; the ProblemDetails <c>reason</c> says why
    /// (<see cref="RestoreReasons"/>).
    /// </summary>
    public const string RestoreNotAllowed = "RESTORE_NOT_ALLOWED";

    /// <summary>FR-27: a refinement round is already queued or running for the project.</summary>
    public const string RefinementInProgress = "REFINEMENT_IN_PROGRESS";

    // Review, release, rollback
    public const string ReviewInProgress = "REVIEW_IN_PROGRESS";
    public const string NotReleasing = "NOT_RELEASING";
    public const string ReleaseInProgress = "RELEASE_IN_PROGRESS";
    public const string MergeConflict = "MERGE_CONFLICT";
    public const string InvalidTargetVersion = "INVALID_TARGET_VERSION";
    public const string RollbackBlocked = "ROLLBACK_BLOCKED";
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>
    /// FR-61 (409 on <c>POST /versions</c>) and FR-64 (recorded on a failed release): the tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c>
    /// already exists in the repository on a commit that doesn't hold the release; tags are never moved (replaces v1's
    /// <c>TAG_CONFLICT</c> and the <c>v&lt;N&gt;-aide.&lt;k&gt;</c> suffixes).
    /// </summary>
    public const string TagExists = "TAG_EXISTS";

    // Release versions (§4.10, FR-60–FR-66)

    /// <summary>FR-61: not <c>MAJOR.MINOR.PATCH[-prerelease]</c>.</summary>
    public const string VersionInvalid = "VERSION_INVALID";

    /// <summary>FR-61: not strictly greater than the released version (or the version already exists).</summary>
    public const string VersionNotGreater = "VERSION_NOT_GREATER";

    /// <summary>FR-61: a version is already in development.</summary>
    public const string VersionInProgress = "VERSION_IN_PROGRESS";

    /// <summary>FR-60: the latest release is waiting for its pull request to be merged (FR-65).</summary>
    public const string ReleaseNotMerged = "RELEASE_NOT_MERGED";

    /// <summary>FR-60: a new version needs the project to be released.</summary>
    public const string NotReleased = "NOT_RELEASED";

    /// <summary>An external service (the Git host) failed or couldn't be reached while serving the request (§6).</summary>
    public const string UpstreamError = "UPSTREAM_ERROR";

    /// <summary>
    /// FR-78: the LLM provider rejected the key (HTTP 401/403): returned (400) when a connection is saved, and recorded on a
    /// run that failed because the bound key was rejected (the connection is then marked <c>invalid</c>).
    /// </summary>
    public const string CredentialInvalid = "CREDENTIAL_INVALID";

    /// <summary>FR-79: an Owner tried to bind a connection that belongs to another user.</summary>
    public const string CredentialNotOwner = "CREDENTIAL_NOT_OWNER";

    // GitHub App and connections (FR-80–FR-86, §4.14)

    /// <summary>FR-81: the caller has no (valid) GitHub connection; connect under Account → Connections → GitHub.</summary>
    public const string GitHubNotConnected = "GITHUB_NOT_CONNECTED";

    /// <summary>
    /// FR-81, FR-82, FR-83: the AIDE GitHub App isn't installed on the target owner or repository. The ProblemDetails
    /// carries an <c>installUrl</c> extension (the App's install link) when an App is registered.
    /// </summary>
    public const string GitHubAppNotInstalled = "GITHUB_APP_NOT_INSTALLED";

    /// <summary>FR-81: a repository with that name already exists for the owner.</summary>
    public const string RepoNameTaken = "REPO_NAME_TAKEN";

    /// <summary>
    /// §4.14: GitHub's rate limit for the token in use is exhausted. Returned as 429 with <c>Retry-After</c> (seconds until
    /// GitHub's reset); also recorded on a background job that gave up waiting.
    /// </summary>
    public const string GitHubRateLimited = "GITHUB_RATE_LIMITED";

    /// <summary>
    /// FR-73 (400 on <c>POST /api/projects</c> with <c>source: import</c>): the "currently released version" tag the wizard
    /// sent doesn't exist in the repository (checked against its tags, case-sensitively).
    /// </summary>
    public const string TagNotFound = "TAG_NOT_FOUND";

    /// <summary>FR-74: the preset doesn't exist (until presets land, every preset id).</summary>
    public const string PresetNotFound = "PRESET_NOT_FOUND";

    /// <summary>
    /// FR-74 (409 on <c>PATCH /api/projects/:id/preset</c>): the version's first implementation plan is locked (or nothing
    /// is in development), so its preset can't change; a new version can pick another one.
    /// </summary>
    public const string PresetLocked = "PRESET_LOCKED";

    // Vercel deployments (FR-87–FR-91, §4.15)

    /// <summary>
    /// FR-88 (409 on <c>PATCH /settings/deployment</c> and <c>POST /api/projects</c>): Vercel's GitHub App can't access the
    /// project's repository. The ProblemDetails carries an <c>installUrl</c> extension (Vercel's install link) when known.
    /// </summary>
    public const string VercelGitNotConnected = "VERCEL_GIT_NOT_CONNECTED";

    /// <summary>FR-88: the Vercel plan doesn't allow it (a Hobby account linking an organisation-owned repository).</summary>
    public const string VercelPlanLimit = "VERCEL_PLAN_LIMIT";

    /// <summary>FR-90 (409 on <c>POST /deployments/preview</c>): the project has no deployment settings (no provider bound).</summary>
    public const string DeploymentNotConfigured = "DEPLOYMENT_NOT_CONFIGURED";

    /// <summary>FR-91 (409 on promote): only a <c>ready</c> production deployment can be promoted.</summary>
    public const string InvalidDeployment = "INVALID_DEPLOYMENT";

    /// <summary>§4.15 (recorded on a deployment, not returned by an endpoint): still not finished after the 30-minute poll ceiling.</summary>
    public const string DeploymentTimeout = "DEPLOYMENT_TIMEOUT";

    /// <summary>FR-90, FR-91 (recorded on a deployment, not returned by an endpoint): Vercel reported the build or deployment
    /// as failed, or refused to create it; the message and build-log tail say why.</summary>
    public const string VercelError = "VERCEL_ERROR";

    /// <summary>FR-42, FR-43 (recorded on a failed release): Git or GitHub refused the merge, tag or push for good.</summary>
    public const string GitError = "GIT_ERROR";

    /// <summary>FR-44 (recorded on a release): a rollback cancelled the release before it ran.</summary>
    public const string ReleaseCancelled = "RELEASE_CANCELLED";

    /// <summary>
    /// FR-65, §4.14 (recorded on a release attempt, not returned by an endpoint): the PR stayed unmerged for the whole merge
    /// poll (14 days). The project stays in <c>release</c>; an Owner can retry with <c>POST /release</c>.
    /// </summary>
    public const string MergeTimeout = "MERGE_TIMEOUT";

    /// <summary>
    /// FR-65 (recorded on a release attempt, not returned by an endpoint): the PR was closed without being merged while the
    /// release was awaiting its merge. The project stays in <c>release</c>; an Owner can reopen it on GitHub and retry.
    /// </summary>
    public const string PrClosed = "PR_CLOSED";

    // Approval links
    public const string LinkExpired = "LINK_EXPIRED";
    public const string LinkUsed = "LINK_USED";

    // Agent runs (recorded on the run, not returned by an endpoint)
    public const string MissingInput = "MISSING_INPUT";

    /// <summary>
    /// FR-29: a plan breaks the plan rules. Recorded on a plan generator run whose corrective re-run is still invalid, and
    /// returned (400) when a human-edited plan draft (FR-18) or a plan being approved (FR-30) is invalid.
    /// </summary>
    public const string InvalidPlan = "INVALID_PLAN";

    /// <summary>FR-28: the spec generator's output still misses required sections after its corrective re-run.</summary>
    public const string MissingSections = "MISSING_SECTIONS";

    /// <summary>
    /// FR-75, D-V2-13: the spec generator's output has a functional requirement without an <c>FR-n</c> ID or without a
    /// Given/When/Then acceptance criterion (every offender in the details), still after its corrective re-run. Recorded
    /// on the run, like <see cref="MissingSections"/>.
    /// </summary>
    public const string MissingRequirementIds = "MISSING_REQUIREMENT_IDS";

    /// <summary>
    /// §4.13: an iteration spec's requirement tags are invalid (an <c>FR-n</c> without exactly one of <c>[new]</c>,
    /// <c>[changed]</c>, <c>[unchanged]</c>, <c>[removed]</c>; no <c>[new]</c>/<c>[changed]</c> at all; or a
    /// <c>[changed]</c>/<c>[removed]</c> ID that isn't in the baseline spec), still after the corrective re-run. Recorded on the run.
    /// </summary>
    public const string InvalidRequirementTags = "INVALID_REQUIREMENT_TAGS";

    /// <summary>
    /// FR-76, §4.13 (a <b>warning</b>, never blocking): an iteration plan's steps don't cover (<c>covers</c>) every
    /// <c>[new]</c>/<c>[changed]</c> requirement; the missing IDs are in the details.
    /// </summary>
    public const string UncoveredRequirements = "UNCOVERED_REQUIREMENTS";

    /// <summary>
    /// FR-76, §4.11 (a <b>warning</b>, never blocking): a plan step declares files outside its specialist agent's owned
    /// paths; each offending step and file is in the details.
    /// </summary>
    public const string AgentScope = "AGENT_SCOPE";

    /// <summary>
    /// §4.11, §4.12 (a <b>warning</b>, never blocking): a <i>soft</i> preset ordering rule is broken (e.g. a
    /// <c>lib/&lt;domain&gt;</c> step after an <c>app/**</c> step); hard ordering rules are <see cref="InvalidPlan"/>.
    /// </summary>
    public const string PlanOrder = "PLAN_ORDER";

    /// <summary>
    /// FR-89 (a <b>warning</b>, never blocking): the version's preset deploys to Vercel, the base commit has no
    /// <c>vercel.json</c> and no plan step declares one, so nothing would stop Vercel's automatic deploys of <c>aide/**</c>.
    /// </summary>
    public const string MissingVercelConfig = "MISSING_VERCEL_CONFIG";

    /// <summary>
    /// FR-71: a repo tool call beyond the run's limits (25 calls, 400 KB read); returned to the agent as the tool result,
    /// which must then answer with what it has. Unsafe tool paths reuse <see cref="Guardian.UnsafePath"/> (<c>UNSAFE_PATH</c>).
    /// </summary>
    public const string ToolLimit = "TOOL_LIMIT";

    /// <summary>FR-27: an agent's structured (JSON) output could not be read, even after its corrective re-run.</summary>
    public const string InvalidOutput = "INVALID_OUTPUT";

    /// <summary>FR-22: the run's provider needs an API key and neither the project nor the system has one; never called.</summary>
    public const string ProviderNotConfigured = "PROVIDER_NOT_CONFIGURED";

    /// <summary>FR-25: the provider failed (status, timeout or unreadable response); transient ones are retried first.</summary>
    public const string ProviderError = "PROVIDER_ERROR";

    /// <summary>
    /// FR-157, FR-159: the LLM provider is disabled on this deployment (<c>Llm:Providers:&lt;P&gt;:Enabled=false</c>) or not
    /// usable by the caller (no allowed system key and no connection). Returned (400) on project create and
    /// <c>PATCH …/settings/llm</c>, and recorded as the failure code of an agent run whose project is bound to a disabled provider.
    /// </summary>
    public const string LlmProviderUnavailable = "LLM_PROVIDER_UNAVAILABLE";

    /// <summary>
    /// FR-116, §4.17: a composition breaks a compatibility rule (400); the ProblemDetails <c>violations</c> extension lists
    /// each <c>{layer, option, rule, reason}</c>.
    /// </summary>
    public const string StackIncompatible = "STACK_INCOMPATIBLE";

    /// <summary>Execution Guardian violation codes (§4.6, D18).</summary>
    public static class Guardian
    {
        public const string SingleStep = "SINGLE_STEP";
        public const string FileLimit = "FILE_LIMIT";
        public const string DependencyOrder = "DEPENDENCY_ORDER";
        public const string PlanAlignment = "PLAN_ALIGNMENT";
        public const string ScopeCreep = "SCOPE_CREEP";
        public const string ProtectedPath = "PROTECTED_PATH";
        public const string UnsafePath = "UNSAFE_PATH";
        public const string DuplicatePath = "DUPLICATE_PATH";
        public const string SecretDetected = "SECRET_DETECTED";

        public static IReadOnlyList<string> All { get; } =
            [SingleStep, FileLimit, DependencyOrder, PlanAlignment, ScopeCreep, ProtectedPath, UnsafePath, DuplicatePath, SecretDetected];
    }

    private static readonly Dictionary<string, int> HttpStatus = new(StringComparer.Ordinal)
    {
        [Validation] = 400,
        [Unauthenticated] = 401,
        [Forbidden] = 403,
        [NotFound] = 404,
        [Conflict] = 409,
        [Antiforgery] = 403,
        [EmailTaken] = 409,
        [InvalidCredentials] = 401,
        [GitHubAuthFailed] = 400,
        [UserNotFound] = 404,
        [LastOwner] = 409,
        [PhasePreconditionFailed] = 409,
        [GatePending] = 409,
        [BudgetExceeded] = 409,
        [WrongPhase] = 409,
        [ArtifactNotDraft] = 409,
        [ArtifactLocked] = 409,
        [GateNotPending] = 409,
        [InvalidDecision] = 400,
        [RestoreNotAllowed] = 409,
        [RefinementInProgress] = 409,
        [InvalidPlan] = 400,
        [ReviewInProgress] = 409,
        [NotReleasing] = 409,
        [ReleaseInProgress] = 409,
        [MergeConflict] = 409,
        [InvalidTargetVersion] = 400,
        [RollbackBlocked] = 409,
        [RateLimited] = 429,
        [TagExists] = 409,
        [VersionInvalid] = 400,
        [VersionNotGreater] = 409,
        [VersionInProgress] = 409,
        [ReleaseNotMerged] = 409,
        [NotReleased] = 409,
        [TagNotFound] = 400,
        [PresetNotFound] = 400,
        [PresetLocked] = 409,
        [CredentialInvalid] = 400,
        [CredentialNotOwner] = 403,
        [LlmProviderUnavailable] = 400,
        [StackIncompatible] = 400,
        [UpstreamError] = 502,
        [GitHubNotConnected] = 409,
        [GitHubAppNotInstalled] = 409,
        [RepoNameTaken] = 409,
        [GitHubRateLimited] = 429,
        [VercelGitNotConnected] = 409,
        [VercelPlanLimit] = 409,
        [DeploymentNotConfigured] = 409,
        [InvalidDeployment] = 409,
        [LinkExpired] = 410,
        [LinkUsed] = 409,
    };

    /// <summary>Every code that an endpoint can return, with its HTTP status (SPEC §6).</summary>
    public static IReadOnlyDictionary<string, int> HttpStatusCodes => HttpStatus;

    /// <summary>The HTTP status for an endpoint error code; <c>null</c> for codes that are only recorded (runs, Guardian).</summary>
    public static int? StatusCodeFor(string code) => HttpStatus.TryGetValue(code, out var status) ? status : null;
}
