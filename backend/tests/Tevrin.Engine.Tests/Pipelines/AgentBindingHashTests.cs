using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// D-0.7.0-42, FR-277, §4.50: secondary agent bindings (<c>agents:</c>) are additive to the canonical JSON. A definition
/// without them hashes exactly as before (pinned below); one with them writes each binding with its defaults filled.
/// </summary>
public class AgentBindingHashTests
{
    /// <summary>
    /// The SDLC hash of <c>sdlc.pipeline.yaml</c> version 2 (also pinned in <c>SdlcContextProviderTests</c>). FR-321,
    /// D-0.7.0-41: version 2 adds plan validation, the primary agents' repo tools and the <c>agents:</c> bindings, so the hash legitimately changed from
    /// <see cref="SdlcVersion1Hash"/>; runs pinned to that one are re-pinned at start-up.
    /// </summary>
    public const string SdlcHash = "d2f1c98b44e99bd673944f36cfdfd3abb64e072f893e3b7ea6d8366c3f689873";

    /// <summary>The 0.7.0 SDLC hash before FR-321 (version 1), unchanged by D-0.7.0-42's engine change.</summary>
    public const string SdlcVersion1Hash = "90f1f065397fe245955c62ef5057122a6fca2ae373941a90dc37b431ecf0c9e7";

    public const string FixtureReviewHash = "680bed90acc8058107008946fef2874f660eafd1addf5c0f25fa3cbecb011f69";

    public const string SdlcSketchHash = "df9482417ad204cc403f497d0763bd7dcb2a4cb0cb6874c8d8f2f2a479bcd641";

    public const string MinimalHash = "b365b160aa09e6a3ac1f23559eda4624e00d39d899e381eed7e63b96770930f3";

    private static string Hash(string yaml) => DefinitionHash.Compute(PipelineDefinitionLoader.Parse(yaml).Definition!);

    [Fact]
    public void FR321_the_sdlc_hash_is_version_2s()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(SdlcHash, Hash(reader.ReadToEnd()));
    }

    [Fact]
    public void D_0_7_0_42_the_fixture_hashes_are_unchanged()
    {
        Assert.Equal(FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));
        Assert.Equal(MinimalHash, Hash(Minimal));
    }

    /// <summary><see cref="DefinitionYaml.Minimal"/> with one binding on summarise.</summary>
    private static string WithBinding(string binding) =>
        Edit("    status: working\n    inputs: [report]\n", $"    status: working\n    agents:\n      - {binding}\n    inputs: [report]\n");

    [Fact]
    public void D_0_7_0_42_canonical_json_omits_agents_when_there_are_none()
    {
        Assert.DoesNotContain("\"agents\"", DefinitionHash.CanonicalJson(Fixtures.Load("fixture-review.pipeline.yaml")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"agents\"", DefinitionHash.CanonicalJson(PipelineDefinitionLoader.Parse(Minimal).Definition!), StringComparison.Ordinal);
    }

    [Fact]
    public void D_0_7_0_42_a_binding_is_written_with_its_defaults_filled_and_reads_back_to_the_same_hash()
    {
        var definition = PipelineDefinitionLoader.Parse(WithBinding("{ key: reviewer, job_type: stage.run }")).Definition!;
        var json = DefinitionHash.CanonicalJson(definition);

        Assert.Contains("\"agents\":[{\"inputs\":[\"report\"],\"job_type\":\"stage.run\",\"key\":\"reviewer\",\"tools\":[]}]", json, StringComparison.Ordinal);
        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(json, DefinitionHash.CanonicalJson(reread.Definition!));
        Assert.NotEqual(MinimalHash, DefinitionHash.Compute(definition));
    }

    [Fact]
    public void D_0_7_0_42_writing_a_bindings_defaults_out_keeps_the_hash() =>
        Assert.Equal(
            Hash(WithBinding("{ key: reviewer, job_type: stage.run }")),
            Hash(WithBinding("{ tools: [], inputs: [report], job_type: stage.run, key: reviewer }")));

    [Fact]
    public void D_0_7_0_42_a_changed_binding_value_changes_the_hash()
    {
        var hash = Hash(WithBinding("{ key: reviewer, job_type: stage.run }"));
        Assert.NotEqual(hash, Hash(WithBinding("{ key: reviewer, job_type: stage.run, inputs: [] }")));
        Assert.NotEqual(hash, Hash(WithBinding("{ key: reviewer, job_type: stage.run, output: summary }")));
        Assert.NotEqual(hash, Hash(WithBinding("{ key: reviewer, job_type: stage.run, prompt: p.review }")));
        Assert.NotEqual(hash, Hash(WithBinding("{ key: reviewer, job_type: gate.timer }")));
        Assert.NotEqual(hash, Hash(WithBinding("{ key: reviewer, job_type: stage.run, tools: [fixture.lookup] }")));
    }
}
