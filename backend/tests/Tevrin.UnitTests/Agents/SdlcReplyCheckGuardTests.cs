using Tevrin.Api.Agents;
using Tevrin.Domain.Common;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;
using Tevrin.UnitTests.Lifecycle;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// FR-314, D-0.7.0-44: the SDLC output guards (<see cref="SdlcReplyCheckGuard"/>) apply the attempt's own reply check to
/// the reply as sent, are no entry rules, and are registered and named like <c>sdlc.plan-valid</c>.
/// </summary>
public class SdlcReplyCheckGuardTests
{
    private const string Reply = "  not what the check wants  ";

    private static readonly DomainError Rejected = new(ErrorCodes.MissingSections, "Missing sections: Overview.");

    private static readonly ArtifactAgentSpec Spec = new(
        SdlcArtifacts.TechnicalSpecification,
        text => text == Reply ? Result.Failure<string>(Rejected) : Result.Success(text),
        (_, _) => throw new InvalidOperationException("Not re-run here."))
    {
        CorrectiveToContent = Result.Success,
    };

    private static GuardContext Context(FactBag facts) => new(Guid.CreateVersion7(), Guid.CreateVersion7(), "specification", ApprovalMode.Advisory, facts);

    private static FactBag Output(object? hostState, int attempt = 1) => FactBag.Empty
        .With(new StageOutputFact(SdlcArtifacts.TechnicalSpecification, ArtifactFormat.Markdown, Reply.Trim()))
        .With(new StageAttemptFact(attempt, Reply, hostState));

    private static SdlcAgentStageCall Call(ArtifactAgentSpec spec) =>
        new(new AgentRunRequest { ProjectId = Guid.CreateVersion7(), Agent = SdlcAgents.SpecGenerator, Job = SdlcAgentStageHarness.Job() }, spec);

    [Fact]
    public async Task FR314_the_guard_gives_the_attempts_check_message_on_the_reply_as_sent_as_its_one_reason()
    {
        var result = await new SdlcReplyCheckGuard().EvaluateAsync(Context(Output(Call(Spec))), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal([Rejected.Message], result.Reasons);
    }

    [Fact]
    public async Task FR314_the_corrective_attempt_is_checked_with_the_jobs_corrective_check()
    {
        var result = await new SdlcReplyCheckGuard().EvaluateAsync(Context(Output(Call(Spec.ForCorrective()), attempt: 2)), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task FR314_a_lifecycle_evaluation_passes_and_an_output_without_the_SDLC_request_is_rejected()
    {
        var guard = new SdlcReplyCheckGuard();
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await guard.EvaluateAsync(Context(FactBag.Empty), ct)).Ok);
        var rejected = await guard.EvaluateAsync(Context(Output(hostState: null)), ct);
        Assert.False(rejected.Ok);
        Assert.Equal([SdlcReplyCheckGuard.NoRequestReason], rejected.Reasons);
    }

    [Fact]
    public void FR314_every_output_guard_is_an_SDLC_guard_with_no_lifecycle_condition_and_one_per_checked_agent()
    {
        foreach (var name in SdlcGuardNames.ReplyChecks.Append(SdlcGuardNames.PlanValid))
        {
            Assert.Contains(name, SdlcGuardNames.All);
            Assert.Empty(SdlcGuards.Evaluate(name, null!, null!));
        }

        Assert.Equal(
            [SdlcAgents.IdeaRefinement, SdlcAgents.SpecGenerator, SdlcAgents.PlanGenerator, SdlcAgents.CodeReview, SdlcAgents.RepositoryAnalyst, SdlcAgents.StackAnalyst, SdlcAgents.BuildFix],
            SdlcAgents.All.Where(SdlcGuardNames.OutputCheckOf.ContainsKey));
        Assert.Equal(SdlcGuardNames.OutputCheckOf.Values.Order(StringComparer.Ordinal), SdlcGuardNames.ReplyChecks.Append(SdlcGuardNames.PlanValid).Order(StringComparer.Ordinal));
        Assert.Equal(
            SdlcOutputGuardFixture.Checks.Select(c => c.Guard).Order(StringComparer.Ordinal),
            SdlcGuardNames.OutputCheckOf.Values.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FR314_D_0_7_0_44_version_3_declares_each_output_check_with_0_6_1s_one_corrective_rerun_and_nothing_else()
    {
        var definition = SdlcPipelineFixture.Definition;
        Assert.Equal(3, definition.Version);
        foreach (var stage in definition.Stages)
        {
            var own = SdlcOutputGuardFixture.Checks.Where(c => c.Stage == stage.Id && c.Binding is null).Select(c => c.Guard).ToList();
            Assert.Equal(own, stage.Validate.Select(v => v.Guard));
            Assert.Equal(own.Count == 0 ? null : SdlcOutputGuardFixture.CorrectiveOnce, stage.OnInvalid);
            foreach (var binding in stage.Agents)
            {
                var bound = SdlcOutputGuardFixture.Checks.Where(c => c.Stage == stage.Id && c.Binding == binding.Key).Select(c => c.Guard).ToList();
                Assert.Equal(bound, binding.Validate.Select(v => v.Guard));
                Assert.Equal(bound.Count == 0 ? null : SdlcOutputGuardFixture.CorrectiveOnce, binding.OnInvalid);
            }
        }
    }

    [Fact]
    public void FR321_D_0_7_0_44_version_3_without_its_additions_is_version_2_by_hash() =>
        Assert.Equal(SdlcRunRepin.Version2Hash, DefinitionHash.Compute(SdlcOutputGuardFixture.Version2(SdlcPipelineFixture.Definition)));

    [Fact]
    public void D_0_7_0_43_execution_declares_its_stage_handlers_and_stays_a_manual_stage()
    {
        var execution = SdlcPipelineFixture.Definition.FindStage("execution")!;

        Assert.Equal([SdlcStageHandlerNames.Execution, SdlcStageHandlerNames.BuildCheck], execution.Handlers);
        Assert.Equal((null, StageKind.Manual), (execution.Handler, execution.Kind));
        Assert.Same(execution, StageHandlers.StageOf(SdlcPipelineFixture.Definition, SdlcStageHandlerNames.Execution));
        Assert.Same(execution, StageHandlers.StageOf(SdlcPipelineFixture.Definition, SdlcStageHandlerNames.BuildCheck));
    }
}
