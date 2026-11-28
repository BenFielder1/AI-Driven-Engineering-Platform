namespace Tevrin.Engine.Domain.Jobs;

/// <summary>
/// FR-272, D-0.7.0-23: the job types the engine owns. Values are stored in <c>jobs.type</c>, so they never change once
/// used. A pack adds its own types by registering a handler for each (the SDLC's are
/// <c>Tevrin.Pack.Sdlc.SdlcJobTypes</c>); enqueuing a type with no registered handler fails at enqueue.
/// </summary>
public static class EngineJobTypes
{
    /// <summary>Runs one stage of a pipeline run.</summary>
    public const string StageRun = "stage.run";

    /// <summary>Runs one agent.</summary>
    public const string AgentRun = "agent.run";

    /// <summary>Reserved: a gate's reminder or overdue timer. Unused in 0.7.0 (D-0.7.0-15).</summary>
    public const string GateTimer = "gate.timer";

    /// <summary>Reserved: fires a pipeline trigger. Unused in 0.7.0 (D-0.7.0-15).</summary>
    public const string TriggerFire = "trigger.fire";

    /// <summary>
    /// FR-332, FR-336 (0.8.0): the job types that move engine-controlled runs (the run driver; from M66 step c, trigger
    /// fires). A host's own lifecycle operations (e.g. the SDLC rollback) leave them alone: they belong to other runs.
    /// </summary>
    public static IReadOnlyList<string> RunScoped { get; } = [StageRun, TriggerFire];

    /// <summary>Every engine job type.</summary>
    public static IReadOnlyList<string> All { get; } = [StageRun, AgentRun, GateTimer, TriggerFire];
}
