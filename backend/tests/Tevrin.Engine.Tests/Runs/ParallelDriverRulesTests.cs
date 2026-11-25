using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-343, §4.64: the run driver's parallel-group decisions (entering, branch order, join, fail vs continue), on
/// <c>fixture-parallel</c> (branch 0: bull → bull_review; branch 1: bear).</summary>
public class ParallelDriverRulesTests
{
    private static readonly Guid RunId = Guid.CreateVersion7();
    private static readonly DateTimeOffset T = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static PipelineDefinition Definition()
    {
        var registry = new PipelineRegistry([new FlowFixturePack(FlowFixturePack.Parallel)], [], Array.Empty<JobHandlerRegistration>());
        Assert.True(registry.TryGet(FlowFixturePack.Parallel, out var loaded), string.Join("; ", registry.Errors.Select(e => e.Error.ToString())));
        return loaded.Definition!;
    }

    private static ParallelDefinition Group(BranchFailurePolicy policy = BranchFailurePolicy.Fail) =>
        Definition().FindStage("analysts")!.Parallel! with { OnBranchFailure = policy };

    private static StageExecution GroupEntry() => new() { PipelineRunId = RunId, StageId = "analysts", EnteredAt = T };

    private static StageExecution Branch(string stage, string branch, int at, int? exitedAt = null, string? outcome = null) => new()
    {
        PipelineRunId = RunId,
        StageId = stage,
        Branch = branch,
        EnteredAt = T.AddSeconds(at),
        ExitedAt = exitedAt is { } x ? T.AddSeconds(x) : null,
        Outcome = outcome,
    };

    [Fact]
    public void FR343_entering_a_group_starts_every_branchs_first_stage_with_the_branch_index_as_its_id()
    {
        Assert.Equal([("0", "bull"), ("1", "bear")], RunDriverRules.EnterGroup(Group()));
        Assert.NotNull(Definition().FindAnyStage("bull_review"));
        Assert.Null(Definition().FindStage("bull_review"));
    }

    [Fact]
    public void FR343_a_branch_runs_its_stages_in_order_and_ends_after_its_last()
    {
        Assert.Equal(new BranchNext.Start("bull_review"), RunDriverRules.AfterBranchStage(Group(), "bull", groupFailed: false));
        Assert.IsType<BranchNext.Ended>(RunDriverRules.AfterBranchStage(Group(), "bull_review", groupFailed: false));
        Assert.IsType<BranchNext.Ended>(RunDriverRules.AfterBranchStage(Group(), "bear", groupFailed: false));
    }

    [Fact]
    public void FR343_a_branch_execution_belongs_to_the_group_only_under_its_own_branch_id()
    {
        Assert.True(RunDriverRules.InGroup(Group(), Branch("bull_review", "0", 1)));
        Assert.False(RunDriverRules.InGroup(Group(), Branch("bull_review", "1", 1)));
        Assert.False(RunDriverRules.InGroup(Group(), Branch("decide", "0", 1)));
    }

    [Fact]
    public void FR343_a_stale_check_lets_a_branch_execution_through_while_the_run_is_in_the_group()
    {
        var run = new PipelineRun { Id = RunId, CurrentStage = "analysts", Status = GenericRunStatus.Running };

        Assert.False(RunDriverRules.IsStale(run, Branch("bear", "1", 1)));
        Assert.True(RunDriverRules.IsStale(run, Branch("bear", "1", 1, exitedAt: 2, outcome: BranchOutcomes.Completed)));
        Assert.True(RunDriverRules.IsStale(run, new StageExecution { PipelineRunId = RunId, StageId = "decide", EnteredAt = T }));
    }

    [Fact]
    public void FR343_the_join_waits_for_every_branchs_last_stage()
    {
        var group = Group();
        var executions = new List<StageExecution>
        {
            Branch("bull", "0", 0, 2, PipelineTransitionKinds.Advance),
            Branch("bull_review", "0", 2, 3, BranchOutcomes.Completed),
            Branch("bear", "1", 0),
        };

        var states = RunDriverRules.BranchStates(group, GroupEntry(), executions);

        Assert.Equal([BranchState.Completed, BranchState.Running], states);
        Assert.IsType<JoinState.Pending>(RunDriverRules.Join(group, states));
    }

    [Fact]
    public void FR343_a_branch_closed_on_an_earlier_stage_is_not_complete()
    {
        var states = RunDriverRules.BranchStates(
            Group(), GroupEntry(), [Branch("bull", "0", 0, 2, BranchOutcomes.Completed), Branch("bear", "1", 0, 1, BranchOutcomes.Completed)]);

        Assert.Equal([BranchState.Running, BranchState.Completed], states);
    }

    [Fact]
    public void FR343_executions_of_an_earlier_entry_of_the_group_are_ignored()
    {
        var old = Branch("bear", "1", -10, -5, BranchOutcomes.Completed);

        Assert.Equal([BranchState.Running, BranchState.Running], RunDriverRules.BranchStates(Group(), GroupEntry(), [old]));
    }

    /// <summary>§4.64 example: branch A finishes at t+3 and branch B at t+7, so the join is at t+7.</summary>
    [Fact]
    public void FR343_section_4_64_the_join_is_when_the_last_branch_finishes()
    {
        var group = Group();
        var timeline = new List<StageExecution>
        {
            Branch("bull", "0", 0, 1, PipelineTransitionKinds.Advance),
            Branch("bull_review", "0", 1, 3, BranchOutcomes.Completed),
            Branch("bear", "1", 0, 7, BranchOutcomes.Completed),
        };

        DateTimeOffset? joinedAt = null;
        foreach (var t in Enumerable.Range(0, 10).Select(s => T.AddSeconds(s)))
        {
            var seen = timeline.Select(e => e.ExitedAt <= t ? e : Branch(e.StageId, e.Branch!, (int)(e.EnteredAt - T).TotalSeconds))
                .Where(e => e.EnteredAt <= t)
                .ToList();
            if (joinedAt is null && RunDriverRules.Join(group, RunDriverRules.BranchStates(group, GroupEntry(), seen)) is JoinState.Complete)
            {
                joinedAt = t;
            }
        }

        Assert.Equal(T.AddSeconds(7), joinedAt);
    }

    /// <summary>§4.64 example: if A fails at t+2, the group fails at t+2, B's current stage finishes, and B's next stage
    /// doesn't start (here branch 1 is the failed A and branch 0 the B with a next stage).</summary>
    [Fact]
    public void FR343_section_4_64_with_fail_a_failed_branch_holds_back_the_others_next_stage()
    {
        var group = Group(BranchFailurePolicy.Fail);

        Assert.Equal(new BranchNext.Halt("bull_review"), RunDriverRules.AfterBranchStage(group, "bull", groupFailed: true));
        Assert.False(RunDriverRules.BranchFailureEndsBranch(group));

        // The failed branch's stage stays open for a retry, so the group never joins.
        var states = RunDriverRules.BranchStates(group, GroupEntry(), [Branch("bull", "0", 0, 3, PipelineTransitionKinds.Advance), Branch("bull_review", "0", 3), Branch("bear", "1", 0)]);
        Assert.IsType<JoinState.Pending>(RunDriverRules.Join(group, states));
    }

    [Fact]
    public void FR343_with_fail_a_branch_marked_failed_never_completes_the_join() =>
        Assert.IsType<JoinState.Pending>(RunDriverRules.Join(Group(BranchFailurePolicy.Fail), [BranchState.Completed, BranchState.Failed]));

    [Fact]
    public void FR343_with_continue_the_join_completes_with_the_failed_branches_marked()
    {
        var group = Group(BranchFailurePolicy.Continue);
        var states = RunDriverRules.BranchStates(group, GroupEntry(), [
            Branch("bull", "0", 0, 2, BranchOutcomes.Failed),
            Branch("bear", "1", 0, 4, BranchOutcomes.Completed),
        ]);

        Assert.True(RunDriverRules.BranchFailureEndsBranch(group));
        Assert.Equal([BranchState.Failed, BranchState.Completed], states);
        var complete = Assert.IsType<JoinState.Complete>(RunDriverRules.Join(group, states));
        Assert.Equal(["0"], complete.FailedBranches);
        Assert.Equal(new BranchNext.Start("bull_review"), RunDriverRules.AfterBranchStage(group, "bull", groupFailed: true));
    }

    [Fact]
    public void FR343_with_continue_the_join_still_waits_for_a_running_branch() =>
        Assert.IsType<JoinState.Pending>(RunDriverRules.Join(Group(BranchFailurePolicy.Continue), [BranchState.Failed, BranchState.Running]));

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    public void FR335_FR343_an_input_queues_the_group_job_even_while_a_sibling_branch_runs(bool inGroup, bool anyActive, bool groupJobQueued, bool queues) =>
        Assert.Equal(queues, RunDriverRules.ResumeOnInput(inGroup, anyActive, groupJobQueued));

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, JobStatus.Succeeded, true)]
    [InlineData(false, JobStatus.Failed, false)]
    [InlineData(false, JobStatus.Dead, false)]
    [InlineData(true, JobStatus.Failed, true)]
    [InlineData(true, JobStatus.Dead, true)]
    public void FR333_FR343_only_a_retry_resumes_a_branch_whose_last_job_failed(bool retry, JobStatus? lastJob, bool resumes) =>
        Assert.Equal(resumes, RunDriverRules.ResumeBranch(retry, lastJob));
}
