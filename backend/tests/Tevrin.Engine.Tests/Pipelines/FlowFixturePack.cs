using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-343–FR-349 (M68): a pack for one flow-construct fixture (<c>fixture-parallel</c>, <c>fixture-veto</c>,
/// <c>fixture-forecast</c>, <c>fixture-editorial</c>, <c>fixture-variants</c>): its definition plus every prompt, guard
/// and job type the fixtures name. Definition syntax only; the driver steps add the runtime.
/// </summary>
public sealed class FlowFixturePack(string pipelineId) : IPipelinePack
{
    public const string Parallel = "fixture-parallel";
    public const string Veto = "fixture-veto";
    public const string Forecast = "fixture-forecast";
    public const string Editorial = "fixture-editorial";
    public const string Variants = "fixture-variants";

    /// <summary>FR-350 (M69): a writer stage with a fact-checker validator agent.</summary>
    public const string FactCheck = "fixture-factcheck";

    public const string PositionLimitGuard = "flow.position-limit";
    public const string ForecastSpreadGuard = "flow.forecast-spread";

    public static IReadOnlyList<string> All { get; } = [Parallel, Veto, Forecast, Editorial, Variants, FactCheck];

    public static IReadOnlyList<string> Prompts { get; } =
    [
        "flow.bull", "flow.bull-review", "flow.bear", "flow.decide",
        "flow.trade", "flow.size", "flow.risk", "flow.order",
        "flow.forecast", "flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian", "flow.aggregate",
        "flow.research", "flow.write", "flow.edit",
        "flow.headline", "flow.tagline", "flow.judge",
        "flow.factwrite", "flow.fact-check",
    ];

    public static IReadOnlyList<string> JobTypes { get; } =
        ["flow.risk-review", "flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian", "flow.judge", "flow.fact-check"];

    /// <summary>The catalog the fixtures validate against (prompts unchecked), for the invalid-case tests.</summary>
    public static PackCatalog Catalog { get; } = new()
    {
        Guards = [PositionLimitGuard, ForecastSpreadGuard],
        JobTypes = JobTypes,
    };

    /// <summary>The guard and job type namespace (<c>flow.&lt;name&gt;</c>).</summary>
    public string Id => "flow";

    /// <summary>The fixture's YAML.</summary>
    public static string Yaml(string pipelineId) => Fixtures.Read(pipelineId + ".pipeline.yaml");

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition(pipelineId + ".pipeline.yaml", Yaml(pipelineId));
        foreach (var prompt in Prompts)
        {
            registry.AddPrompt(prompt, $"""
                ---
                version: {prompt}-1
                ---
                You are a fixture agent.
                <!-- user -->
                Do the work.
                """);
        }

        registry.AddGuard(PositionLimitGuard);
        registry.AddGuard(ForecastSpreadGuard);
        foreach (var jobType in JobTypes)
        {
            registry.AddJobType(jobType);
        }
    }
}
