using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-332, §4.62 step 3: the state of a stage's entry gate for the current entry.</summary>
public enum EntryGateState
{
    /// <summary>The stage has no gate in the run's approval mode.</summary>
    NotNeeded = 0,

    /// <summary>The gate was approved after the run entered the stage.</summary>
    Approved = 1,

    /// <summary>The gate is pending.</summary>
    Pending = 2,

    /// <summary>The gate is needed and hasn't been raised for this entry.</summary>
    Missing = 3,
}

/// <summary>
/// FR-332, §4.62 steps 2–3: what the caller found about the stage the run is in. <see cref="MissingInputs"/> are the keys
/// of unmet <c>requires: [{ input }]</c>; <see cref="UnmetReasons"/> the reasons of every other unmet requirement
/// (built-ins and guards, in declared order).
/// </summary>
public sealed record StageEntryFacts
{
    public IReadOnlyList<string> MissingInputs { get; init; } = [];

    public IReadOnlyList<string> UnmetReasons { get; init; } = [];

    public EntryGateState Gate { get; init; } = EntryGateState.NotNeeded;
}

/// <summary>FR-332, §4.62 steps 2–4: what the run driver does with the stage the run is in.</summary>
public abstract record DriverStep
{
    private DriverStep()
    {
    }

    /// <summary>A required input is missing: the run waits (<c>waiting_on_input</c>).</summary>
    public sealed record WaitOnInput(IReadOnlyList<string> ArtifactKeys) : DriverStep;

    /// <summary>A requirement other than an input is unmet: the stage fails with the reasons.</summary>
    public sealed record FailStage(IReadOnlyList<string> Reasons) : DriverStep;

    /// <summary>The entry gate is needed: raise it (pending) and stop (<c>waiting_on_gate</c>).</summary>
    public sealed record RaiseGate(string GateType) : DriverStep;

    /// <summary>The entry gate is already pending: stop.</summary>
    public sealed record WaitOnGate(string GateType) : DriverStep;

    /// <summary>Run the stage's agent (<c>AgentStageRunner</c>).</summary>
    public sealed record RunAgent(string AgentKey) : DriverStep;

    /// <summary>Run the stage's handler (<c>StageHandlerRunner</c>).</summary>
    public sealed record RunHandler(string Handler) : DriverStep;

    /// <summary>A manual stage that is only an approval (D-0.7.0-13): its approved gate is its work, so it moves on.</summary>
    public sealed record PassApproval : DriverStep;

    /// <summary>A manual stage without a gate: its work happens through pack endpoints, so the driver stops.</summary>
    public sealed record StopManual : DriverStep;
}

/// <summary>FR-332, §4.62 steps 5–6: where the run goes after its stage's work is done.</summary>
public abstract record DriverNext
{
    private DriverNext()
    {
    }

    /// <summary>Enter <paramref name="State"/>'s stage (an <c>advance</c>, checkpoint + 1) and queue the driver.</summary>
    public sealed record Advance(RunState State) : DriverNext;

    /// <summary>The last stage is done: the run ends <c>completed</c> with pack status <paramref name="PackStatus"/>.</summary>
    public sealed record Complete(string PackStatus) : DriverNext;
}

/// <summary>FR-332: what a rejected entry gate does to an engine-controlled run.</summary>
public abstract record GateRejection
{
    private GateRejection()
    {
    }

    /// <summary>The stage's <c>on_reject</c>: re-enter <paramref name="State"/>'s stage (a <c>loop</c>, checkpoint unchanged).</summary>
    public sealed record Reenter(RunState State) : GateRejection;

    /// <summary>No <c>on_reject</c>, or its cap is reached: the run ends <c>failed</c>.</summary>
    public sealed record FailRun(string Reason) : GateRejection;
}

/// <summary>
/// FR-332, §4.62: the run driver's pure decisions. The driver moves an engine-controlled run (<c>RunControl.Engine</c>)
/// one stage at a time: steps 2–4 are <see cref="Plan"/>, step 5 is <see cref="After"/>, gate rejections are
/// <see cref="OnGateRejected"/>. Loops whose <c>on</c> names an outcome are not taken by the driver: no stage kind it runs
/// today reports an outcome, so a stage with a loop advances along <c>on_success</c> or the next stage.
/// </summary>
public static class RunDriverRules
{
    /// <summary>
    /// §4.62 step 1: whether a <c>stage.run</c> delivery for <paramref name="stageExecution"/> has nothing to do: the run is
    /// final, the stage execution is unknown, already finished, or no longer the run's current stage (idempotent on redelivery).
    /// </summary>
    public static bool IsStale(PipelineRun run, StageExecution? stageExecution)
    {
        ArgumentNullException.ThrowIfNull(run);
        return PipelineRunRules.IsFinal(run.Status)
            || stageExecution is null
            || stageExecution.PipelineRunId != run.Id
            || stageExecution.ExitedAt is not null
            || !string.Equals(stageExecution.StageId, run.CurrentStage, StringComparison.Ordinal);
    }

    /// <summary>
    /// §4.62 steps 2–4 for <paramref name="stage"/>: a missing input waits; any other unmet requirement fails the stage; a
    /// needed gate is raised or waited on; otherwise the stage runs by kind.
    /// </summary>
    public static DriverStep Plan(StageDefinition stage, StageEntryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.MissingInputs.Count > 0)
        {
            return new DriverStep.WaitOnInput(facts.MissingInputs);
        }

        if (facts.UnmetReasons.Count > 0)
        {
            return new DriverStep.FailStage(facts.UnmetReasons);
        }

        switch (facts.Gate)
        {
            case EntryGateState.Missing:
                return new DriverStep.RaiseGate(stage.Gate!.Type);
            case EntryGateState.Pending:
                return new DriverStep.WaitOnGate(stage.Gate!.Type);
        }

        return stage.Kind switch
        {
            StageKind.Agent => new DriverStep.RunAgent(stage.Agent!.Key),
            StageKind.Code => new DriverStep.RunHandler(stage.Handler!),
            _ => stage.Gate is not null ? new DriverStep.PassApproval() : new DriverStep.StopManual(),
        };
    }

    /// <summary>The input keys of <paramref name="stage"/>'s <c>requires: [{ input }]</c> that aren't in
    /// <paramref name="presentInputs"/>, in declared order.</summary>
    public static IReadOnlyList<string> MissingInputs(StageDefinition stage, IReadOnlyCollection<string> presentInputs)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(presentInputs);
        return [.. stage.Requires.OfType<InputRequirement>()
            .Select(r => r.ArtifactKey)
            .Where(k => !presentInputs.Contains(k, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>§4.62 step 3: the entry gate's state from whether it applies in <paramref name="mode"/>, and what the run has.</summary>
    public static EntryGateState GateState(StageDefinition stage, ApprovalMode mode, bool approvedSinceEntry, bool pending)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (BuiltInRequirements.RequiredGate(stage, mode) is null)
        {
            return EntryGateState.NotNeeded;
        }

        return approvedSinceEntry ? EntryGateState.Approved : pending ? EntryGateState.Pending : EntryGateState.Missing;
    }

    /// <summary>
    /// §4.62 steps 5–6: after the work of <paramref name="state"/>'s stage, advance to its successor (<c>on_success</c>, else
    /// the next stage: the successor's status, checkpoint + 1, one more entry), or complete the run after the last stage.
    /// </summary>
    public static DriverNext After(PipelineDefinition definition, RunState state)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        if (definition.FindStage(state.Stage) is null)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.Stage, "The run's stage is not in its definition.");
        }

        if (definition.SuccessorOf(state.Stage) is not { } next)
        {
            return new DriverNext.Complete(CompletedStatus(definition));
        }

        var entered = definition.FindStage(next)!;
        return new DriverNext.Advance(new RunState(next, entered.Status, state.Checkpoint + 1, Entered(state.Iterations, next)));
    }

    /// <summary>
    /// FR-332: a rejected entry gate of <paramref name="state"/>'s stage. With <c>on_reject</c>, the run re-enters its target
    /// (a loop: checkpoint unchanged) unless the target's entries already reach the cap (<paramref name="cap"/>, resolved by
    /// the caller); without one, the run fails.
    /// </summary>
    public static GateRejection OnGateRejected(PipelineDefinition definition, RunState state, string gateType, int? cap)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        var stage = definition.FindStage(state.Stage)
            ?? throw new ArgumentOutOfRangeException(nameof(state), state.Stage, "The run's stage is not in its definition.");
        if (stage.OnReject is not { } reject || definition.FindStage(reject.To) is not { } target)
        {
            return new GateRejection.FailRun($"The {gateType} gate was rejected.");
        }

        if (cap is { } limit && state.IterationsOf(target.Id) >= limit)
        {
            return new GateRejection.FailRun($"The {gateType} gate was rejected and {target.Id} has run {limit} times, its limit.");
        }

        return new GateRejection.Reenter(new RunState(target.Id, target.Status, state.Checkpoint, Entered(state.Iterations, target.Id)));
    }

    /// <summary>The pack status of a completed run: the definition's first status mapping to <c>completed</c>, else
    /// <c>completed</c>.</summary>
    public static string CompletedStatus(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Statuses.FirstOrDefault(s => s.Generic == GenericRunStatus.Completed)?.Name
            ?? GenericRunStatus.Completed.ToWire();
    }

    /// <summary>The pack status of a run ended <paramref name="status"/> (failed or cancelled): the definition's first status
    /// mapping to it, else its wire value.</summary>
    public static string FinalStatus(PipelineDefinition definition, GenericRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Statuses.FirstOrDefault(s => s.Generic == status)?.Name ?? status.ToWire();
    }

    private static Dictionary<string, int> Entered(IReadOnlyDictionary<string, int> iterations, string stage)
    {
        var next = new Dictionary<string, int>(iterations, StringComparer.Ordinal);
        next[stage] = next.TryGetValue(stage, out var count) ? count + 1 : 1;
        return next;
    }
}

/// <summary>
/// FR-331, D-0.8.0-18: the key of the transaction-scoped PostgreSQL advisory lock (<c>pg_advisory_xact_lock</c>) a run
/// start holds on (project, pipeline) while it checks for an open run and inserts the new one. Stable across processes and
/// releases: the first 8 bytes (big-endian) of SHA-256 over <c>&lt;project id, 32 hex digits&gt;/&lt;pipeline id&gt;</c>.
/// </summary>
public static class RunStartLock
{
    public static long Key(Guid projectId, string pipelineId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipelineId);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(projectId.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + "/" + pipelineId));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
    }
}
