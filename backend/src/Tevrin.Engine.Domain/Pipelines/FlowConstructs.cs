namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-343 (0.8.0, §4.49 amended): <c>parallel: { id, branches: [[&lt;stage&gt;…], …], join: all, on_branch_failure }</c> on a
/// stage entry makes it a parallel group: each branch is a list of ordinary stages run in order, and the branches run at
/// once. The group's id is the entry's <c>id</c> (<c>parallel.id</c>, when given, must match it). Branch stages aren't in
/// <see cref="PipelineDefinition.Stages"/>; <see cref="PipelineDefinition.AllStages"/> lists them too.
/// </summary>
public sealed record ParallelDefinition(IReadOnlyList<IReadOnlyList<StageDefinition>> Branches)
{
    public const int MinBranches = 2;

    /// <summary>§4.64: the only join, <c>all</c>.</summary>
    public ParallelJoin Join { get; init; } = ParallelJoin.All;

    public BranchFailurePolicy OnBranchFailure { get; init; } = BranchFailurePolicy.Fail;
}

public enum ParallelJoin
{
    /// <summary>The group completes when every branch's last stage completes.</summary>
    All = 0,
}

public enum BranchFailurePolicy
{
    /// <summary>A failed branch fails the group (§4.64).</summary>
    Fail = 0,

    /// <summary>The join completes with the failed branches marked.</summary>
    Continue = 1,
}

/// <summary>FR-344: one <c>veto:</c> entry, <c>{ guard: &lt;pack&gt;.&lt;name&gt; }</c> or <c>{ agent: &lt;key&gt; }</c>
/// (an agent the stage binds in <c>agents:</c>). Exactly one is set.</summary>
public sealed record VetoDefinition(string? Guard, string? Agent);

/// <summary>FR-344: <c>on_veto: { to: &lt;earlier stage&gt; }</c> or <c>on_veto: { end: vetoed }</c>
/// (<see cref="To"/> <c>null</c>).</summary>
public sealed record OnVetoDefinition(string? To)
{
    /// <summary>The only <c>end:</c> value.</summary>
    public const string Vetoed = "vetoed";

    public static OnVetoDefinition EndVetoed { get; } = new((string?)null);
}

/// <summary>FR-345: <c>fan_out: { agents: [&lt;key&gt;…] }</c> (one branch per agent; <see cref="Count"/> <c>null</c>) or
/// <c>fan_out: { count: n }</c> (the stage's agent, n samples; <see cref="Agents"/> empty).</summary>
public sealed record FanOutDefinition(IReadOnlyList<string> Agents, int? Count)
{
    public const int MinBranches = 2;
    public const int MaxBranches = 10;
}

/// <summary>FR-346: <c>aggregate: { from: &lt;fan-out stage or parallel group id&gt;, disagreement: { guard } }</c>.</summary>
public sealed record AggregateDefinition(string From, string? DisagreementGuard);

/// <summary>FR-348: pipeline-level <c>limits: { revisions: n, then: escalate | fail }</c> (§4.66).</summary>
public sealed record RunLimits(int Revisions, InvalidOutcome Then)
{
    public const int MinRevisions = 1;
    public const int MaxRevisions = 100;
}

/// <summary>FR-349: <c>variants: { count: n, select: gate | { agent: &lt;key&gt; } }</c>; <see cref="SelectAgent"/>
/// <c>null</c> means <c>select: gate</c> (a <c>variant_select</c> gate, §4.67).</summary>
public sealed record VariantsDefinition(int Count, string? SelectAgent)
{
    public const int MinCount = 2;
    public const int MaxCount = 5;

    /// <summary>The gate type <c>select: gate</c> raises.</summary>
    public const string SelectGateType = "variant_select";
}
