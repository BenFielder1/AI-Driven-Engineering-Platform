using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Artifacts;

/// <summary>An existing version of one artifact type.</summary>
public sealed record ArtifactVersion(int Version, ArtifactStatus Status);

/// <summary>Numbering for a new draft, and the older drafts it supersedes (§4.3, FR-16, FR-18).</summary>
public sealed record NewDraftPlan(int Version, int? ParentVersion, IReadOnlyList<int> SupersededVersions);

/// <summary>A status change from approve/reject; <see cref="Changed"/> is false for an idempotent no-op.</summary>
public sealed record ArtifactStatusChange(ArtifactStatus NewStatus, bool Changed, AgentKind? RerunAgent = null);

/// <summary>Artifact lifecycle rules (§4.3, FR-16–FR-18, FR-20).</summary>
public static class ArtifactRules
{
    /// <summary>The types that go through drafts and approval (v1 §4.3); <c>codebase_summary</c> (auto-locked, FR-70) is not one.</summary>
    public static IReadOnlyList<ArtifactType> All { get; } =
        [ArtifactType.ProjectRequest, ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan];

    /// <summary>
    /// The agent that produces a type (re-run on rejection or corruption). <c>codebase_summary</c> is written by the
    /// Repository Analyst (FR-70), which runs as the <c>repository_analysis</c> job rather than an <see cref="AgentKind"/>
    /// re-run, so it throws here (see <see cref="RerunAgent"/>).
    /// </summary>
    public static AgentKind ProducingAgent(ArtifactType type) => type switch
    {
        ArtifactType.ProjectRequest => AgentKind.IdeaRefinement,
        ArtifactType.TechnicalSpecification => AgentKind.SpecGenerator,
        ArtifactType.ImplementationPlan => AgentKind.PlanGenerator,
        ArtifactType.OptimizationPlan => AgentKind.CodeReview,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>The phase in which a type is produced.</summary>
    public static Phase ProducingPhase(ArtifactType type) => type switch
    {
        ArtifactType.ProjectRequest => Phase.Refinement,
        ArtifactType.TechnicalSpecification => Phase.Specification,
        ArtifactType.ImplementationPlan => Phase.Planning,
        ArtifactType.OptimizationPlan => Phase.Review,

        // §4.12: produced automatically at the start of refinement.
        ArtifactType.CodebaseSummary => Phase.Refinement,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// The agent re-run when a version of <paramref name="type"/> is rejected or found corrupted, or <c>null</c> when none
    /// is: <c>codebase_summary</c> has no <see cref="AgentKind"/> (its analyst is re-queued as a job, FR-70).
    /// </summary>
    public static AgentKind? RerunAgent(ArtifactType type) =>
        CodebaseSummaryRules.IsAutoLocked(type) ? null : ProducingAgent(type);

    /// <summary>
    /// The status a new agent-authored version is stored with: <c>locked</c> for an auto-locked type (FR-70
    /// <c>codebase_summary</c>, no approval gate), <c>draft</c> otherwise.
    /// </summary>
    public static ArtifactStatus InitialStatus(ArtifactType type) =>
        CodebaseSummaryRules.IsAutoLocked(type) ? ArtifactStatus.Locked : ArtifactStatus.Draft;

    /// <summary>Allowed stored-status moves: draft → locked/rejected/superseded/invalidated, locked → invalidated, any → corrupted.</summary>
    public static bool CanTransition(ArtifactStatus from, ArtifactStatus to) => (from, to) switch
    {
        (_, ArtifactStatus.Corrupted) => from != ArtifactStatus.Corrupted,
        (ArtifactStatus.Draft, ArtifactStatus.Locked or ArtifactStatus.Rejected or ArtifactStatus.Superseded or ArtifactStatus.Invalidated) => true,
        (ArtifactStatus.Locked, ArtifactStatus.Invalidated) => true,
        _ => false,
    };

    /// <summary>The next version (highest + 1) with the highest existing version as parent; any open draft is superseded.</summary>
    public static NewDraftPlan NewDraft(IEnumerable<ArtifactVersion> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var list = existing.ToList();
        var latest = list.Count == 0 ? (int?)null : list.Max(a => a.Version);
        var superseded = list.Where(a => a.Status == ArtifactStatus.Draft).Select(a => a.Version).Order().ToList();
        return new NewDraftPlan((latest ?? 0) + 1, latest, superseded);
    }

    /// <summary>The "current" version: highest <c>locked</c>, or <c>null</c>.</summary>
    public static int? Current(IEnumerable<ArtifactVersion> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing.Where(a => a.Status == ArtifactStatus.Locked).Select(a => (int?)a.Version).Max();
    }

    /// <summary>FR-17: approve a draft → locked. Approving a locked artifact is a no-op.</summary>
    public static Result<ArtifactStatusChange> Approve(ArtifactStatus status) => status switch
    {
        ArtifactStatus.Draft => Result.Success(new ArtifactStatusChange(ArtifactStatus.Locked, Changed: true)),
        ArtifactStatus.Locked => Result.Success(new ArtifactStatusChange(ArtifactStatus.Locked, Changed: false)),
        _ => NotDraft(status),
    };

    /// <summary>
    /// FR-17, FR-19 (rollback): a draft created at a project version above the project's current version belongs to work
    /// a rollback discarded (rollback moves the project back to an earlier version and invalidates everything created
    /// after it); one that slipped in concurrently is refused with <c>ARTIFACT_NOT_DRAFT</c> instead of being locked.
    /// </summary>
    public static Result EnsureNotStale(int projectVersionAtCreation, int currentProjectVersion) =>
        projectVersionAtCreation > currentProjectVersion
            ? Result.Failure(new DomainError(
                ErrorCodes.ArtifactNotDraft,
                $"The draft was created at project version {projectVersionAtCreation}, after the project was rolled back to version {currentProjectVersion}; it can't be approved."))
            : Result.Success();

    /// <summary>
    /// FR-17: reject a draft → rejected. The producing agent re-runs with the comment unless the draft
    /// was a human edit (FR-18).
    /// </summary>
    public static Result<ArtifactStatusChange> Reject(ArtifactType type, ArtifactStatus status, bool authoredByAgent) => status switch
    {
        ArtifactStatus.Draft => Result.Success(new ArtifactStatusChange(
            ArtifactStatus.Rejected,
            Changed: true,
            authoredByAgent ? RerunAgent(type) : null)),
        ArtifactStatus.Locked => Result.Failure<ArtifactStatusChange>(Locked()),
        _ => NotDraft(status),
    };

    /// <summary>Content of a stored artifact never changes; a locked one reports <c>ARTIFACT_LOCKED</c>.</summary>
    public static Result EnsureEditable(ArtifactStatus status) => status switch
    {
        ArtifactStatus.Draft => Result.Success(),
        ArtifactStatus.Locked => Result.Failure(Locked()),
        _ => Result.Failure(new DomainError(ErrorCodes.ArtifactNotDraft, $"The artifact is {status.ToWire()}.")),
    };

    /// <summary>Cascade or rollback: drafts and locked versions become invalidated; others are left as history.</summary>
    public static bool ShouldInvalidate(ArtifactStatus status) => status is ArtifactStatus.Draft or ArtifactStatus.Locked;

    /// <summary>
    /// FR-20: a hash mismatch marks the artifact corrupted (and raises the alert). Its producing agent re-runs only when
    /// the row is the current version of its type (<paramref name="isCurrentVersion"/>, see <see cref="IsCurrentVersion"/>)
    /// and agent-authored: regenerating history (a superseded, rejected, invalidated or older locked version) would
    /// replace newer work, and a human-authored artifact (FR-18) has no agent to regenerate it.
    /// </summary>
    public static ArtifactStatusChange OnIntegrityCheck(
        ArtifactType type, ArtifactStatus status, string content, string storedHash, bool authoredByAgent = true, bool isCurrentVersion = true) =>
        ContentHash.Matches(content, storedHash) || status == ArtifactStatus.Corrupted
            ? new ArtifactStatusChange(status, Changed: false)
            : new ArtifactStatusChange(ArtifactStatus.Corrupted, Changed: true, authoredByAgent && isCurrentVersion ? RerunAgent(type) : null);

    /// <summary>
    /// FR-20: is <paramref name="candidate"/> (with its status before any corruption mark) the current version of its
    /// type — the open draft, or the highest locked version? <paramref name="others"/> are the type's other versions.
    /// </summary>
    public static bool IsCurrentVersion(ArtifactVersion candidate, IEnumerable<ArtifactVersion> others)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(others);
        return candidate.Status switch
        {
            ArtifactStatus.Draft => true,
            ArtifactStatus.Locked => others.All(o => o.Status != ArtifactStatus.Locked || o.Version < candidate.Version),
            _ => false,
        };
    }

    /// <summary>
    /// FR-18: an edit made from version <paramref name="baseVersion"/> may only be saved while that is still the latest
    /// version of the type; otherwise someone (or an agent) saved a newer version since, and saving would silently
    /// discard it — <c>CONFLICT</c> with <c>latestVersion: N</c> (or <c>none</c>) in the details. This also covers editing
    /// a locked version while a newer draft exists. No base version (CLI uploads) is always allowed.
    /// </summary>
    public static Result CheckBaseVersion(int? baseVersion, IEnumerable<ArtifactVersion> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (baseVersion is not { } requested)
        {
            return Result.Success();
        }

        var latest = existing.Select(a => (int?)a.Version).Max();
        if (latest == requested)
        {
            return Result.Success();
        }

        var latestText = latest?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        return Result.Failure(new DomainError(
            ErrorCodes.Conflict,
            $"This edit was based on version {requested}, but the latest version is {latestText}. Reload to see it, then edit again.")
        {
            Details = [$"latestVersion: {latestText}"],
        });
    }

    /// <summary>
    /// FR-20: the integrity reported on a read. <c>corrupted</c> when the stored content no longer matches its hash, or
    /// the row was already marked <see cref="ArtifactStatus.Corrupted"/> by an earlier read.
    /// </summary>
    public static ArtifactIntegrity IntegrityOf(ArtifactStatus status, string content, string storedHash) =>
        status == ArtifactStatus.Corrupted || !ContentHash.Matches(content, storedHash)
            ? ArtifactIntegrity.Corrupted
            : ArtifactIntegrity.Ok;

    /// <summary>
    /// FR-18: can a new draft of <paramref name="type"/> be saved now? A type can be edited from the phase that
    /// produces it onwards (a later edit is approved like any version and cascades per §4.4); the optimisation plan
    /// only during review (it belongs to a review cycle); and nothing while a release is in progress
    /// (<see cref="ProjectStatus.Releasing"/>). Otherwise <c>WRONG_PHASE</c>. The auto-locked <c>codebase_summary</c> is
    /// never drafted: <c>ARTIFACT_LOCKED</c> (FR-70).
    /// </summary>
    public static Result CanCreateDraft(ArtifactType type, Phase phase, ProjectStatus status)
    {
        if (CodebaseSummaryRules.IsAutoLocked(type))
        {
            // FR-70: never a draft; only the Repository Analyst writes new (locked) versions.
            return Result.Failure(new DomainError(
                ErrorCodes.ArtifactLocked,
                "The codebase summary is written and locked by the Repository Analyst; it can't be edited."));
        }

        var producing = ProducingPhase(type);
        if (status == ProjectStatus.Releasing)
        {
            return WrongPhase($"A release is in progress; {type.ToWire()} can't be edited until it finishes.");
        }

        if (type == ArtifactType.OptimizationPlan && phase != Phase.Review)
        {
            return WrongPhase($"The optimization plan can only be edited during review, not in {phase.ToWire()}.");
        }

        return phase.IsBefore(producing)
            ? WrongPhase($"The {type.ToWire()} is produced in {producing.ToWire()}; the project is still in {phase.ToWire()}.")
            : Result.Success();
    }

    /// <summary>The author of a version: an agent run unless a user created it (FR-16, FR-18).</summary>
    public static bool IsAuthoredByAgent(Guid? createdByUserId) => createdByUserId is null;

    private static Result WrongPhase(string message) => Result.Failure(new DomainError(ErrorCodes.WrongPhase, message));

    private static DomainError Locked() =>
        new(ErrorCodes.ArtifactLocked, "The artifact is locked; create a new version instead.");

    private static Result<ArtifactStatusChange> NotDraft(ArtifactStatus status) =>
        Result.Failure<ArtifactStatusChange>(new DomainError(ErrorCodes.ArtifactNotDraft, $"The artifact is {status.ToWire()}, not a draft."));
}
