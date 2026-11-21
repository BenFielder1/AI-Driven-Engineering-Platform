using System.Text.Json;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// D-0.7.0-42, FR-287, FR-288, §4.54: secondary agents never affect stages, transitions, gates, cascade or stage states.
/// Every stage of the fixture gains a binding (reading and writing artifacts its stage doesn't), and every state machine,
/// cascade and stage view result is the same as without them.
/// </summary>
public class AgentBindingStateMachineTests
{
    private static readonly PipelineDefinition Plain = Fixtures.Load("fixture-review.pipeline.yaml");

    private static readonly PipelineDefinition Bound = LoadBound();

    private static PipelineDefinition LoadBound()
    {
        var yaml = Fixtures.Read("fixture-review.pipeline.yaml");
        string[] stageIds = ["draft", "check", "summarise"];
        foreach (var (id, n) in stageIds.Select((id, n) => (id, n)))
        {
            yaml = yaml.Replace(
                $"  - id: {id}\n",
                $"  - id: {id}\n    agents:\n      - {{ key: extra_{id}, job_type: {JobType(n)}, inputs: [summary], output: report, tools: [fixture.lookup] }}\n",
                StringComparison.Ordinal);
        }

        var parsed = PipelineDefinitionLoader.ParseAndValidate(yaml, FixturePack.Catalog with { JobTypes = ["fixture.extra"] });
        Assert.Empty(parsed.Errors);
        Assert.All(parsed.Definition!.Stages, s => Assert.Single(s.Agents));
        return parsed.Definition;
    }

    private static string JobType(int n) => n switch
    {
        0 => EngineJobTypes.StageRun,
        1 => EngineJobTypes.GateTimer,
        _ => "fixture.extra",
    };

    private static RunState At(string stage, string status, int checkIterations = 1) =>
        new(stage, status, 3, new Dictionary<string, int>(StringComparer.Ordinal) { ["check"] = checkIterations });

    private static RunFacts Facts(ApprovalMode mode, string[] locked, bool gateApproved = false, string[]? pending = null) => new()
    {
        Mode = mode,
        LockedArtifacts = locked,
        PendingGates = pending ?? [],
        EntryGateApproved = gateApproved,
        Guards = new Dictionary<string, GuardResult>(StringComparer.Ordinal) { [FixturePack.ReportConsistentGuard] = GuardResult.Pass },
        Settings = new Dictionary<string, int>(StringComparer.Ordinal) { [FixturePack.RedraftCycles] = 2 },
    };

    /// <summary>Every result the state machine, cascade and stage views give for <paramref name="d"/>, as JSON.</summary>
    private static List<string> Results(PipelineDefinition d)
    {
        var results = new List<object?>
        {
            PipelineStateMachine.Start(d, "draft"),
            Outcome(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(ApprovalMode.Advisory, []), d)),
            Outcome(PipelineStateMachine.Apply(At("draft", "drafting"), Transition.Advance(), Facts(ApprovalMode.Advisory, ["draft"]), d)),
            Outcome(PipelineStateMachine.Apply(At("check", "checking"), Transition.Loop("needs_redraft"), Facts(ApprovalMode.Advisory, []), d)),
            Outcome(PipelineStateMachine.Apply(At("check", "checking", 3), Transition.Loop("needs_redraft"), Facts(ApprovalMode.Advisory, []), d)),
            Outcome(PipelineStateMachine.Apply(At("check", "checking"), Transition.Advance(), Facts(ApprovalMode.Strict, ["report"]), d)),
            Outcome(PipelineStateMachine.RequestAdvance(At("check", "checking"), Facts(ApprovalMode.Strict, ["report"]), d)),
            Outcome(PipelineStateMachine.RequestAdvance(At("check", "checking"), Facts(ApprovalMode.Strict, ["report"], pending: ["report_approval"]), d)),
            Outcome(PipelineStateMachine.RequestAdvance(At("check", "checking"), Facts(ApprovalMode.Strict, ["report"], gateApproved: true), d)),
            Outcome(PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Change("summary_published"), Facts(ApprovalMode.Advisory, []), d)),
            Outcome(PipelineStateMachine.Apply(At("summarise", "summarising"), Transition.Rollback("draft", 1), Facts(ApprovalMode.Advisory, []), d)),
            PipelineStateMachine.UnmetEntryConditions(At("check", "checking"), "summarise", Facts(ApprovalMode.Strict, []), d),
            DefinitionCascade.Effect("draft", "summarise", d),
            DefinitionCascade.Effect("report", "summarise", d),
            DefinitionCascade.Downstream("summary", d),
            DefinitionCascade.Downstream("report", d),
            DefinitionCascade.ProducingStage("report", d),
            DefinitionCascade.ProducingStage("summary", d),
            RunViewRules.Stages(d, "check", false, [], [], DateTimeOffset.UnixEpoch),
            d.Stages.Select(s => (s.Id, s.Kind, d.SuccessorOf(s.Id))).ToList(),
        };
        return [.. results.Select(r => JsonSerializer.Serialize(r, r?.GetType() ?? typeof(object), Json))];
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    private static object? Outcome<T>(Result<T> result) => result.IsSuccess ? result.Value : result.Error;

    [Fact]
    public void D_0_7_0_42_secondary_agents_leave_every_state_machine_cascade_and_stage_view_result_unchanged()
    {
        Assert.NotEqual(DefinitionHash.Compute(Plain), DefinitionHash.Compute(Bound));
        Assert.Equal(Results(Plain), Results(Bound));
    }

    [Fact]
    public void D_0_7_0_42_a_stage_with_only_secondary_agents_stays_a_manual_stage()
    {
        var manual = new StageDefinition { Id = "x", Agents = [new AgentBindingDefinition("a", "stage.run", null, [], null, [])] };

        Assert.Equal(StageKind.Manual, manual.Kind);
        Assert.Null(manual.ForAgent(null));
        Assert.Equal("a", manual.ForAgent("a")!.Agent!.Key);
    }
}
