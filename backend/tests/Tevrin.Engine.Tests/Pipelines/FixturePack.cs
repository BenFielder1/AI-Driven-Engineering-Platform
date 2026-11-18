using System.Text.Json;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// The test pack (M57; reused by M59 and M61): registers <c>fixture-review.pipeline.yaml</c>, its schemas, guard, tool and
/// setting from embedded resources, as a real pack does.
/// </summary>
public sealed class FixturePack : IPipelinePack
{
    public const string ReportConsistentGuard = "fixture.report-consistent";
    public const string LookupTool = "fixture.lookup";
    public const string RedraftCycles = "redraft_cycles";

    public string Id => "fixture";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using (var yaml = Fixtures.Open("fixture-review.pipeline.yaml"))
        {
            registry.AddDefinition("fixture-review.pipeline.yaml", yaml);
        }

        registry.AddSchema("draft.schema.json", Fixtures.Read("draft.schema.json"));
        registry.AddSchema("report.schema.json", Fixtures.Read("report.schema.json"));
        registry.AddGuard(ReportConsistentGuard);
        registry.AddTool(LookupTool);
        registry.AddSetting(RedraftCycles);
    }

    /// <summary>The catalog of everything this pack registers, for validator tests that skip the registry.</summary>
    public static PackCatalog Catalog { get; } = new()
    {
        Guards = [ReportConsistentGuard],
        Tools = [LookupTool],
        Settings = [RedraftCycles],
        Schemas = ["draft.schema.json", "report.schema.json"],
    };
}

/// <summary>Reads the embedded fixture files.</summary>
public static class Fixtures
{
    public static Stream Open(string name) =>
        typeof(Fixtures).Assembly.GetManifestResourceStream("Fixtures/" + name)
        ?? throw new InvalidOperationException($"Fixture '{name}' is not embedded.");

    public static string Read(string name)
    {
        using var reader = new StreamReader(Open(name));
        return reader.ReadToEnd();
    }

    public static PipelineDefinition Load(string name)
    {
        var result = PipelineDefinitionLoader.Parse(Read(name));
        Assert.Empty(result.Errors);
        return result.Definition!;
    }
}

/// <summary>A guard that always passes (bound in DI by registry tests).</summary>
public sealed class PassingGuard : IGuard
{
    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken) => Task.FromResult(GuardResult.Pass);
}

/// <summary>A tool that echoes its arguments.</summary>
public sealed class EchoTool : IAgentTool
{
    public ToolSpec Spec { get; } = new("lookup", "Echoes its arguments.", JsonDocument.Parse("""{"type":"object"}""").RootElement);

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken) => Task.FromResult(new ToolResult(args.GetRawText()));
}
