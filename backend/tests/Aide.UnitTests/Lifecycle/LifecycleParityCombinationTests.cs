using System.Collections.Concurrent;
using System.Reflection;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// FR-291, §4.56: the generated parity test. Both machines (0.6.1's <see cref="LifecycleStateMachine"/> and the engine with
/// the SDLC definition, through <see cref="LifecycleParity"/>, the mapper and comparer the app uses) run over the §4.56
/// combinations and must agree on success, error code, message, unmet reasons (in order), stage, status, checkpoint, kind,
/// reason, review/loop state clearing, the job queued on entry and the gate needed. The full product of every dimension is
/// about 10⁹ moves, so the test is the union of three full products, each over the dimensions that matter to it:
/// <list type="number">
/// <item><b>State × request</b>: every stage × every status × every operation (each <see cref="TransitionKind"/> through
/// <c>Apply</c>, <c>RequestAdvance</c>, and <c>UnmetEntryConditions</c> with and without the gate) × approval mode × target
/// phase (each phase, and none) × target checkpoint (none, 0, 1, below, equal, above) × the "all true" and "all false"
/// snapshots. Status changes only target <c>release</c> or nothing: they name their stage in the engine, and 0.6.1's
/// factories always target release.</item>
/// <item><b>Facts</b>: every stage at its own status and at <c>rolled_back</c> (plus <c>release/released</c> and
/// <c>release/awaiting_merge</c>) × the fact-reading operations (advance, request advance, review cycle, new version into
/// refinement and analysis, unmet entry conditions for every phase with and without the gate) × approval mode × every
/// snapshot fact toggled one at a time from "all true" and from "all false" (<see cref="Toggles"/>, checked against
/// <see cref="LifecycleSnapshot"/>'s properties) × review decision (none, each value for this checkpoint, each value for an
/// earlier, stale checkpoint) × escalation outcome (none, each value) × pending gates (none, each gate, two pairs).</item>
/// <item><b>Review loop cap</b> (D-0.7.0-30): the review cycle at review cycles 0 … cap + 1 with
/// <c>maxReviewCycles</c> below, equal to and above the current cycle and <c>extraReviewCycles</c> 0 or 1 (after an
/// escalation <c>continue</c>), over the facts of (2). The SDLC's <c>review_cycles</c> never drops below the cycle + 1, so
/// the engine's backstop never refuses a cycle 0.6.1 allows, and the machines must always agree.</item>
/// </list>
/// The review iteration is kept below <c>review_cycles</c> in (1) and (2), as the app's review decision rules do. The test
/// uses every core for about 10 seconds, so it runs on its own (<see cref="ParityRunsAlone"/>), not beside timing-sensitive
/// tests.
/// </summary>
[Collection(ParityRunsAlone.Name)]
public class LifecycleParityCombinationTests
{
    private const int Checkpoint = 4;
    private const int ReviewCycles = 3;

    /// <summary>The number of moves the test compares (documents the coverage; update it when a dimension changes).</summary>
    private const long ExpectedCombinations = 6_271_314;

    private static readonly ReviewLoopState BelowCap = new(1, ReviewCycles);

    /// <summary>Every snapshot fact as a boolean: its name and how to set it.</summary>
    public static IReadOnlyList<(string Name, Func<LifecycleSnapshot, bool, LifecycleSnapshot> Set)> Toggles { get; } =
    [
        (nameof(LifecycleSnapshot.ProjectRequestLocked), (s, v) => s with { ProjectRequestLocked = v }),
        (nameof(LifecycleSnapshot.TechnicalSpecificationLocked), (s, v) => s with { TechnicalSpecificationLocked = v }),
        (nameof(LifecycleSnapshot.ImplementationPlanLocked), (s, v) => s with { ImplementationPlanLocked = v }),
        (nameof(LifecycleSnapshot.CurrentPlanStepCount), (s, v) => s with { CurrentPlanStepCount = v ? 3 : 0 }),
        (nameof(LifecycleSnapshot.CurrentPlanCompletedStepCount), (s, v) => s with { CurrentPlanCompletedStepCount = v ? 3 : 1 }),
        (nameof(LifecycleSnapshot.WorkingCopyPrepared), (s, v) => s with { WorkingCopyPrepared = v }),
        (nameof(LifecycleSnapshot.OptimizationPlanLocked), (s, v) => s with { OptimizationPlanLocked = v }),
        (nameof(LifecycleSnapshot.OptimizationPlanStepCount), (s, v) => s with { OptimizationPlanStepCount = v ? 2 : 0 }),
        (nameof(LifecycleSnapshot.EntryGateApproved), (s, v) => s with { EntryGateApproved = v }),
        (nameof(LifecycleSnapshot.DevelopmentVersionExists), (s, v) => s with { DevelopmentVersionExists = v }),
        (nameof(LifecycleSnapshot.StackProfileLocked), (s, v) => s with { StackProfileLocked = v }),
        (nameof(LifecycleSnapshot.StackCompositionPinned), (s, v) => s with { StackCompositionPinned = v }),
        (nameof(LifecycleSnapshot.StackProfileGeneralist), (s, v) => s with { StackProfileGeneralist = v }),
        (nameof(LifecycleSnapshot.InfersStack), (s, v) => s with { InfersStack = v }),
        (nameof(LifecycleSnapshot.BaseHasCode), (s, v) => s with { BaseHasCode = v }),
    ];

    /// <summary>Snapshot properties that are dimensions of their own, not toggles.</summary>
    private static readonly string[] OwnDimensions =
    [
        nameof(LifecycleSnapshot.Mode), nameof(LifecycleSnapshot.LatestReviewDecision), nameof(LifecycleSnapshot.LatestReviewProjectVersion),
        nameof(LifecycleSnapshot.EscalationOutcome), nameof(LifecycleSnapshot.PendingGates),
    ];

    private static readonly LifecycleSnapshot AllTrue = Toggles.Aggregate(new LifecycleSnapshot(), (s, t) => t.Set(s, true));

    private static readonly LifecycleSnapshot AllFalse = Toggles.Aggregate(new LifecycleSnapshot(), (s, t) => t.Set(s, false));

    private static readonly (string Label, ReviewDecision? Decision, int? AtCheckpoint)[] Decisions =
    [
        ("no review", null, null),
        .. Enum.GetValues<ReviewDecision>().Select(d => (d.ToWire(), (ReviewDecision?)d, (int?)Checkpoint)),
        .. Enum.GetValues<ReviewDecision>().Select(d => ($"stale {d.ToWire()}", (ReviewDecision?)d, (int?)(Checkpoint - 1))),
    ];

    private static readonly EscalationDecision?[] Escalations = [null, .. Enum.GetValues<EscalationDecision>().Cast<EscalationDecision?>()];

    private static readonly string[][] PendingGateSets =
    [
        [],
        .. SdlcGates.All.Select(g => new[] { g }),
        [SdlcGates.PreRelease, EngineGates.Escalation],
        [SdlcGates.SpecApproval, SdlcGates.PlanApproval],
    ];

    private static readonly int?[] TargetCheckpoints = [null, 0, 1, Checkpoint - 1, Checkpoint, Checkpoint + 1];

    [Fact]
    public void FR291_the_toggles_cover_every_snapshot_fact()
    {
        var properties = typeof(LifecycleSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Except(OwnDimensions)
            .Order(StringComparer.Ordinal);

        Assert.Equal(properties, Toggles.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FR291_parity_over_all_combinations()
    {
        var definition = Definition;
        var differences = new ConcurrentQueue<string>();
        long count = 0;

        var states = PhaseOrder.All.SelectMany(p => Enum.GetValues<ProjectStatus>().Select(s => new LifecycleState(p, s, Checkpoint))).ToList();
        Parallel.ForEach(states, state => Interlocked.Add(ref count, StateByRequest(state, definition, differences)));

        var factStates = PhaseOrder.All
            .SelectMany(p => new[] { PhaseOrder.StatusFor(p), ProjectStatus.RolledBack }.Select(s => new LifecycleState(p, s, Checkpoint)))
            .Concat([new(Phase.Release, ProjectStatus.Released, Checkpoint), new(Phase.Release, ProjectStatus.AwaitingMerge, Checkpoint)])
            .ToList();
        Parallel.ForEach(factStates, state => Interlocked.Add(ref count, Facts(state, definition, differences)));

        count += ReviewLoopCap(definition, differences);

        Assert.True(differences.IsEmpty, $"{differences.Count} differences over {count} combinations; the first 20:{Environment.NewLine}"
            + string.Join(Environment.NewLine, differences.Take(20)));
        Assert.True(count == ExpectedCombinations, $"Compared {count} combinations; update {nameof(ExpectedCombinations)}.");
    }

    /// <summary>(1) Every request shape from <paramref name="state"/> over the "all true" and "all false" snapshots.</summary>
    private static long StateByRequest(LifecycleState state, PipelineDefinition definition, ConcurrentQueue<string> differences)
    {
        long count = 0;
        var allTrue = AllTrue with { LatestReviewDecision = ReviewDecision.ReleaseEligible, LatestReviewProjectVersion = Checkpoint, EscalationOutcome = EscalationDecision.Accept };
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            foreach (var (label, snapshot) in new[] { ("all true", allTrue with { Mode = mode }), ("all false", AllFalse with { Mode = mode }) })
            {
                foreach (var target in PhaseOrder.All.Cast<Phase?>().Append(null))
                {
                    foreach (var checkpoint in TargetCheckpoints)
                    {
                        foreach (var kind in Enum.GetValues<TransitionKind>())
                        {
                            if (IsStatusChange(kind) && target is not (Phase.Release or null))
                            {
                                continue;
                            }

                            var request = new TransitionRequest(kind, target, checkpoint);
                            count += Record(differences, LifecycleParity.Apply(state, request, snapshot, BelowCap, definition).Differences, () => $"{Describe(state)} {kind} → {Wire(target)} @{checkpoint?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {label}, {mode.ToWire()}");
                        }
                    }

                    if (target is { } entering)
                    {
                        foreach (var includeGate in new[] { false, true })
                        {
                            count += Record(differences, LifecycleParity.UnmetEntryConditions(state, entering, snapshot, includeGate, BelowCap, definition).Differences, () => $"{Describe(state)} unmet entering {entering.ToWire()} (gate {includeGate}), {label}, {mode.ToWire()}");
                        }
                    }
                }

                count += Record(differences, LifecycleParity.RequestAdvance(state, snapshot, BelowCap, definition, "given").Differences, () => $"{Describe(state)} request advance, {label}, {mode.ToWire()}");
            }
        }

        return count;
    }

    /// <summary>(2) The fact-reading operations from <paramref name="state"/> over every fact combination.</summary>
    private static long Facts(LifecycleState state, PipelineDefinition definition, ConcurrentQueue<string> differences)
    {
        long count = 0;
        foreach (var (label, snapshot) in FactSnapshots())
        {
            Check(state, snapshot, BelowCap, definition, differences, label, ref count);
        }

        return count;
    }

    /// <summary>
    /// (3) The review cycle with <c>maxReviewCycles + extraReviewCycles</c> below, equal to and above the current cycle, and
    /// after an escalation <c>continue</c> (extra + 1): the setting (D-0.7.0-30) means the machines always agree.
    /// </summary>
    private static long ReviewLoopCap(PipelineDefinition definition, ConcurrentQueue<string> differences)
    {
        long count = 0;
        var state = new LifecycleState(Phase.Review, ProjectStatus.Reviewing, Checkpoint);
        foreach (var cycle in Enumerable.Range(0, ReviewCycles + 2))
        {
            foreach (var max in new[] { cycle - 1, cycle, cycle + 1 }.Where(m => m >= 1))
            {
                foreach (var extra in new[] { 0, 1 })
                {
                    var loop = ReviewLoopState.Of(cycle, max, extra);
                    foreach (var (label, snapshot) in FactSnapshots())
                    {
                        count += Record(
                            differences,
                            LifecycleParity.Apply(state, TransitionRequest.ReviewCycle(), snapshot, loop, definition).Differences,
                            () => $"review cycle at cycle {cycle}, max {max} + extra {extra}, {label}");
                    }
                }
            }
        }

        return count;
    }

    private static void Check(LifecycleState state, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition, ConcurrentQueue<string> differences, string label, ref long count)
    {
        foreach (var request in new[]
        {
            TransitionRequest.Advance(),
            TransitionRequest.ReviewCycle(),
            TransitionRequest.NewVersionInto(Phase.Refinement),
            TransitionRequest.NewVersionInto(Phase.Analysis),
        })
        {
            count += Record(differences, LifecycleParity.Apply(state, request, snapshot, loop, definition).Differences, () => $"{Describe(state)} {request.Kind} → {Wire(request.TargetPhase)}, {label}");
        }

        count += Record(differences, LifecycleParity.RequestAdvance(state, snapshot, loop, definition).Differences, () => $"{Describe(state)} request advance, {label}");
        foreach (var entering in PhaseOrder.All)
        {
            foreach (var includeGate in new[] { false, true })
            {
                count += Record(differences, LifecycleParity.UnmetEntryConditions(state, entering, snapshot, includeGate, loop, definition).Differences, () => $"{Describe(state)} unmet entering {entering.ToWire()} (gate {includeGate}), {label}");
            }
        }
    }

    /// <summary>(2)'s snapshots: mode × toggles × review decision × escalation × pending gates.</summary>
    private static IEnumerable<(string Label, LifecycleSnapshot Snapshot)> FactSnapshots()
    {
        var bases = new List<(string, LifecycleSnapshot)> { ("all true", AllTrue), ("all false", AllFalse) };
        foreach (var (name, set) in Toggles)
        {
            bases.Add(($"all true but {name}", set(AllTrue, false)));
            bases.Add(($"all false but {name}", set(AllFalse, true)));
        }

        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            foreach (var (baseLabel, snapshot) in bases)
            {
                foreach (var (decisionLabel, decision, atCheckpoint) in Decisions)
                {
                    foreach (var escalation in Escalations)
                    {
                        foreach (var gates in PendingGateSets)
                        {
                            yield return (
                                $"{mode.ToWire()}, {baseLabel}, {decisionLabel}, escalation {escalation?.ToWire() ?? "none"}, pending [{string.Join(",", gates)}]",
                                snapshot with
                                {
                                    Mode = mode,
                                    LatestReviewDecision = decision,
                                    LatestReviewProjectVersion = atCheckpoint,
                                    EscalationOutcome = escalation,
                                    PendingGates = gates,
                                });
                        }
                    }
                }
            }
        }
    }

    private static long Record(ConcurrentQueue<string> differences, IReadOnlyList<string> found, Func<string> label)
    {
        if (found.Count > 0)
        {
            differences.Enqueue($"{label()}: {string.Join("; ", found)}");
        }

        return 1;
    }

    private static bool IsStatusChange(TransitionKind kind) =>
        kind is TransitionKind.ReleaseCompleted or TransitionKind.ReleaseAwaitingMerge or TransitionKind.ReleaseMergeAbandoned;

    private static string Describe(LifecycleState state) => $"{state.Phase.ToWire()}/{state.Status.ToWire()}";

    private static string Wire(Phase? phase) => phase?.ToWire() ?? "-";
}

/// <summary>FR-291: the combinatorial parity test runs alone (after the parallel tests), so its CPU use can't slow others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParityRunsAlone
{
    public const string Name = "FR-291 parity";
}
