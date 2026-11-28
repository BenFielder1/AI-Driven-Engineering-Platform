using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Triggers;

/// <summary>
/// FR-336, FR-337 (M66 step c): the trigger fixtures: <c>fixture-scheduled</c> (<c>trigger: { kind: schedule }</c>) and
/// <c>fixture-webhook</c> (<c>trigger: { kind: webhook, input: payload, on_active: submit }</c>), each a single gate stage.
/// </summary>
public sealed class FixtureTriggersPack : IPipelinePack
{
    public const string ScheduledPipeline = "fixture-scheduled";
    public const string WebhookPipeline = "fixture-webhook";

    public string Id => "fixture-triggers";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition("fixture-scheduled.pipeline.yaml", Fixtures.Read("fixture-scheduled.pipeline.yaml"));
        registry.AddDefinition("fixture-webhook.pipeline.yaml", Fixtures.Read("fixture-webhook.pipeline.yaml"));
        registry.AddSchema("brief.schema.json", Fixtures.Read("brief.schema.json"));
        registry.AddSchema("payload.schema.json", Fixtures.Read("payload.schema.json"));
    }
}

/// <summary>FR-336, FR-337: the trigger fixtures load valid, engine-controlled, with the triggers they declare.</summary>
public class FixtureTriggersDefinitionTests
{
    private static readonly PipelineRegistry Registry = new([new FixtureTriggersPack()], [], Array.Empty<JobHandlerRegistration>());

    [Fact]
    public void FR336_FR337_trigger_fixtures_are_valid_and_declare_their_triggers()
    {
        Assert.Empty(Registry.Errors);

        Assert.True(Registry.TryGet(FixtureTriggersPack.ScheduledPipeline, out var scheduled));
        Assert.Equal(TriggerKind.Schedule, scheduled.Definition!.Trigger.Kind);
        Assert.Equal(RunControl.Engine, scheduled.RunControl);

        Assert.True(Registry.TryGet(FixtureTriggersPack.WebhookPipeline, out var webhook));
        Assert.Equal(TriggerKind.Webhook, webhook.Definition!.Trigger.Kind);
        Assert.Equal("payload", webhook.Definition.Trigger.Input);
        Assert.Equal(TriggerOnActive.Submit, webhook.Definition.Trigger.OnActive);
        Assert.Contains(EngineJobTypes.TriggerFire, EngineJobTypes.RunScoped);
    }
}
