using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-352 (M67): artifact <c>scope: project</c> in the definition model: loaded, validated, and left out of the canonical
/// JSON when it is the default <c>run</c>, so every existing hash (the SDLC's included) is unchanged.
/// </summary>
public class SourceOfTruthDefinitionTests
{
    /// <summary><see cref="DefinitionYaml.Minimal"/> with a project-scope <c>source</c> the check stage reads.</summary>
    private static readonly string WithSource = Edit(
        "  summary: { title: Summary, format: markdown }\n",
        "  summary: { title: Summary, format: markdown }\n  source: { title: Source of truth, format: markdown, scope: project }\n",
        Edit("    inputs: [draft]\n", "    inputs: [draft, source]\n"));

    private static PipelineDefinition Parse(string yaml) => PipelineDefinitionLoader.Parse(yaml).Definition!;

    private static string Hash(string yaml) => DefinitionHash.Compute(Parse(yaml));

    [Fact]
    public void FR352_the_loader_reads_scope_and_defaults_to_run()
    {
        var definition = Parse(WithSource);

        Assert.Equal(ArtifactScope.Project, definition.FindArtifact("source")!.Scope);
        Assert.Equal(ArtifactScope.Run, definition.FindArtifact("draft")!.Scope);
    }

    [Fact]
    public void FR352_the_sdlc_and_existing_definitions_hash_as_before()
    {
        using var stream = SdlcPack.OpenDefinition();
        using var reader = new StreamReader(stream);
        Assert.Equal(AgentBindingHashTests.SdlcHash, Hash(reader.ReadToEnd()));
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Minimal));
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));
        Assert.Equal(AgentBindingHashTests.SdlcSketchHash, DefinitionHash.Compute(Fixtures.Load("sdlc-sketch.pipeline.yaml")));
        Assert.DoesNotContain("\"scope\"", DefinitionHash.CanonicalJson(Parse(Minimal)), StringComparison.Ordinal);
    }

    [Fact]
    public void FR352_writing_the_default_scope_run_keeps_the_hash() =>
        Assert.Equal(AgentBindingHashTests.MinimalHash, Hash(Edit("approval: auto_lock,", "approval: auto_lock, scope: run,")));

    [Fact]
    public void FR352_scope_project_changes_the_hash_is_written_out_and_reads_back()
    {
        var plain = Edit("  summary: { title: Summary, format: markdown }\n",
            "  summary: { title: Summary, format: markdown }\n  source: { title: Source of truth, format: markdown }\n",
            Edit("    inputs: [draft]\n", "    inputs: [draft, source]\n"));
        Assert.NotEqual(Hash(plain), Hash(WithSource));

        var json = DefinitionHash.CanonicalJson(Parse(WithSource));
        Assert.Contains("\"scope\":\"project\"", json, StringComparison.Ordinal);
        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(Hash(WithSource), DefinitionHash.Compute(reread.Definition!));
    }

    [Fact]
    public void FR352_a_project_scope_artifact_needs_no_producing_stage() => Assert.Empty(Errors(WithSource));

    [Fact]
    public void FR352_DEF_SCOPE_INPUT_a_stage_may_not_output_a_project_scope_artifact() =>
        Assert.Contains(
            (DefinitionErrorCodes.ScopeInput, "stages[0].output"),
            Errors(Edit("  draft: { title: Draft, format: json, approval: auto_lock, cascade: downstream }",
                "  draft: { title: Draft, format: json, approval: auto_lock, cascade: downstream, scope: project }")));

    [Fact]
    public void FR352_DEF_SCOPE_INPUT_an_artifact_is_not_both_input_and_project_scope() =>
        Assert.Contains(
            (DefinitionErrorCodes.ScopeInput, "artifacts.source.scope"),
            Errors(WithSource.Replace("scope: project", "scope: project, input: true", StringComparison.Ordinal)));

    [Fact]
    public void FR352_DEF_SCOPE_INPUT_a_gate_may_not_approve_a_project_scope_artifact() =>
        Assert.Contains(
            (DefinitionErrorCodes.ScopeInput, "stages[2].gate.approves"),
            Errors(Edit("  report: { title: Report, format: markdown }", "  report: { title: Report, format: markdown, scope: project }")));

    [Fact]
    public void FR352_DEF_SCOPE_INPUT_a_pack_handler_may_not_produce_a_project_scope_artifact()
    {
        Assert.Contains(
            (DefinitionErrorCodes.ScopeInput, "artifacts.source.scope"),
            Errors(WithSource, Catalog with { ProducedArtifacts = ["source"] }));
        Assert.Empty(Errors(WithSource, Catalog with { ProducedArtifacts = ["summary"] }));
    }

    [Fact]
    public void FR352_DEF_SCOPE_INPUT_is_in_the_code_table() => Assert.Contains("DEF_SCOPE_INPUT", DefinitionErrorCodes.All);

    [Fact]
    public void FR352_DEF_PARSE_an_unknown_scope_value() =>
        Assert.Contains(Errors(WithSource.Replace("scope: project", "scope: team", StringComparison.Ordinal)), e => e.Code == DefinitionErrorCodes.Parse);

    [Fact]
    public void FR352_fixture_cv_is_valid_engine_controlled_and_declares_master_cv_project_scope()
    {
        var registry = new PipelineRegistry([new FixtureCvPack()], [], Array.Empty<JobHandlerRegistration>());

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        Assert.True(registry.TryGet(FixtureCvPack.PipelineId, out var loaded));
        Assert.Equal(RunControl.Engine, registry.RunControlOf(FixtureCvPack.PipelineId));
        Assert.Equal(["score", "tailor"], loaded.Definition!.Stages.Select(s => s.Id));
        Assert.Equal(ArtifactScope.Project, loaded.Definition.FindArtifact("master_cv")!.Scope);
    }

    [Fact]
    public void FR352_D_0_8_0_19_pipelines_sharing_a_project_scope_key_must_declare_it_the_same_way()
    {
        var mismatch = new PipelineRegistry([new FixtureCvPack(), new SharedCvPack("fixture-cv-json", "json")], [], Array.Empty<JobHandlerRegistration>());
        Assert.Empty(mismatch.Errors.Where(e => e.Error.Code != DefinitionErrorCodes.ScopeInput).Select(e => e.Error.ToString()));
        var error = Assert.Single(mismatch.Errors);
        Assert.Equal(("fixture-cv-json", DefinitionErrorCodes.ScopeInput, "artifacts.master_cv"),
            (error.Source.Definition!.Pipeline, error.Error.Code, error.Error.Path));
        Assert.Contains("'fixture-cv'", error.Error.Message, StringComparison.Ordinal);
        Assert.Contains("'fixture-cv-json'", error.Error.Message, StringComparison.Ordinal);
        Assert.True(mismatch.TryGet(FixtureCvPack.PipelineId, out _));
        Assert.False(mismatch.TryGet("fixture-cv-json", out _));

        var same = new PipelineRegistry([new FixtureCvPack(), new SharedCvPack("fixture-cv-md", "markdown")], [], Array.Empty<JobHandlerRegistration>());
        Assert.Empty(same.Errors.Select(e => e.Error.ToString()));
    }

    /// <summary>A second pipeline declaring the project-scope <c>master_cv</c> with <paramref name="format"/>.</summary>
    private sealed class SharedCvPack(string id, string format) : IPipelinePack
    {
        public string Id => id;

        public void Register(IPackRegistry registry)
        {
            registry.AddDefinition(id + ".pipeline.yaml", $$"""
                pipeline: {{id}}
                version: 1
                title: Shared CV
                trigger: { kind: manual }
                approval_modes: [advisory]
                default_approval_mode: advisory
                start: [score]
                artifacts:
                  master_cv: { title: Master CV, format: {{format}}, approval: required, cascade: none, scope: project }
                  out: { title: Out, format: markdown, approval: auto_lock, cascade: none }
                statuses:
                  scoring: { generic: active }
                  done: { generic: completed }
                stages:
                  - id: score
                    title: Score
                    status: scoring
                    agent: { key: scorer, prompt: {{id}}.score }
                    inputs: [master_cv]
                    output: out
                """);
            registry.AddPrompt(id + ".score", "---\nversion: s1\n---\nYou score.\n<!-- user -->\n<cv>{{MASTER_CV?}}</cv>\n");
        }
    }
}
