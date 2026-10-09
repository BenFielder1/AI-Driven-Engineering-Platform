using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Execution;

/// <summary>The facts that decide whether the project PR may be (re-)requested (FR-36).</summary>
/// <param name="Phase">The project's current phase.</param>
/// <param name="HasPlan">The project has a current plan.</param>
/// <param name="AllStepsCompleted">Every step of the current plan is completed (and there is at least one).</param>
/// <param name="RecordedForPlan">A PR for the current plan is recorded (and not forgotten since).</param>
/// <param name="JobPending">A pull request job is queued or running.</param>
public sealed record PullRequestFacts(Phase Phase, bool HasPlan, bool AllStepsCompleted, bool RecordedForPlan, bool JobPending);

/// <summary>
/// FR-36: when the pull request job may be queued again — by a writer after it failed or dead-lettered, or automatically
/// when the Git settings change. Only in execution with every step of the current plan completed, or in review, while no
/// PR is recorded for the current plan and no pull request job is queued or running.
/// </summary>
public static class PullRequestRules
{
    public static Result CanRequest(PullRequestFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Phase is not (Phase.Execution or Phase.Review))
        {
            return Precondition("The pull request can only be opened in execution or review.");
        }

        if (!facts.HasPlan)
        {
            return Precondition("The project has no current plan.");
        }

        if (!facts.AllStepsCompleted)
        {
            return Precondition("The pull request is opened once every step of the current plan is completed.");
        }

        if (facts.RecordedForPlan)
        {
            return Precondition("The pull request for the current plan is already open.");
        }

        if (facts.JobPending)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "Opening the pull request is already queued or running."));
        }

        return Result.Success();
    }

    private static Result Precondition(string message) =>
        Result.Failure(new DomainError(ErrorCodes.PhasePreconditionFailed, message) { Details = [message] });
}
