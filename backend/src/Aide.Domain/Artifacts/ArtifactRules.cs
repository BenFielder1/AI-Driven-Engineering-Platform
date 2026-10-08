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
    public static IReadOnlyList<ArtifactType> All { get; } =
        [ArtifactType.ProjectRequest, ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan];

    /// <summary>The agent that produces a type (re-run on rejection or corruption).</summary>
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
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

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
    /// FR-17: reject a draft → rejected. The producing agent re-runs with the comment unless the draft
    /// was a human edit (FR-18).
    /// </summary>
    public static Result<ArtifactStatusChange> Reject(ArtifactType type, ArtifactStatus status, bool authoredByAgent) => status switch
    {
        ArtifactStatus.Draft => Result.Success(new ArtifactStatusChange(
            ArtifactStatus.Rejected,
            Changed: true,
            authoredByAgent ? ProducingAgent(type) : null)),
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

    /// <summary>FR-20: a hash mismatch marks the artifact corrupted and re-runs its producing agent.</summary>
    public static ArtifactStatusChange OnIntegrityCheck(ArtifactType type, ArtifactStatus status, string content, string storedHash) =>
        ContentHash.Matches(content, storedHash) || status == ArtifactStatus.Corrupted
            ? new ArtifactStatusChange(status, Changed: false)
            : new ArtifactStatusChange(ArtifactStatus.Corrupted, Changed: true, ProducingAgent(type));

    private static DomainError Locked() =>
        new(ErrorCodes.ArtifactLocked, "The artifact is locked; create a new version instead.");

    private static Result<ArtifactStatusChange> NotDraft(ArtifactStatus status) =>
        Result.Failure<ArtifactStatusChange>(new DomainError(ErrorCodes.ArtifactNotDraft, $"The artifact is {status.ToWire()}, not a draft."));
}
