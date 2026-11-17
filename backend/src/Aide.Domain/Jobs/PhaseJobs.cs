using Aide.Domain.Lifecycle;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Jobs;

/// <summary>§4.9: the SDLC job each phase entry queues (the job types are <see cref="SdlcJobTypes"/>, FR-272).</summary>
public static class PhaseJobs
{
    /// <summary>
    /// The follow-up job written in the same transaction as a forward transition into <paramref name="entered"/>:
    /// spec generation on entering specification (FR-28), plan generation on entering planning (FR-29), a review
    /// cycle on entering review (FR-38) and the release job on entering release (FR-42). Refinement is started by
    /// the user (FR-27) and execution by <c>POST /execution/start</c> (FR-35, FR-37), so they have none.
    /// </summary>
    public static string? OnEnter(Phase entered) => entered switch
    {
        Phase.Specification => SdlcJobTypes.SpecGeneration,
        Phase.Planning => SdlcJobTypes.PlanGeneration,
        Phase.Review => SdlcJobTypes.Review,
        Phase.Release => SdlcJobTypes.Release,
        _ => null,
    };
}
