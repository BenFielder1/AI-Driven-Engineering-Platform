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

    /// <summary>FR-74: the preset doesn't exist (until presets land, every preset id).</summary>
    public const string PresetNotFound = "PRESET_NOT_FOUND";

    /// <summary>FR-42, FR-43 (recorded on a failed release): Git or GitHub refused the merge, tag or push for good.</summary>
    public const string GitError = "GIT_ERROR";

    /// <summary>FR-44 (recorded on a release): a rollback cancelled the release before it ran.</summary>
    public const string ReleaseCancelled = "RELEASE_CANCELLED";

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

    /// <summary>FR-27: an agent's structured (JSON) output could not be read, even after its corrective re-run.</summary>
    public const string InvalidOutput = "INVALID_OUTPUT";

    /// <summary>FR-22: the run's provider needs an API key and neither the project nor the system has one; never called.</summary>
    public const string ProviderNotConfigured = "PROVIDER_NOT_CONFIGURED";

    /// <summary>FR-25: the provider failed (status, timeout or unreadable response); transient ones are retried first.</summary>
    public const string ProviderError = "PROVIDER_ERROR";

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
        [PresetNotFound] = 400,
        [UpstreamError] = 502,
        [LinkExpired] = 410,
        [LinkUsed] = 409,
    };

    /// <summary>Every code that an endpoint can return, with its HTTP status (SPEC §6).</summary>
    public static IReadOnlyDictionary<string, int> HttpStatusCodes => HttpStatus;

    /// <summary>The HTTP status for an endpoint error code; <c>null</c> for codes that are only recorded (runs, Guardian).</summary>
    public static int? StatusCodeFor(string code) => HttpStatus.TryGetValue(code, out var status) ? status : null;
}
