using Aide.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// D-0.7.0-44: the output checks <c>sdlc.pipeline.yaml</c> version 3 declares: <c>validate:</c> and 0.6.1's one corrective
/// re-run (<c>on_invalid: { retry_with_feedback: true, max_attempts: 2, then: fail }</c>) on each checked agent's stage, or
/// on its <c>agents:</c> binding; and version 2 derived from it, for the runs that keep the host checks.
/// </summary>
internal static class SdlcOutputGuardFixture
{
    public static readonly OnInvalidPolicy CorrectiveOnce = new(RetryWithFeedback: true, MaxAttempts: 2, Then: InvalidOutcome.Fail);

    /// <summary>The stage (and, for a bound agent, the binding) whose output each guard checks.</summary>
    public static readonly (string Stage, string? Binding, string Guard)[] Checks =
    [
        ("analysis", null, SdlcGuardNames.StackProfileValid),
        ("refinement", null, SdlcGuardNames.RefinementValid),
        ("refinement", SdlcAgents.RepositoryAnalyst, SdlcGuardNames.SummarySections),
        ("specification", null, SdlcGuardNames.SpecSections),
        ("planning", null, SdlcGuardNames.PlanValid),
        ("execution", SdlcAgents.BuildFix, SdlcGuardNames.FixPlanValid),
        ("review", null, SdlcGuardNames.ReviewValid),
    ];

    /// <summary>
    /// Pins <paramref name="harness"/>'s run to version 2 (<see cref="Version2"/> of the registered version 3, under
    /// <see cref="SdlcRunRepin.Version2Hash"/>) unless <paramref name="version3"/>: the harness runs on the registered
    /// definition, version 3, by default.
    /// </summary>
    public static void Pin(SdlcAgentStageHarness harness, bool version3)
    {
        ArgumentNullException.ThrowIfNull(harness);
        if (version3)
        {
            return;
        }

        harness.PinTo(SdlcRunRepin.Version2Hash, Version2(Registered(harness.Services)));
    }

    /// <summary>The registered SDLC definition (version 3).</summary>
    public static PipelineDefinition Registered(IServiceProvider services)
    {
        Assert.True(services.GetRequiredService<PipelineRegistry>().TryGet(SdlcPack.PipelineId, out var loaded));
        return loaded.Definition!;
    }

    /// <summary>
    /// <paramref name="definition"/> (version 3) without D-0.7.0-43/-44's additions: only planning's <c>validate:</c>,
    /// no binding checks and no <c>handlers:</c>. Its hash is version 2's (asserted in <c>SdlcReplyCheckGuardTests</c>).
    /// </summary>
    public static PipelineDefinition Version2(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition with
        {
            Version = 2,
            Stages = [.. definition.Stages.Select(s => s with
            {
                Validate = s.Id == "planning" ? s.Validate : [],
                OnInvalid = s.Id == "planning" ? s.OnInvalid : null,
                Handlers = [],
                Agents = [.. s.Agents.Select(b => b with { Validate = [], OnInvalid = null })],
            })],
        };
    }
}
