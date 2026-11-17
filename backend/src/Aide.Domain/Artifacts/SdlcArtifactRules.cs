using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Artifacts;

/// <summary>SDLC artifact rules per type (§4.3, FR-16–FR-18, FR-20, FR-70, FR-183): which agent produces a type, in which
/// phase, and when it can be drafted. The rules that name no type are the engine's <see cref="ArtifactRules"/>.</summary>
public static class SdlcArtifactRules
{
    /// <summary>The types that go through drafts and approval (0.1.0 §4.3); <c>codebase_summary</c> (auto-locked, FR-70) is not one.</summary>
    public static IReadOnlyList<string> All { get; } =
        [SdlcArtifacts.ProjectRequest, SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan];

    /// <summary>
    /// The agent that produces a type (re-run on rejection or corruption). <c>codebase_summary</c> is written by the
    /// Repository Analyst (FR-70), which runs as the <c>repository_analysis</c> job rather than an agent
    /// (<see cref="SdlcAgents"/>) re-run, so it throws here (see <see cref="RerunAgent"/>).
    /// </summary>
    public static string ProducingAgent(string type) => type switch
    {
        SdlcArtifacts.ProjectRequest => SdlcAgents.IdeaRefinement,
        SdlcArtifacts.TechnicalSpecification => SdlcAgents.SpecGenerator,
        SdlcArtifacts.ImplementationPlan => SdlcAgents.PlanGenerator,
        SdlcArtifacts.OptimizationPlan => SdlcAgents.CodeReview,

        // FR-180: the Stack Analyst (re-run on rejection, FR-183, and on corruption, FR-20).
        SdlcArtifacts.StackProfile => SdlcAgents.StackAnalyst,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>The phase in which a type is produced.</summary>
    public static Phase ProducingPhase(string type) => type switch
    {
        SdlcArtifacts.ProjectRequest => Phase.Refinement,
        SdlcArtifacts.TechnicalSpecification => Phase.Specification,
        SdlcArtifacts.ImplementationPlan => Phase.Planning,
        SdlcArtifacts.OptimizationPlan => Phase.Review,

        // §4.12: produced automatically at the start of refinement.
        SdlcArtifacts.CodebaseSummary => Phase.Refinement,

        // FR-170: imported at the start of the work, while the project is in refinement (D-V5-10).
        SdlcArtifacts.SourceItems => Phase.Refinement,

        // FR-179, §4.27: produced and approved in analysis, the first phase of a version that infers its stack.
        SdlcArtifacts.StackProfile => Phase.Analysis,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// The agent re-run when a version of <paramref name="type"/> is rejected or found corrupted, or <c>null</c> when none
    /// is: <c>codebase_summary</c> has no <see cref="SdlcAgents"/> (its analyst is re-queued as a job, FR-70).
    /// </summary>
    public static string? RerunAgent(string type) =>
        CodebaseSummaryRules.IsAutoLocked(type) ? null : ProducingAgent(type);

    /// <summary>
    /// The status a new agent-authored version is stored with: <c>locked</c> for an auto-locked type (FR-70
    /// <c>codebase_summary</c>, no approval gate), <c>draft</c> otherwise.
    /// </summary>
    public static ArtifactStatus InitialStatus(string type) =>
        CodebaseSummaryRules.IsAutoLocked(type) ? ArtifactStatus.Locked : ArtifactStatus.Draft;

    /// <summary>
    /// FR-17: reject a draft → rejected. The producing agent re-runs with the comment unless the draft
    /// was a human edit (FR-18). FR-183 (amends FR-17 for <c>stack_profile</c>): the Stack Analyst always re-runs, a human
    /// edit included; it gets the rejected draft, so it can keep the user's corrections that agree with the comment.
    /// </summary>
    public static Result<ArtifactStatusChange> Reject(string type, ArtifactStatus status, bool authoredByAgent) => status switch
    {
        ArtifactStatus.Draft => Result.Success(new ArtifactStatusChange(
            ArtifactStatus.Rejected,
            Changed: true,
            authoredByAgent || type == SdlcArtifacts.StackProfile ? RerunAgent(type) : null)),
        ArtifactStatus.Locked => Result.Failure<ArtifactStatusChange>(ArtifactRules.Locked()),
        _ => ArtifactRules.NotDraft(status),
    };

    /// <summary>
    /// FR-20: a hash mismatch marks the artifact corrupted (and raises the alert). Its producing agent re-runs only when
    /// the row is the current version of its type (<paramref name="isCurrentVersion"/>, see <see cref="ArtifactRules.IsCurrentVersion"/>)
    /// and agent-authored: regenerating history (a superseded, rejected, invalidated or older locked version) would
    /// replace newer work, and a human-authored artifact (FR-18) has no agent to regenerate it.
    /// </summary>
    public static ArtifactStatusChange OnIntegrityCheck(
        string type, ArtifactStatus status, string content, string storedHash, bool authoredByAgent = true, bool isCurrentVersion = true) =>
        ContentHash.Matches(content, storedHash) || status == ArtifactStatus.Corrupted
            ? new ArtifactStatusChange(status, Changed: false)
            : new ArtifactStatusChange(ArtifactStatus.Corrupted, Changed: true, authoredByAgent && isCurrentVersion ? RerunAgent(type) : null);

    /// <summary>
    /// FR-18: can a new draft of <paramref name="type"/> be saved now? A type can be edited from the phase that
    /// produces it onwards (a later edit is approved like any version and cascades per §4.4); the optimisation plan
    /// only during review (it belongs to a review cycle); and nothing while a release is in progress
    /// (<see cref="ProjectStatus.Releasing"/>, or <see cref="ProjectStatus.AwaitingMerge"/>, FR-65). Otherwise <c>WRONG_PHASE</c>. The auto-locked <c>codebase_summary</c> is
    /// never drafted: <c>ARTIFACT_LOCKED</c> (FR-70).
    /// </summary>
    public static Result CanCreateDraft(string type, Phase phase, ProjectStatus status)
    {
        if (CodebaseSummaryRules.IsAutoLocked(type))
        {
            // FR-70, FR-170: never a draft; only the Repository Analyst or an import writes new (locked) versions.
            return Result.Failure(new DomainError(
                ErrorCodes.ArtifactLocked,
                type == SdlcArtifacts.SourceItems
                    ? "The source items are a locked snapshot of imports; re-import or remove items instead."
                    : "The codebase summary is written and locked by the Repository Analyst; it can't be edited."));
        }

        var producing = ProducingPhase(type);
        if (status is ProjectStatus.Releasing or ProjectStatus.AwaitingMerge)
        {
            return WrongPhase($"A release is in progress; {type} can't be edited until it finishes.");
        }

        if (type == SdlcArtifacts.OptimizationPlan && phase != Phase.Review)
        {
            return WrongPhase($"The optimization plan can only be edited during review, not in {phase.ToWire()}.");
        }

        // FR-183, §4.27: the profile belongs to analysis; once approved, Settings → Stack changes the pinned composition (FR-117).
        if (type == SdlcArtifacts.StackProfile && phase != Phase.Analysis)
        {
            return WrongPhase($"The stack profile can only be edited during analysis, not in {phase.ToWire()}. Change the stack in Settings → Stack instead.");
        }

        return phase.IsBefore(producing)
            ? WrongPhase($"The {type} is produced in {producing.ToWire()}; the project is still in {phase.ToWire()}.")
            : Result.Success();
    }

    private static Result WrongPhase(string message) => Result.Failure(new DomainError(ErrorCodes.WrongPhase, message));
}
