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

    // Review, release, rollback
    public const string ReviewInProgress = "REVIEW_IN_PROGRESS";
    public const string NotReleasing = "NOT_RELEASING";
    public const string ReleaseInProgress = "RELEASE_IN_PROGRESS";
    public const string MergeConflict = "MERGE_CONFLICT";
    public const string InvalidTargetVersion = "INVALID_TARGET_VERSION";
    public const string RollbackBlocked = "ROLLBACK_BLOCKED";
    public const string RateLimited = "RATE_LIMITED";

    // Approval links
    public const string LinkExpired = "LINK_EXPIRED";
    public const string LinkUsed = "LINK_USED";

    // Agent runs (recorded on the run, not returned by an endpoint)
    public const string MissingInput = "MISSING_INPUT";
    public const string InvalidPlan = "INVALID_PLAN";

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
        [ReviewInProgress] = 409,
        [NotReleasing] = 409,
        [ReleaseInProgress] = 409,
        [MergeConflict] = 409,
        [InvalidTargetVersion] = 400,
        [RollbackBlocked] = 409,
        [RateLimited] = 429,
        [LinkExpired] = 410,
        [LinkUsed] = 409,
    };

    /// <summary>Every code that an endpoint can return, with its HTTP status (SPEC §6).</summary>
    public static IReadOnlyDictionary<string, int> HttpStatusCodes => HttpStatus;

    /// <summary>The HTTP status for an endpoint error code; <c>null</c> for codes that are only recorded (runs, Guardian).</summary>
    public static int? StatusCodeFor(string code) => HttpStatus.TryGetValue(code, out var status) ? status : null;
}
