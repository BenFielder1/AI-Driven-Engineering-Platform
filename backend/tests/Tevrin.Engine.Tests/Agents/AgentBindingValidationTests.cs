using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// D-0.7.0-44, FR-314: an <c>agents:</c> binding's own <c>validate:</c> and <c>on_invalid:</c> check its output as a
/// stage's do, and a <c>validate:</c> guard sees the attempt (<see cref="StageAttemptFact"/>: its number, the reply as
/// sent, the request's host state) beside the output.
/// </summary>
public class AgentBindingValidationTests
{
    private const string AttemptGuard = "fixture.attempt";

    private const string Inconsistent = """{"consistent":false,"findings":["the totals differ"]}""";

    private const string Bindings = """
          - id: check
            agents:
              - { key: auditor, job_type: stage.run, prompt: fixture.check, inputs: [draft], output: summary, validate: [{ guard: fixture.report-consistent }, { guard: fixture.attempt }], on_invalid: { retry_with_feedback: true, max_attempts: 2, then: fail } }

        """;

    private static string AddBindings(string yaml)
    {
        Assert.Contains("  - id: check\n", yaml, StringComparison.Ordinal);
        return yaml.Replace("  - id: check\n", Bindings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D_0_7_0_44_a_bindings_validate_and_on_invalid_check_its_output_and_fail_after_its_attempts()
    {
        var recorder = new AttemptRecorder();
        using var harness = new AgentStageHarness(
            [new ScriptedResponder("drafter", AgentStageHarness.ValidDraft), new ScriptedResponder("auditor", Inconsistent)],
            AddBindings,
            configure: services => services.AddSingleton(recorder).AddPipelineGuard<RecordingAttemptGuard>(AttemptGuard));
        await harness.RunStageAsync("draft");
        var state = new object();

        var result = await harness.Coordinator.RunAsync(
            new AgentStageRequest(harness.Run.Id, "check", JobTriggers.StageEntered) { AgentKey = "auditor", HostState = state },
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentStageEnd.Failed, result.End);
        Assert.Equal(["the totals differ", "the totals differ"], result.Attempts.Select(a => a.Findings));
        Assert.Equal(AgentRunStatus.Failed, harness.Store.AgentRuns[^1].Status);
        Assert.DoesNotContain(harness.Store.Artifacts, a => a.Type == "summary");
        Assert.Equal([1, 2], recorder.Seen.Select(f => f.Attempt));
        Assert.All(recorder.Seen, f => Assert.Equal(Inconsistent, f.Reply));
        Assert.Same(state, recorder.Seen[0].HostState);
    }

    [Fact]
    public void D_0_7_0_44_the_binding_view_carries_its_own_checks_and_a_binding_without_them_none()
    {
        var parsed = PipelineDefinitionLoader.Parse(AddBindings(Fixtures.Read("fixture-review.pipeline.yaml")));
        Assert.Empty(parsed.Errors);
        var check = parsed.Definition!.FindStage("check")!;

        var view = check.ForAgent("auditor")!;

        Assert.Equal([FixturePack.ReportConsistentGuard, AttemptGuard], view.Validate.Select(v => v.Guard));
        Assert.Equal(new OnInvalidPolicy(RetryWithFeedback: true, MaxAttempts: 2, Then: InvalidOutcome.Fail), view.OnInvalid);
        Assert.Equal("summary", view.Output);
        Assert.Same(check, check.ForAgent(null));
    }

    [Fact]
    public void D_0_7_0_44_a_bindings_checks_are_hashed_only_when_set_and_read_back_to_the_same_hash()
    {
        var definition = PipelineDefinitionLoader.Parse(AddBindings(Fixtures.Read("fixture-review.pipeline.yaml"))).Definition!;
        var json = DefinitionHash.CanonicalJson(definition);

        Assert.Contains("\"on_invalid\":{\"max_attempts\":2,\"retry_with_feedback\":true,\"then\":\"fail\"}", json, StringComparison.Ordinal);
        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(json, DefinitionHash.CanonicalJson(reread.Definition!));
        var unchecked_ = definition with
        {
            Stages = [.. definition.Stages.Select(s => s with { Agents = [.. s.Agents.Select(b => b with { Validate = [], OnInvalid = null })] })],
        };
        Assert.DoesNotContain("\"validate\":[{\"guard\":\"fixture.attempt\"}", DefinitionHash.CanonicalJson(unchecked_), StringComparison.Ordinal);
        Assert.NotEqual(DefinitionHash.Compute(definition), DefinitionHash.Compute(unchecked_));
    }

    [Fact]
    public void D_0_7_0_44_the_validator_checks_a_bindings_guards_like_a_stages()
    {
        var yaml = AddBindings(Fixtures.Read("fixture-review.pipeline.yaml")).Replace("{ guard: fixture.attempt }", "{ guard: fixture.missing }", StringComparison.Ordinal);

        var errors = PipelineDefinitionLoader.ParseAndValidate(yaml, FixturePack.Catalog).Errors;

        Assert.Contains(errors, e => e.Code == DefinitionErrorCodes.UnknownGuard && e.Path.EndsWith(".agents[0].validate[1].guard", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Path.EndsWith(".agents[0].validate[0].guard", StringComparison.Ordinal));
    }

    /// <summary>The attempts <see cref="RecordingAttemptGuard"/> saw.</summary>
    private sealed class AttemptRecorder
    {
        public List<StageAttemptFact> Seen { get; } = [];
    }

    /// <summary>Records the <see cref="StageAttemptFact"/> of each output it checks; always passes.</summary>
    private sealed class RecordingAttemptGuard(AttemptRecorder recorder) : IGuard
    {
        public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
        {
            if (context.Facts.Get<StageAttemptFact>() is { } attempt)
            {
                recorder.Seen.Add(attempt);
            }

            return Task.FromResult(GuardResult.Pass);
        }
    }
}
