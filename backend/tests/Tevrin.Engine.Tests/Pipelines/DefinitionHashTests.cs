using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-277, §4.50: the definition hash over the canonical JSON.</summary>
public class DefinitionHashTests
{
    private static string Hash(string yaml) => DefinitionHash.Compute(PipelineDefinitionLoader.Parse(yaml).Definition!);

    [Fact]
    public void FR277_the_hash_is_lower_case_sha256_hex()
    {
        var hash = Hash(Minimal);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Hash(Minimal));
    }

    [Fact]
    public void FR277_a_comment_only_change_keeps_the_hash() =>
        Assert.Equal(Hash(Minimal), Hash("# A comment at the top\n" + Edit("    output: report", "    output: report   # trailing comment")));

    [Fact]
    public void FR277_max_attempts_3_to_4_changes_the_hash() =>
        Assert.NotEqual(Hash(Minimal), Hash(Edit("max_attempts: 3", "max_attempts: 4")));

    [Fact]
    public void FR277_key_order_and_flow_or_block_style_keep_the_hash()
    {
        var reordered = Edit("    on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }",
            "    on_invalid:\n      then: escalate\n      max_attempts: 3\n      retry_with_feedback: true");
        reordered = Edit("version: 1\ntitle: Minimal\n", "title: Minimal\nversion: 1\n", reordered);
        Assert.Equal(Hash(Minimal), Hash(reordered.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void FR277_writing_a_default_out_keeps_the_hash() =>
        Assert.Equal(Hash(Minimal), Hash(Edit("{ key: checker }", $"{{ key: checker, job_type: {EngineJobTypes.AgentRun} }}")));

    [Fact]
    public void FR277_a_changed_value_anywhere_changes_the_hash()
    {
        Assert.NotEqual(Hash(Minimal), Hash(Edit("title: Minimal", "title: Minimal 2")));
        Assert.NotEqual(Hash(Minimal), Hash(Edit("modes: [strict]", "modes: [strict, advisory]")));
        Assert.NotEqual(Hash(Minimal), Hash(Edit("  - { guard: fixture.report-consistent }", "  - { guard: fixture.other }")));
    }

    [Fact]
    public void FR277_canonical_json_is_compact_sorted_and_reads_back_to_the_same_hash()
    {
        var definition = Fixtures.Load("fixture-review.pipeline.yaml");
        var json = DefinitionHash.CanonicalJson(definition);

        Assert.StartsWith("{\"approval_modes\":[\"strict\",\"advisory\",\"autonomous\"],\"artifacts\":{\"draft\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        Assert.DoesNotContain(": ", json, StringComparison.Ordinal);

        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread.Definition!));
        Assert.Equal(json, DefinitionHash.CanonicalJson(reread.Definition!));
    }

    [Fact]
    public void FR277_canonical_json_escapes_only_quote_backslash_and_controls_and_writes_other_code_points_raw()
    {
        // U+0378 (unassigned), U+00A0 (no-break space), U+2028 (line separator) and U+1F600 (emoji) are written raw, so the
        // bytes don't depend on a .NET version's Unicode tables; a tab is \u0009, never \t.
        var definition = new PipelineDefinition { Pipeline = "u", Title = "a\u0378\u00A0\u2028\U0001F600\t\"\\" };

        var json = DefinitionHash.CanonicalJson(definition);

        Assert.Equal(
            "{\"approval_modes\":[],\"artifacts\":{},\"concurrency\":{\"exclusive\":[]},\"pipeline\":\"u\",\"settings\":[],\"stages\":[],"
            + "\"start\":[],\"status_changes\":{},\"statuses\":{},\"title\":\"a\u0378\u00A0\u2028\U0001F600\\u0009\\\"\\\\\","
            + "\"trigger\":{\"kind\":\"manual\"},\"version\":0}",
            json);
        // Pinned (computed independently): a change to the writer that alters any byte fails here.
        Assert.Equal("8a088fa62c99705b3dca3db1a8c3704eb4cbbc54fb8aa9d43e5e096c6d5fd953", DefinitionHash.Compute(definition));
    }

    [Fact]
    public void FR277_the_sdlc_sketch_round_trips_through_canonical_json()
    {
        var definition = Fixtures.Load("sdlc-sketch.pipeline.yaml");
        var reread = PipelineDefinitionLoader.Parse(DefinitionHash.CanonicalJson(definition));
        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread.Definition!));
    }
}
