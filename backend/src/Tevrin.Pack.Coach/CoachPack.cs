using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Pack.Coach;

/// <summary>
/// FR-338, FR-339: the Impulse coaching pack. It registers <c>Definitions/impulse-coach.pipeline.yaml</c>, the artifacts'
/// JSON schemas, the agents' prompt templates and the Velocity guard name (FR-340). It references only the engine
/// (D-0.7.0-23); the host binds <see cref="VelocityLoadRulesGuard"/> to <see cref="CoachGuardNames.VelocityLoadRules"/>.
/// The pipeline is engine-controlled (the default <see cref="IPipelinePack.RunControlOf"/>).
/// </summary>
public sealed class CoachPack : IPipelinePack
{
    /// <summary>The pack id: the namespace of its guard names (<c>coach.velocity-load-rules</c>).</summary>
    public const string PackId = "coach";

    /// <summary>The pipeline id of <c>impulse-coach.pipeline.yaml</c>.</summary>
    public const string PipelineId = "impulse-coach";

    /// <summary>The definition's embedded resource (and source) name.</summary>
    public const string DefinitionName = "impulse-coach.pipeline.yaml";

    public string Id => PackId;

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using (var yaml = Open("Definitions/" + DefinitionName))
        {
            registry.AddDefinition(DefinitionName, yaml);
        }

        foreach (var artifact in CoachArtifacts.All)
        {
            var schema = CoachArtifacts.SchemaOf(artifact);
            registry.AddSchema(schema, ReadText("Schemas/" + schema));
        }

        foreach (var prompt in CoachPrompts.All)
        {
            registry.AddPrompt(prompt, ReadText("Prompts/" + prompt + ".md"));
        }

        foreach (var guard in CoachGuardNames.All)
        {
            registry.AddGuard(guard);
        }
    }

    /// <summary>The embedded <c>impulse-coach.pipeline.yaml</c>.</summary>
    public static Stream OpenDefinition() => Open("Definitions/" + DefinitionName);

    /// <summary>An embedded resource's text (UTF-8).</summary>
    public static string ReadText(string resource)
    {
        using var stream = Open(resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream Open(string resource) =>
        typeof(CoachPack).Assembly.GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException($"'{resource}' is not embedded in {typeof(CoachPack).Assembly.GetName().Name}.");
}
