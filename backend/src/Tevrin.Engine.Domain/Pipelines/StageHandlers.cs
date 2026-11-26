namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-280, D-0.7.0-43: finding the stage that declares a stage handler.</summary>
public static class StageHandlers
{
    /// <summary>
    /// The stage whose <c>handler:</c>, or one of whose <c>handlers:</c> entries, is <paramref name="handler"/>, or
    /// <c>null</c> when no stage declares it (e.g. a definition written before the handler existed). Names are unique
    /// across a valid pipeline, so the first match is the only one. Branch stages of parallel groups count (FR-343).
    /// </summary>
    public static StageDefinition? StageOf(PipelineDefinition definition, string handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        return definition.AllStages.FirstOrDefault(s => s.Declares(handler));
    }

    /// <summary>Whether <paramref name="stage"/> declares <paramref name="handler"/> as its <c>handler:</c> or in <c>handlers:</c>.</summary>
    public static bool Declares(this StageDefinition stage, string handler)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return string.Equals(stage.Handler, handler, StringComparison.Ordinal)
            || stage.Handlers.Contains(handler, StringComparer.Ordinal);
    }

    /// <summary>Every handler name <paramref name="stage"/> declares: its <c>handler:</c>, then its <c>handlers:</c>.</summary>
    public static IEnumerable<string> AllHandlers(this StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Handler is { } primary ? [primary, .. stage.Handlers] : stage.Handlers;
    }

    /// <summary>FR-355: the handlers <paramref name="stage"/>'s <c>map</c> and <c>reduce</c> name, in that order (none when
    /// they use agents).</summary>
    public static IEnumerable<string> ConstructHandlers(this StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return new[] { stage.Map?.Handler, stage.Reduce?.Handler }.OfType<string>();
    }
}
