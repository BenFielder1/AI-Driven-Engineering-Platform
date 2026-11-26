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

    /// <summary>FR-355: a map stage: open its items (or go on with them) and reduce once they're done.</summary>
    public sealed record RunMap(MapDefinition Map) : DriverStep;

    /// <summary>FR-356: a conversation stage: run its next turn, or stop and store the transcript.</summary>
    public sealed record RunConversation(ConversationDefinition Conversation) : DriverStep;
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

/// <summary>FR-348, §4.66: whether one more backward transition fits <c>limits.revisions</c>.</summary>
public abstract record RevisionCheck
{
    private RevisionCheck()
    {
    }

    /// <summary>Allowed: the run's count becomes <paramref name="Revisions"/>.</summary>
    public sealed record Allowed(int Revisions) : RevisionCheck;

    /// <summary>Refused: <paramref name="Then"/> applies (escalate or fail).</summary>
    public sealed record Refused(InvalidOutcome Then, string Reason) : RevisionCheck;
}

/// <summary>FR-347, FR-344, FR-348: a backward move to an earlier stage.</summary>
public abstract record BackwardMove
{
    private BackwardMove()
    {
    }

    /// <summary>Re-enter <paramref name="State"/>'s stage (a <c>loop</c>); the run's revisions become
    /// <paramref name="Revisions"/>.</summary>
    public sealed record Reenter(RunState State, int Revisions) : BackwardMove;

    /// <summary>The revision cap refuses the move: <paramref name="Then"/> applies.</summary>
    public sealed record Refused(InvalidOutcome Then, string Reason) : BackwardMove;
}

/// <summary>FR-347: what a stage's <c>route</c> value does.</summary>
public abstract record RouteChoice
{
    private RouteChoice()
    {
    }

    /// <summary><c>continue</c> (or a stage without routes): move on normally.</summary>
    public sealed record Proceed : RouteChoice;

    /// <summary>Go back to the earlier stage <paramref name="Stage"/>.</summary>
    public sealed record Back(string Stage) : RouteChoice;

    /// <summary>Not <c>continue</c> or a declared route: the stage fails (<c>VALIDATION</c>).</summary>
    public sealed record Invalid(string Reason) : RouteChoice;
}

/// <summary>FR-344: where a vetoed stage goes.</summary>
public abstract record VetoNext
{
    private VetoNext()
    {
    }

    /// <summary><c>on_veto: { to }</c>: the backward move (or its refusal by the revision cap).</summary>
    public sealed record Move(BackwardMove Back) : VetoNext;

    /// <summary><c>on_veto: { end: vetoed }</c>: the run ends <c>completed</c> with outcome <c>vetoed</c>.</summary>
    public sealed record EndVetoed(string PackStatus) : VetoNext;
}

/// <summary>
/// FR-332, §4.62: the run driver's pure decisions. The driver moves an engine-controlled run (<c>RunControl.Engine</c>)
/// one stage at a time: steps 2–4 are <see cref="Plan"/>, step 5 is <see cref="After"/>, gate rejections are
/// <see cref="OnGateRejected"/>. Loops whose <c>on</c> names an outcome are not taken by the driver: no stage kind it runs
/// today reports an outcome, so a stage with a loop advances along <c>on_success</c> or the next stage.
/// </summary>
public static partial class RunDriverRules
{
    /// <summary>
    /// §4.62 step 1: whether a <c>stage.run</c> delivery for <paramref name="stageExecution"/> has nothing to do: the run is
    /// final, the stage execution is unknown, already finished, or no longer the run's current stage (idempotent on redelivery).
    /// A branch execution (FR-343) is checked against the run's current group by <see cref="InGroup"/> once the definition is known.
    /// </summary>
    public static bool IsStale(PipelineRun run, StageExecution? stageExecution)
    {
        ArgumentNullException.ThrowIfNull(run);
        return PipelineRunRules.IsFinal(run.Status)
            || stageExecution is null
            || stageExecution.PipelineRunId != run.Id
            || stageExecution.ExitedAt is not null
            || (stageExecution.Branch is null && !string.Equals(stageExecution.StageId, run.CurrentStage, StringComparison.Ordinal));
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

        // FR-355, FR-356: map and conversation stages have no agent or handler of their own (their kind reads manual).
        if (stage.Map is { } map)
        {
            return new DriverStep.RunMap(map);
        }

        if (stage.Conversation is { } conversation)
        {
            return new DriverStep.RunConversation(conversation);
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

    /// <summary>
    /// FR-348, §4.66: one more backward transition (a loop, <c>on_reject</c>, a route to an earlier stage or a veto
    /// <c>to:</c>) when the run has made <paramref name="revisions"/>. Without <c>limits</c> it is always allowed; with them,
    /// the transition that would make the count exceed <c>limits.revisions</c> is refused and <c>then</c> applies.
    /// </summary>
    public static RevisionCheck CountRevision(PipelineDefinition definition, int revisions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var next = revisions + 1;
        if (definition.Limits is { } limits && next > limits.Revisions)
        {
            return new RevisionCheck.Refused(
                limits.Then,
                $"The run has made {revisions} revision(s), its limit of {limits.Revisions}: another backward transition is refused.");
        }

        return new RevisionCheck.Allowed(next);
    }

    /// <summary>
    /// FR-347, FR-344, FR-348: a backward move of the run from <paramref name="state"/> to the earlier stage
    /// <paramref name="target"/> (a <c>loop</c>: checkpoint unchanged, one more entry of the target), checked against
    /// <c>limits.revisions</c>.
    /// </summary>
    public static BackwardMove Back(PipelineDefinition definition, RunState state, string target, int revisions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        var stage = definition.FindStage(target)
            ?? throw new ArgumentOutOfRangeException(nameof(target), target, "The target stage is not in the definition.");
        return Revise(definition, new RunState(stage.Id, stage.Status, state.Checkpoint, Entered(state.Iterations, stage.Id)), revisions);
    }

    /// <summary>FR-348: re-entering <paramref name="reentered"/> (already worked out, e.g. by <see cref="OnGateRejected"/>)
    /// as one more backward transition, checked against <c>limits.revisions</c>.</summary>
    public static BackwardMove Revise(PipelineDefinition definition, RunState reentered, int revisions) =>
        CountRevision(definition, revisions) switch
        {
            RevisionCheck.Allowed allowed => new BackwardMove.Reenter(reentered, allowed.Revisions),
            RevisionCheck.Refused refused => new BackwardMove.Refused(refused.Then, refused.Reason),
            _ => throw new InvalidOperationException("Unknown revision check."),
        };

    /// <summary>
    /// FR-347: what <paramref name="stage"/>'s <c>route</c> value does. A stage without <c>routes</c> always continues; with
    /// them, <c>continue</c> continues, one of its routes goes back to that stage, and anything else (including no value)
    /// is invalid.
    /// </summary>
    public static RouteChoice Route(StageDefinition stage, string? route)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Routes.Count == 0)
        {
            return new RouteChoice.Proceed();
        }

        if (string.Equals(route, ContinueRoute, StringComparison.Ordinal))
        {
            return new RouteChoice.Proceed();
        }

        if (route is not null && stage.Routes.Contains(route, StringComparer.Ordinal))
        {
            return new RouteChoice.Back(route);
        }

        return new RouteChoice.Invalid(
            $"The {stage.Id} output's route '{route ?? "(none)"}' is not '{ContinueRoute}' or one of: {string.Join(", ", stage.Routes)}.");
    }

    /// <summary>FR-347: the <c>route</c> value that proceeds normally.</summary>
    public const string ContinueRoute = "continue";

    /// <summary>
    /// FR-347: the <c>route</c> field of a stage's output: a JSON object's top-level <c>route</c> string, or, for other
    /// content (markdown), the first line of the form <c>route: &lt;value&gt;</c>. <c>null</c> when there is none.
    /// </summary>
    public static string? ReadRoute(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        if (TryParseObject(content) is { } json)
        {
            return json.TryGetPropertyValue("route", out var value) && value is System.Text.Json.Nodes.JsonValue v
                && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        }

        // Only a line that is exactly `route: <id>` (lower case, at column 0); the last one wins, so prose isn't read.
        string? found = null;
        foreach (var line in content.Split('\n'))
        {
            var match = RouteLine().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                found = match.Groups[1].Value;
            }
        }

        return found;
    }

    /// <summary>
    /// FR-344: what a veto agent said. Its output is a JSON object: <c>{ "findings": ["…"] }</c> (any finding is a veto) or
    /// <c>{ "approve": true }</c> / an empty <c>findings</c> (approval). Anything else is unreadable (<c>null</c>), which
    /// fails the stage.
    /// </summary>
    public static IReadOnlyList<string>? ReadVetoFindings(string? content)
    {
        if (TryParseObject(content) is not { } json)
        {
            return null;
        }

        if (json.TryGetPropertyValue("findings", out var findings) && findings is System.Text.Json.Nodes.JsonArray array)
        {
            var list = new List<string>();
            foreach (var item in array)
            {
                switch (item)
                {
                    case System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text):
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            list.Add(text.Trim());
                        }

                        break;
                    case null:
                        break;
                    default:
                        list.Add(item.ToJsonString());
                        break;
                }
            }

            return list;
        }

        return json.TryGetPropertyValue("approve", out var approve) && approve is System.Text.Json.Nodes.JsonValue a
            && a.TryGetValue<bool>(out var approved) && approved ? [] : null;
    }

    /// <summary>
    /// FR-344: where a vetoed stage goes. <c>on_veto: { to }</c> goes back to that stage (checked against
    /// <c>limits.revisions</c>); <c>on_veto: { end: vetoed }</c> (or none) ends the run <c>completed</c>, outcome
    /// <c>vetoed</c>.
    /// </summary>
    public static VetoNext OnVetoed(PipelineDefinition definition, RunState state, int revisions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        var stage = definition.FindStage(state.Stage)
            ?? throw new ArgumentOutOfRangeException(nameof(state), state.Stage, "The run's stage is not in its definition.");
        return stage.OnVeto?.To is { } to
            ? new VetoNext.Move(Back(definition, state, to, revisions))
            : new VetoNext.EndVetoed(CompletedStatus(definition));
    }

    /// <summary>
    /// FR-347, FR-344, FR-332: the artifacts a backward move to <paramref name="targetStage"/> invalidates: everything that
    /// depends on the outputs of the target (and its branch stages), not those outputs themselves (they get new versions
    /// when the target runs again), in the definition's artifact order.
    /// </summary>
    public static IReadOnlyList<string> InvalidatedOnBack(PipelineDefinition definition, string targetStage)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var target = definition.FindStage(targetStage)
            ?? throw new ArgumentOutOfRangeException(nameof(targetStage), targetStage, "The target stage is not in the definition.");
        var outputs = target.WithBranchStages().Select(s => s.Output).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var found = outputs.SelectMany(o => DefinitionCascade.Downstream(o, definition))
            .Where(k => !outputs.Contains(k))
            .ToHashSet(StringComparer.Ordinal);
        var declared = definition.Artifacts.Select(a => a.Key).Where(found.Contains).ToList();
        return [.. declared, .. found.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^route: ([A-Za-z0-9_\-]+)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RouteLine();

    /// <summary>FR-344: the run outcome of a run a veto ended.</summary>
    public const string VetoedOutcome = OnVetoDefinition.Vetoed;

    private static System.Text.Json.Nodes.JsonObject? TryParseObject(string? content)
    {
        var trimmed = content?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Tolerate a fenced JSON block.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('\n', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (start < 0 || end <= start)
            {
                return null;
            }

            trimmed = trimmed[(start + 1)..end].Trim();
        }

        if (!trimmed.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(trimmed) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
