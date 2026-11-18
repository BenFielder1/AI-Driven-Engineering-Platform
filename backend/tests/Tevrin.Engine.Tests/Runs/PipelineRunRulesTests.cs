using System.Text.Json;
using System.Text.Json.Nodes;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-277, FR-294, D-0.7.0-28: stored run status, transition kinds and stored definition hashes.</summary>
public class PipelineRunRulesTests
{
    /// <summary>A YAML title with escaped quotes and backslash, a tab, U+2028 and non-ASCII.</summary>
    private const string SpecialTitle =
        "title: \"Say \\\"hi\\\" \\\\ tab\\there\\u2028sep caf\\u00e9\"";

    private static PipelineDefinition Load(string yaml)
    {
        var result = PipelineDefinitionLoader.ParseAndValidate(yaml, DefinitionYaml.Catalog);
        Assert.Empty(result.Errors);
        return result.Definition!;
    }

    [Theory]
    [InlineData("done", GenericRunStatus.Completed)]
    [InlineData("working", GenericRunStatus.Active)]
    [InlineData("completed", GenericRunStatus.Completed)]
    [InlineData("failed", GenericRunStatus.Failed)]
    [InlineData("cancelled", GenericRunStatus.Cancelled)]
    [InlineData("waiting_on_gate", GenericRunStatus.Active)]
    [InlineData("running", GenericRunStatus.Active)]
    [InlineData("unknown", GenericRunStatus.Active)]
    public void D_0_7_0_28_only_final_statuses_and_active_are_stored(string packStatus, GenericRunStatus expected) =>
        Assert.Equal(expected, PipelineRunRules.StoredStatus(Load(DefinitionYaml.Minimal), packStatus));

    [Fact]
    public void D_0_7_0_28_completed_failed_and_cancelled_are_final() =>
        Assert.Equal(
            [GenericRunStatus.Completed, GenericRunStatus.Failed, GenericRunStatus.Cancelled],
            Enum.GetValues<GenericRunStatus>().Where(PipelineRunRules.IsFinal));

    [Fact]
    public void FR294_stored_transition_kinds_equal_the_engine_transition_kinds_plus_start()
    {
        string[] stored =
        [
            PipelineTransitionKinds.Advance, PipelineTransitionKinds.Loop, PipelineTransitionKinds.Rollback,
            PipelineTransitionKinds.Fallback, PipelineTransitionKinds.StatusChange, PipelineTransitionKinds.Noop,
        ];

        Assert.Equal(stored, Enum.GetValues<RunTransitionKind>().Select(k => k.ToWire()));
        Assert.DoesNotContain(PipelineTransitionKinds.Start, stored);
    }

    [Fact]
    public void FR277_canonical_json_read_back_with_a_JSON_reader_reproduces_the_hash()
    {
        // Quotes, a backslash, a control character, a line separator (a line break to YAML) and non-ASCII.
        var definition = Load(DefinitionYaml.Edit("title: Minimal", SpecialTitle));
        var canonical = DefinitionHash.CanonicalJson(definition);
        var hash = DefinitionHash.Compute(definition);

        Assert.Equal(hash, StoredDefinitions.HashOf(canonical));

        // What jsonb hands back keeps the meaning, not the bytes: other key order, whitespace and escapes.
        var reordered = Reverse(JsonNode.Parse(canonical)!).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(canonical, reordered);
        Assert.Equal(hash, StoredDefinitions.HashOf(reordered));
    }

    [Fact]
    public void FR277_a_changed_value_changes_the_stored_hash()
    {
        var canonical = DefinitionHash.CanonicalJson(Load(DefinitionYaml.Minimal));
        var changed = canonical.Replace("\"max_attempts\":3", "\"max_attempts\":4", StringComparison.Ordinal);

        Assert.NotEqual(canonical, changed);
        Assert.NotEqual(StoredDefinitions.HashOf(canonical), StoredDefinitions.HashOf(changed));
    }

    private static JsonNode Reverse(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Reverse(p.Value.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(i => i is null ? null : Reverse(i.DeepClone()))]),
        _ => node.DeepClone(),
    };
}
