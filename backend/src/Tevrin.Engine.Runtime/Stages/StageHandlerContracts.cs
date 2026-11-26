using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Stages;

/// <summary>
/// FR-280, FR-319, D-0.7.0-43: run the stage handler <see cref="Handler"/> (<c>&lt;pack&gt;.&lt;name&gt;</c>) of run
/// <see cref="RunId"/>, pinned to <see cref="DefinitionHash"/>. The handler runs on the stage of the pinned definition that
/// declares it (<c>handler:</c> or <c>handlers:</c>), with <see cref="Facts"/> from the host.
/// </summary>
public sealed record StageHandlerRequest(Guid RunId, Guid ProjectId, string DefinitionHash, string Handler)
{
    public FactBag Facts { get; init; } = FactBag.Empty;
}

/// <summary>The job a stage handler's work runs in (a fact the job layer adds), so the handler keeps its job's semantics.</summary>
public sealed record StageJobFact(JobExecution Job);

/// <summary>What <see cref="StageHandlerRunner"/> did with a <see cref="StageHandlerRequest"/>.</summary>
public abstract record StageHandlerDispatch
{
    private StageHandlerDispatch()
    {
    }

    /// <summary>The handler ran on <paramref name="StageId"/> and returned <paramref name="Result"/>.</summary>
    public sealed record Ran(string StageId, StageHandlerResult Result) : StageHandlerDispatch;

    /// <summary>The pinned definition is runnable but no stage declares the handler (e.g. written before it existed).</summary>
    public sealed record NotDeclared : StageHandlerDispatch;

    /// <summary>
    /// The pinned definition can't run in this build (FR-277): neither registered nor stored, naming a name this build
    /// doesn't register, or the handler is registered but not bound in DI. <paramref name="Missing"/> says what.
    /// </summary>
    public sealed record Unavailable(string Missing) : StageHandlerDispatch;
}

/// <summary>
/// FR-360: what <see cref="StageHandlerRunner.PrepareExternalAsync"/> did: <see cref="Dispatch"/> is
/// <see cref="StageHandlerDispatch.Ran"/> (with <see cref="Payload"/>) when the handler prepared its payload, otherwise
/// why it couldn't run (<see cref="StageHandlerDispatchExtensions.RequireRan"/> maps that to the job layer).
/// </summary>
public sealed record ExternalPreparation(StageHandlerDispatch Dispatch, ExternalActionPayload? Payload);

/// <summary>D-0.7.0-43: mapping a dispatch to the job layer.</summary>
public static class StageHandlerDispatchExtensions
{
    /// <summary>
    /// For a host that requires the handler: the result of a <see cref="StageHandlerDispatch.Ran"/> dispatch, else a
    /// permanent <see cref="JobFailedException"/> with <c>DEFINITION_UNAVAILABLE</c> (the run's definition can't run the
    /// handler), as an agent with no stage in the pinned definition is refused.
    /// </summary>
    public static StageHandlerResult RequireRan(this StageHandlerDispatch dispatch, string handler)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return dispatch switch
        {
            StageHandlerDispatch.Ran ran => ran.Result,
            StageHandlerDispatch.Unavailable unavailable => throw new JobFailedException(
                $"Stage handler '{handler}' can't run on this pipeline definition (missing: {unavailable.Missing}).", transient: false, EngineErrorCodes.DefinitionUnavailable),
            _ => throw new JobFailedException(
                $"The pipeline definition declares no stage with handler '{handler}'.", transient: false, EngineErrorCodes.DefinitionUnavailable),
        };
    }

    /// <summary>
    /// A <see cref="StageHandlerOutcome.Failed"/> result fails the job permanently with the result's code (default
    /// <c>VALIDATION</c>); <see cref="StageHandlerOutcome.Completed"/> and <see cref="StageHandlerOutcome.Waiting"/> (the
    /// work continues elsewhere, the stage stays current) let the job succeed.
    /// </summary>
    public static void ThrowIfFailed(this StageHandlerResult result, string handler)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == StageHandlerOutcome.Failed)
        {
            throw new JobFailedException(result.Message ?? $"Stage handler '{handler}' failed.", transient: false, result.ErrorCode ?? EngineErrorCodes.Validation);
        }
    }
}
