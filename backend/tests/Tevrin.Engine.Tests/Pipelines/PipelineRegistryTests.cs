using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-278: packs and DI bindings build the registry; definitions are loaded, hashed and validated at startup.</summary>
public class PipelineRegistryTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack<FixturePack>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void FR278_the_fixture_pack_registers_a_valid_definition()
    {
        using var provider = Build();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.True(registry.IsValid, string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        Assert.True(registry.TryGet("fixture-review", out var loaded));
        Assert.Equal(("fixture", "fixture-review.pipeline.yaml"), (loaded.PackId, loaded.Name));
        Assert.Equal(DefinitionHash.Compute(loaded.Definition!), loaded.Hash);
        Assert.Same(loaded, registry.GetByHash(loaded.Hash!));
        Assert.Null(registry.GetByHash(new string('0', 64)));
        Assert.False(registry.TryGet("nope", out _));
    }

    [Fact]
    public void FR278_di_bindings_count_as_registered_and_resolve_by_name()
    {
        using var provider = Build(services => services
            .AddPipelinePack(new InlinePack("extra", "pipeline: extra\nversion: 1\ntitle: Extra\napproval_modes: [strict]\ndefault_approval_mode: strict\nstart: [one]\nstages:\n  - { id: one, title: One, status: active, requires: [{ guard: host.ready }], agent: { key: a, tools: [host.echo] } }\n"))
            .AddPipelineGuard<PassingGuard>("host.ready")
            .AddPipelineTool<EchoTool>("host.echo"));
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.True(registry.IsValid, string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        using var scope = provider.CreateScope();
        Assert.IsType<PassingGuard>(scope.ServiceProvider.GetPipelineGuard("host.ready"));
        Assert.IsType<EchoTool>(scope.ServiceProvider.GetPipelineTool("host.echo"));
        Assert.Null(scope.ServiceProvider.GetPipelineGuard("host.missing"));
    }

    [Fact]
    public void FR278_an_invalid_definition_is_reported_and_not_served()
    {
        using var provider = Build(services => services.AddPipelinePack(new InlinePack("broken", "pipeline: broken\nversion: 1\ntitle: Broken\napproval_modes: [strict]\ndefault_approval_mode: strict\nstart: [one]\nstages:\n  - { id: one, title: One, status: active, requires: [{ guard: broken.missing }] }\n")));
        var registry = provider.GetRequiredService<PipelineRegistry>();

        Assert.False(registry.IsValid);
        Assert.False(registry.TryGet("broken", out _));
        Assert.True(registry.TryGet("fixture-review", out _));
        var (source, error) = Assert.Single(registry.Errors);
        Assert.Equal(("broken", DefinitionErrorCodes.UnknownGuard, "stages[0].requires[0].guard"), (source.PackId, error.Code, error.Path));
    }

    [Fact]
    public void FR278_a_pipeline_id_is_unique_across_packs()
    {
        using var provider = Build(services => services.AddPipelinePack(new InlinePack("copy", Fixtures.Read("fixture-review.pipeline.yaml"), withFixtureNames: true)));
        var (source, error) = Assert.Single(provider.GetRequiredService<PipelineRegistry>().Errors);
        Assert.Equal(("copy", DefinitionErrorCodes.DuplicateId, "pipeline"), (source.PackId, error.Code, error.Path));
    }

    [Fact]
    public void FR278_FR279_an_embedded_schema_that_is_not_json_schema_is_refused()
    {
        using var provider = Build(services => services.AddPipelinePack(new InlinePack(
            "schemas",
            "pipeline: schemas\nversion: 1\ntitle: S\napproval_modes: [strict]\ndefault_approval_mode: strict\nstart: [one]\nartifacts:\n  out: { title: Out, format: json, schema: bad.schema.json }\nstages:\n  - { id: one, title: One, status: active, agent: { key: a }, output: out }\n",
            schemas: new Dictionary<string, string>(StringComparer.Ordinal) { ["bad.schema.json"] = """{ "type": 5 }""" })));
        var (_, error) = Assert.Single(provider.GetRequiredService<PipelineRegistry>().Errors);
        Assert.Equal((DefinitionErrorCodes.SchemaNotFound, "artifacts.out.schema"), (error.Code, error.Path));
    }

    [Theory]
    [InlineData("other.guard")]
    [InlineData("NoNamespace")]
    public void FR278_pack_names_are_namespaced_by_pack_id(string name)
    {
        var registration = new PackRegistration("fixture");
        Assert.Throws<ArgumentException>(() => registration.AddGuard(name));
    }

    [Fact]
    public async Task FR290_the_readiness_check_is_healthy_when_every_definition_is_valid()
    {
        using var provider = Build();
        var result = await provider.GetRequiredService<PipelineDefinitionsHealthCheck>().CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task FR290_the_readiness_check_is_unhealthy_with_the_error_list()
    {
        using var provider = Build(services => services.AddPipelinePack(new InlinePack("broken", "pipeline: broken\nversion: 1\n")));
        var result = await provider.GetRequiredService<PipelineDefinitionsHealthCheck>().CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("broken/inline.pipeline.yaml: DEF_PARSE at title", result.Description, StringComparison.Ordinal);
        Assert.True(result.Data.Count >= 4); // title, approval_modes, default_approval_mode, start, stages
    }

    /// <summary>A pack with one inline definition (optionally the fixture pack's names, so a copy of its definition validates).</summary>
    private sealed class InlinePack(string id, string yaml, IReadOnlyDictionary<string, string>? schemas = null, bool withFixtureNames = false) : IPipelinePack
    {
        public string Id => id;

        public void Register(IPackRegistry registry)
        {
            registry.AddDefinition("inline.pipeline.yaml", yaml);
            foreach (var (name, json) in schemas ?? new Dictionary<string, string>())
            {
                registry.AddSchema(name, json);
            }

            if (withFixtureNames)
            {
                registry.AddSetting(FixturePack.RedraftCycles);
                registry.AddSchema("draft.schema.json", Fixtures.Read("draft.schema.json"));
                registry.AddSchema("report.schema.json", Fixtures.Read("report.schema.json"));
            }
        }
    }
}
