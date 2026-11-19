using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-296, §4.55: validate and retry with feedback.</summary>
public class ValidateRetryTests
{
    private const string Volume = "weekly volume +25% > 10%";
    private const string Taper = "taper missing";

    private static ScriptedResponder WorkedExampleChecker() => new(
        "checker",
        $$"""{"consistent":false,"findings":["{{Volume}}"]}""",
        $$"""{"consistent":false,"findings":["{{Volume}}"]}""",
        $$"""{"consistent":false,"findings":["{{Taper}}"]}""");

    [Fact]
    public async Task FR296_worked_example_three_runs_two_with_feedback_then_one_pending_escalation_gate()
    {
        var checker = WorkedExampleChecker();
        using var harness = new AgentStageHarness([checker]);

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Escalated, result.End);
        Assert.Equal(3, harness.Store.AgentRuns.Count);
        Assert.Equal(3, checker.Prompts.Count);
        Assert.DoesNotContain(Volume, checker.Prompts[0], StringComparison.Ordinal);
        Assert.Contains(Volume, checker.Prompts[1], StringComparison.Ordinal);
        Assert.Contains(Volume, checker.Prompts[2], StringComparison.Ordinal);
        Assert.Contains(AgentStagePrompt.FeedbackHeading, checker.Prompts[1], StringComparison.Ordinal);
        Assert.Equal([null, Volume, Volume], harness.Store.AgentRuns.Select(r => r.UserFeedback));
        Assert.Equal(Taper, result.Findings);

        var gate = Assert.Single(harness.Store.Gates);
        Assert.Same(gate, result.Gate);
        Assert.Equal((EngineGates.Escalation, GateStatus.Pending, harness.Run.Id), (gate.Type, gate.Status, gate.PipelineRunId));
        Assert.Empty(harness.Store.Artifacts);
        Assert.All(harness.Store.AgentRuns, r => Assert.Equal(AgentRunStatus.Succeeded, r.Status));
        result.ThrowIfFailed();
    }

    [Fact]
    public async Task FR296_with_then_fail_the_third_agent_run_is_failed_and_the_job_fails()
    {
        using var harness = new AgentStageHarness(
            [WorkedExampleChecker()],
            yaml => yaml.Replace("then: escalate", "then: fail", StringComparison.Ordinal));

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Failed, result.End);
        Assert.Equal(
            [AgentRunStatus.Succeeded, AgentRunStatus.Succeeded, AgentRunStatus.Failed],
            harness.Store.AgentRuns.Select(r => r.Status));
        Assert.Equal(StageValidationRules.RejectedError(Taper), harness.Store.AgentRuns[2].Error);
        Assert.Empty(harness.Store.Gates);
        var ex = Assert.Throws<JobFailedException>(result.ThrowIfFailed);
        Assert.Equal((false, EngineErrorCodes.Validation), (ex.Transient, ex.ErrorCode));
        Assert.Contains(Taper, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR296_a_valid_retry_stores_the_output()
    {
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("checker", $$"""{"consistent":false,"findings":["{{Volume}}"]}""", AgentStageHarness.ValidReport)]);

        var result = await harness.RunStageAsync("check");

        Assert.Equal(AgentStageEnd.Stored, result.End);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(result.Attempts[1].AgentRun.Id, Assert.Single(harness.Store.Artifacts).AgentRunId);
    }

    [Fact]
    public async Task FR296_an_existing_pending_escalation_gate_is_reused()
    {
        using var harness = new AgentStageHarness([WorkedExampleChecker()]);
        var existing = new ApprovalGate { ProjectId = harness.Run.ProjectId, PipelineRunId = harness.Run.Id, Type = EngineGates.Escalation };
        harness.Store.Gates.Add(existing);

        var result = await harness.RunStageAsync("check");

        Assert.Same(existing, result.Gate);
        Assert.Single(harness.Store.Gates);
        Assert.DoesNotContain(harness.Hooks.Calls, c => c.StartsWith("gate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR296_a_pending_escalation_gate_of_another_run_is_not_reused()
    {
        using var harness = new AgentStageHarness([WorkedExampleChecker()]);
        var other = new ApprovalGate { ProjectId = harness.Run.ProjectId, PipelineRunId = Guid.CreateVersion7(), Type = EngineGates.Escalation };
        harness.Store.Gates.Add(other);

        var result = await harness.RunStageAsync("check");

        Assert.NotSame(other, result.Gate);
        Assert.Equal(harness.Run.Id, result.Gate!.PipelineRunId);
        Assert.Contains($"gate:escalation:{Taper}", harness.Hooks.Calls);
    }

    [Fact]
    public async Task FR296_retry_feedback_keeps_the_original_feedback_and_appends_the_findings()
    {
        var checker = WorkedExampleChecker();
        using var harness = new AgentStageHarness([checker]);

        await harness.RunStageAsync("check", JobTriggers.GateRejected, "Too vague");

        Assert.All(checker.Prompts, p => Assert.Contains("Too vague", p, StringComparison.Ordinal));
        Assert.Equal(["Too vague", $"Too vague\n\n{Volume}", $"Too vague\n\n{Volume}"], harness.Store.AgentRuns.Select(r => r.UserFeedback));
    }

    [Fact]
    public async Task FR296_every_attempt_records_its_output_and_rejected_ones_are_marked()
    {
        using var harness = new AgentStageHarness([WorkedExampleChecker()]);

        await harness.RunStageAsync("check");

        Assert.All(harness.Store.AgentRuns, r =>
        {
            Assert.Contains("\"stopReason\":\"end_turn\"", r.Output, StringComparison.Ordinal);
            Assert.StartsWith(StageValidationRules.RejectedErrorPrefix, r.Error, StringComparison.Ordinal);
        });
        Assert.Contains("taper missing", harness.Store.AgentRuns[2].Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 3, InvalidOutcome.Escalate, 1, InvalidOutputAction.Retry)]
    [InlineData(true, 3, InvalidOutcome.Escalate, 2, InvalidOutputAction.Retry)]
    [InlineData(true, 3, InvalidOutcome.Escalate, 3, InvalidOutputAction.Escalate)]
    [InlineData(true, 3, InvalidOutcome.Fail, 3, InvalidOutputAction.Fail)]
    [InlineData(false, 3, InvalidOutcome.Escalate, 1, InvalidOutputAction.Escalate)]
    [InlineData(true, 1, InvalidOutcome.Fail, 1, InvalidOutputAction.Fail)]
    public void FR296_on_invalid_retries_below_max_attempts_then_follows_then(bool retry, int max, InvalidOutcome then, int attempt, InvalidOutputAction expected) =>
        Assert.Equal(expected, StageValidationRules.OnInvalid(new OnInvalidPolicy(retry, max, then), attempt));

    [Fact]
    public void FR296_without_on_invalid_an_invalid_output_fails() =>
        Assert.Equal(InvalidOutputAction.Fail, StageValidationRules.OnInvalid(null, 1));

    [Fact]
    public void FR296_findings_are_guard_reasons_then_schema_errors_with_their_path() =>
        Assert.Equal(
            "a\nb\n/items/0: Value is \"integer\" but should be \"string\"",
            StageValidationRules.Findings(["a", " ", "b"], [new SchemaFinding("/items/0", "Value is \"integer\" but should be \"string\"")]));

    [Fact]
    public void FR296_findings_are_capped_at_8000_characters()
    {
        var findings = StageValidationRules.Findings([new string('x', 5000), new string('y', 5000)], []);

        Assert.Equal(StageValidationRules.MaxFindingsChars, findings.Length);
        Assert.EndsWith("y…", findings, StringComparison.Ordinal);
        Assert.Equal("short", StageValidationRules.Cap("short"));
    }

    [Fact]
    public void FR296_the_cap_never_splits_a_surrogate_pair()
    {
        var text = new string('x', StageValidationRules.MaxFindingsChars - 2) + "😀😀";

        var capped = StageValidationRules.Cap(text);

        Assert.Equal(StageValidationRules.MaxFindingsChars - 1, capped.Length);
        Assert.False(char.IsHighSurrogate(capped[^2]));
    }
}
