using System.Globalization;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Microsoft.Extensions.Options;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Pipelines;

/// <summary>FR-291: <c>Pipelines:Parity</c>. <see cref="Throw"/> is on in the test hosts only; production logs.</summary>
public sealed class ParityOptions
{
    public const string SectionName = "Pipelines:Parity";

    /// <summary>Throw <see cref="ParityMismatchException"/> on a difference (tests); otherwise log an error and continue.</summary>
    public bool Throw { get; set; }
}

/// <summary>FR-291: the old lifecycle machine and the engine disagreed (thrown only with <see cref="ParityOptions.Throw"/>).</summary>
public sealed class ParityMismatchException : Exception
{
    public ParityMismatchException()
    {
    }

    public ParityMismatchException(string message)
        : base(message)
    {
    }

    public ParityMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// FR-291 (M58 parity mode): every lifecycle decision in the app goes through here. It runs 0.6.1's
/// <see cref="LifecycleStateMachine"/> (or <see cref="InvalidationCascade"/>) and the engine with the registry's SDLC
/// definition (<see cref="LifecycleParity"/>), and always returns the <b>old</b> result, so behaviour is unchanged. When
/// the two differ (or the engine throws, or there is no valid SDLC definition) it throws <see cref="ParityMismatchException"/>
/// with the differences when <c>Pipelines:Parity:Throw</c> is on (the test hosts), and otherwise logs an error with the
/// project, the move and both results, and continues. Removed at FR-293.
/// </summary>
public sealed partial class ParityStateMachine(PipelineRegistryHost registries, IOptions<ParityOptions> options, ILogger<ParityStateMachine> logger)
{
    /// <summary>The review-loop position of <paramref name="project"/> (D-0.7.0-27, D-0.7.0-30, FR-14).</summary>
    public static ReviewLoopState LoopOf(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ReviewLoopState.Of(project.ReviewCycle, project.MaxReviewCycles, project.ExtraReviewCycles);
    }

    /// <summary>The lifecycle state of <paramref name="project"/>.</summary>
    public static LifecycleState StateOf(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint);
    }

    /// <summary><see cref="LifecycleStateMachine.Apply"/> on <paramref name="project"/>'s state.</summary>
    public Result<PhaseTransition> Apply(Project project, TransitionRequest request, LifecycleSnapshot snapshot) =>
        Apply(project?.Id ?? throw new ArgumentNullException(nameof(project)), StateOf(project), request, snapshot, LoopOf(project));

    public Result<PhaseTransition> Apply(Guid projectId, LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot, ReviewLoopState loop)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Check(
            projectId,
            request.Kind.ToString(),
            () => LifecycleStateMachine.Apply(state, request, snapshot),
            definition => LifecycleParity.Apply(state, request, snapshot, loop, definition));
    }

    /// <summary><see cref="LifecycleStateMachine.RequestAdvance"/> on <paramref name="project"/>'s state.</summary>
    public Result<AdvanceOutcome> RequestAdvance(Project project, LifecycleSnapshot snapshot, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var (state, loop) = (StateOf(project), LoopOf(project));
        return Check(
            project.Id,
            "RequestAdvance",
            () => LifecycleStateMachine.RequestAdvance(state, snapshot, reason),
            definition => LifecycleParity.RequestAdvance(state, snapshot, loop, definition, reason));
    }

    /// <summary><see cref="InvalidationCascade.Effect"/> (§4.54) against the definition's cascade with the SDLC hook.</summary>
    public InvalidationEffect CascadeEffect(Guid projectId, string approved, Phase currentPhase) =>
        Check(
            projectId,
            "Cascade",
            () => InvalidationCascade.Effect(approved, currentPhase),
            definition => LifecycleParity.CascadeEffect(approved, currentPhase, definition));

    /// <summary><see cref="LifecycleStateMachine.Start"/> (a new project's first version) against the engine's start.</summary>
    public Result<LifecycleState> Start(Guid projectId, bool infersStack, bool baseHasCode) =>
        Check(
            projectId,
            "Start",
            () => LifecycleStateMachine.Start(infersStack, baseHasCode),
            definition => LifecycleParity.Start(infersStack, baseHasCode, definition));

    /// <summary>The longest difference value written to the log (a reason can hold a reviewer's free-text comment).</summary>
    public const int LoggedValueMaxLength = 200;

    private int _noDefinitionLogged;

    private T Check<T>(Guid projectId, string move, Func<T> old, Func<PipelineDefinition, ParityCheck<T>> both)
    {
        ParityCheck<T> check;
        try
        {
            if (registries.Registry is not { } registry || !registry.TryGet(SdlcPack.PipelineId, out var loaded) || loaded.Definition is not { } definition || !loaded.IsValid)
            {
                return NoDefinition(projectId, move, old());
            }

            check = both(definition);
        }
        catch (Exception exception) when (exception is not ParityMismatchException)
        {
            return Mismatch(projectId, move, old(), $"threw {exception.GetType().Name}", [$"engine threw: {exception.Message}"], exception);
        }

        return check.Agrees ? check.Old : Mismatch(projectId, move, check.Old, check.Engine, check.Differences, null);
    }

    /// <summary>No valid SDLC definition: a mismatch on every decision, but logged once per process (readiness already
    /// reports it, D-0.7.0-28).</summary>
    private T NoDefinition<T>(Guid projectId, string move, T old)
    {
        if (options.Value.Throw)
        {
            throw new ParityMismatchException($"Lifecycle parity check for project {projectId} ({move}) failed: the registry has no valid SDLC definition.");
        }

        if (Interlocked.Exchange(ref _noDefinitionLogged, 1) == 0)
        {
            LogNoDefinition(logger, projectId, move);
        }

        return old;
    }

    private T Mismatch<T>(Guid projectId, string move, T old, string engine, IReadOnlyList<string> differences, Exception? exception)
    {
        if (options.Value.Throw)
        {
            var oldText = old is Result result ? Describe(result) : old?.ToString() ?? "null";
            var message = $"Lifecycle parity mismatch for project {projectId} ({move}): {string.Join("; ", differences)}. Old: {oldText}. Engine: {engine}.";
            throw exception is null ? new ParityMismatchException(message) : new ParityMismatchException(message, exception);
        }

        // Only the differing fields, each value truncated, and the old outcome's stage or code: never the full results.
        LogMismatch(logger, exception, projectId, move, string.Join("; ", differences.Select(ForLog)), Brief(old));
        return old;
    }

    /// <summary><paramref name="value"/> cut to <see cref="LoggedValueMaxLength"/> characters (with an ellipsis).</summary>
    public static string ForLog(string value) =>
        value.Length <= LoggedValueMaxLength ? value : string.Concat(value.AsSpan(0, LoggedValueMaxLength), "…");

    /// <summary>The old outcome without free text: the stage, status and checkpoint, the gate needed, or the error code.</summary>
    private static string Brief(object? old) => old switch
    {
        Result { Error: { } error } => error.Code,
        Result<PhaseTransition> t => $"{t.Value.From.ToWire()} → {t.Value.To.ToWire()}/{t.Value.Status.ToWire()} @{t.Value.Version.ToString(CultureInfo.InvariantCulture)}",
        Result<AdvanceOutcome> { Value.GateRequired: { } gate } => $"gate needed: {gate}",
        Result<AdvanceOutcome> a => Brief(Result.Success(a.Value.Transition!)),
        Result<LifecycleState> s => $"{s.Value.Phase.ToWire()}/{s.Value.Status.ToWire()} @{s.Value.Version.ToString(CultureInfo.InvariantCulture)}",
        InvalidationEffect e => $"fallback {e.FallbackPhase?.ToWire() ?? "none"}",
        _ => "?",
    };

    private static string Describe(Result result) => result switch
    {
        Result<PhaseTransition> t => LifecycleParityComparer.Describe(t),
        Result<AdvanceOutcome> a => LifecycleParityComparer.Describe(a),
        Result<LifecycleState> s => LifecycleParityComparer.Describe(s),
        _ => result.Error is { } error ? $"{error.Code}: {error.Message}" : "ok",
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Lifecycle parity mismatch for project {ProjectId} ({Move}): {Differences}. Old outcome: {OldOutcome}")]
    private static partial void LogMismatch(ILogger logger, Exception? exception, Guid projectId, string move, string differences, string oldOutcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lifecycle parity is off: the registry has no valid SDLC definition (first seen for project {ProjectId}, {Move}); logged once per process")]
    private static partial void LogNoDefinition(ILogger logger, Guid projectId, string move);
}
