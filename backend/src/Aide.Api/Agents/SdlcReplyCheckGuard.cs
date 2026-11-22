using Tevrin.Engine.Domain.Pipelines;

namespace Aide.Api.Agents;

/// <summary>
/// FR-314, D-0.7.0-44: an SDLC output guard (<see cref="Tevrin.Pack.Sdlc.SdlcGuardNames.ReplyChecks"/>), declared as the
/// <c>validate:</c> of its agent's stage or binding. It applies exactly the reply check the job gave that attempt
/// (<see cref="ArtifactAgentSpec.ToContent"/> of the attempt's <see cref="SdlcAgentStageCall"/>, carried as the
/// <see cref="StageAttemptFact"/>'s host state) to the reply as the provider sent it, with the error's message as its one
/// reason: the host check it replaces. The corrective re-run's spec is the job's corrective check
/// (<see cref="ArtifactAgentSpec.ForCorrective"/>), so the review's re-run only has to read and the build fix's re-run is
/// accepted as sent, as in 0.6.1. Without a stage output (a lifecycle evaluation) it passes: it is never an entry rule.
/// </summary>
public sealed class SdlcReplyCheckGuard : IGuard
{
    /// <summary>The reason when the output arrives without the SDLC request (not an SDLC agent attempt).</summary>
    public const string NoRequestReason = "The SDLC output check has no SDLC request to check the reply against.";

    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Facts.Get<StageOutputFact>() is null)
        {
            return Task.FromResult(GuardResult.Pass);
        }

        if (context.Facts.Get<StageAttemptFact>() is not { HostState: SdlcAgentStageCall call } attempt)
        {
            return Task.FromResult(GuardResult.Fail(NoRequestReason));
        }

        var error = call.Spec.ToContent(attempt.Reply).Error;
        return Task.FromResult(error is null ? GuardResult.Pass : GuardResult.Fail(error.Message));
    }
}
