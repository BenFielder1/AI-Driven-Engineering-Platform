using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-339 (0.8.0, §4.49 amended): <c>triggers: [ … ]</c>, the list form of <c>trigger:</c>. Each entry follows the
/// per-kind rules, the list has at most one webhook and one schedule, declaring both forms is <c>DEF_PARSE</c>, and the
/// canonical JSON carries <c>triggers</c> only when declared, so existing hashes are unchanged.
/// </summary>
public class TriggerListDefinitionTests
{
    /// <summary><see cref="DefinitionYaml.Minimal"/> with a <c>brief</c> input artifact and <paramref name="triggers"/>
    /// in place of <c>trigger: { kind: manual }</c>.</summary>
    private static string With(string triggers) => Edit(
        "  summary: { title: Summary, format: markdown }\n",
        "  summary: { title: Summary, format: markdown }\n  brief: { title: Brief, format: json, input: true }\n",
        Edit("trigger: { kind: manual }", triggers));

    private static readonly string AllThree = With(
        "triggers:\n  - { kind: manual }\n  - { kind: webhook, input: brief, on_active: submit }\n  - { kind: schedule }");

    private static PipelineDefinition Parse(string yaml) => PipelineDefinitionLoader.Parse(yaml).Definition!;

    [Fact]
    public void FR339_a_list_of_manual_webhook_and_schedule_is_valid_and_read()
    {
        Assert.Empty(Errors(AllThree));
        var definition = Parse(AllThree);

        Assert.Equal([TriggerKind.Manual, TriggerKind.Webhook, TriggerKind.Schedule], definition.DeclaredTriggers.Select(t => t.Kind));
        Assert.Equal(("brief", TriggerOnActive.Submit), (definition.TriggerOf(TriggerKind.Webhook)!.Input, definition.TriggerOf(TriggerKind.Webhook)!.OnActive));
        Assert.Equal(TriggerOnActive.Skip, definition.TriggerOf(TriggerKind.Schedule)!.OnActive);
        Assert.NotNull(definition.TriggerOf(TriggerKind.Manual));
    }

    [Fact]
    public void FR339_TriggerOf_reads_the_single_trigger_when_there_is_no_list()
    {
        var webhook = Parse(With("trigger: { kind: webhook, input: brief }"));

        Assert.Null(webhook.Triggers);
        Assert.Equal("brief", webhook.TriggerOf(TriggerKind.Webhook)!.Input);
        Assert.Null(webhook.TriggerOf(TriggerKind.Schedule));
        Assert.Null(webhook.TriggerOf(TriggerKind.Manual));
        Assert.Equal(TriggerKind.Manual, Assert.Single(Parse(Minimal).DeclaredTriggers).Kind);
    }

    [Fact]
    public void FR339_DEF_PARSE_declaring_trigger_and_triggers()
    {
        var both = AllThree.Replace("triggers:\n", "trigger: { kind: manual }\ntriggers:\n", StringComparison.Ordinal);

        Assert.Equal([(DefinitionErrorCodes.Parse, "triggers")], Errors(both));
    }

    [Fact]
    public void FR339_DEF_PARSE_a_second_webhook_or_schedule()
    {
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "triggers[1].kind")],
            Errors(With("triggers:\n  - { kind: webhook, input: brief }\n  - { kind: webhook, input: brief }")));
        Assert.Equal(
            [(DefinitionErrorCodes.Parse, "triggers[2].kind")],
            Errors(With("triggers:\n  - { kind: schedule }\n  - { kind: manual }\n  - { kind: schedule }")));
    }

    [Fact]
    public void FR339_DEF_PARSE_an_empty_list_or_an_entry_that_is_not_a_mapping()
    {
        Assert.Equal([(DefinitionErrorCodes.Parse, "triggers")], Errors(With("triggers: []")));
        Assert.Contains((DefinitionErrorCodes.Parse, "triggers[0]"), Errors(With("triggers:\n  - manual")));
    }

    [Fact]
    public void FR339_each_entry_follows_the_per_kind_rules()
    {
        Assert.Equal([(DefinitionErrorCodes.Parse, "triggers[1].input")], Errors(With("triggers:\n  - { kind: manual }\n  - { kind: webhook }")));
        Assert.Equal([(DefinitionErrorCodes.TriggerNotSupported, "triggers[0].input")], Errors(With("triggers:\n  - { kind: manual, input: brief }")));
        Assert.Equal([(DefinitionErrorCodes.TriggerNotSupported, "triggers[0].cron")], Errors(With("triggers:\n  - { kind: schedule, cron: daily }")));
        Assert.Equal([(DefinitionErrorCodes.Parse, "triggers[0].on_active")], Errors(With("triggers:\n  - { kind: schedule, on_active: queue }")));
        Assert.Equal([(DefinitionErrorCodes.Parse, "triggers[0].input")], Errors(With("triggers:\n  - { kind: webhook, input: draft }")));
        Assert.Equal([(DefinitionErrorCodes.UnknownArtifact, "triggers[0].input")], Errors(With("triggers:\n  - { kind: webhook, input: nope }")));
    }

    [Fact]
    public void FR339_triggers_is_left_out_of_the_canonical_json_unless_declared()
    {
        Assert.Equal(AgentBindingHashTests.MinimalHash, DefinitionHash.Compute(Parse(Minimal)));
        Assert.DoesNotContain("\"triggers\"", DefinitionHash.CanonicalJson(Parse(Minimal)), StringComparison.Ordinal);

        var json = DefinitionHash.CanonicalJson(Parse(AllThree));
        Assert.Contains(
            "\"triggers\":[{\"kind\":\"manual\"},{\"input\":\"brief\",\"kind\":\"webhook\",\"on_active\":\"submit\"},{\"kind\":\"schedule\"}]",
            json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"trigger\":", json, StringComparison.Ordinal);
        Assert.NotEqual(DefinitionHash.Compute(Parse(With("trigger: { kind: manual }"))), DefinitionHash.Compute(Parse(With("triggers:\n  - { kind: manual }"))));
    }

    [Fact]
    public void FR339_canonical_json_with_triggers_reads_back_to_the_same_hash()
    {
        var definition = Parse(AllThree);
        var reread = PipelineDefinitionLoader.Parse(DefinitionHash.CanonicalJson(definition)).Definition!;

        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread));
        Assert.Equal(3, reread.Triggers!.Count);
    }
}
