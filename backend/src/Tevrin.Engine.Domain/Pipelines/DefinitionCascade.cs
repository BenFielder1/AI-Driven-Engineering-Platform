namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-289, §4.54: what approving a new version of an artifact does. <see cref="InvalidatedArtifactKeys"/> are in the
/// definition's artifact order; <see cref="PackFallbackStages"/> are the stages the pack's <see cref="IInvalidationHook"/>
/// counted its own effect as produced in; <see cref="FallbackStage"/> is where the run falls back to, or <c>null</c>.
/// </summary>
public sealed record CascadeEffect(
    string ApprovedArtifactKey,
    IReadOnlyList<string> InvalidatedArtifactKeys,
    IReadOnlyList<string> PackFallbackStages,
    string? FallbackStage);

/// <summary>
/// FR-289, §4.54: the invalidation cascade, computed from the definition. Artifact A depends on B when the stage that
/// outputs A has B in its <c>inputs</c>, transitively. Approving B invalidates every artifact that depends on B, unless B's
/// <c>cascade</c> is <c>none</c>. The fallback stage is the earliest of: the producing stages of the invalidated artifacts,
/// B's own producing stage for <c>self_and_downstream</c>, and the stages the pack's hook adds; it applies only when the
/// run is in a later stage. Pure.
/// </summary>
public static class DefinitionCascade
{
    /// <summary>The effect of approving <paramref name="approvedArtifactKey"/> while the run is in
    /// <paramref name="currentStage"/>. An unknown artifact or stage throws <see cref="ArgumentOutOfRangeException"/>, as
    /// 0.6.1's cascade does.</summary>
    public static CascadeEffect Effect(string approvedArtifactKey, string currentStage, PipelineDefinition definition, IInvalidationHook? hook = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var approved = definition.FindArtifact(approvedArtifactKey)
            ?? throw new ArgumentOutOfRangeException(nameof(approvedArtifactKey), approvedArtifactKey, $"Not an artifact of '{definition.Pipeline}'.");
        var currentIndex = definition.IndexOfStage(currentStage);
        if (currentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentStage), currentStage, $"Not a stage of '{definition.Pipeline}'.");
        }

        if (hook is not null && !string.Equals(hook.PipelineId, definition.Pipeline, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The hook serves '{hook.PipelineId}', not '{definition.Pipeline}'.", nameof(hook));
        }

        if (approved.Cascade == CascadeMode.None)
        {
            return new CascadeEffect(approved.Key, [], [], null);
        }

        var invalidated = Downstream(approved.Key, definition);
        var packStages = hook?.FallbackStages(approved.Key, invalidated) ?? [];
        var candidates = invalidated.Select(key => ProducingStage(key, definition)).OfType<string>().ToList();
        if (approved.Cascade == CascadeMode.SelfAndDownstream && ProducingStage(approved.Key, definition) is { } self)
        {
            candidates.Add(self);
        }

        foreach (var stage in packStages)
        {
            if (definition.IndexOfStage(stage) < 0)
            {
                throw new InvalidOperationException($"The invalidation hook named '{stage}', which is not a stage of '{definition.Pipeline}'.");
            }

            candidates.Add(stage);
        }

        var earliest = candidates.Count == 0 ? -1 : candidates.Min(definition.IndexOfStage);
        var fallback = earliest >= 0 && earliest < currentIndex ? definition.Stages[earliest].Id : null;
        return new CascadeEffect(approved.Key, invalidated, [.. packStages], fallback);
    }

    /// <summary>The artifacts that depend on <paramref name="artifactKey"/> (not itself), in the definition's artifact
    /// order, ignoring its <c>cascade</c>.</summary>
    public static IReadOnlyList<string> Downstream(string artifactKey, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([artifactKey]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var stage in definition.Stages)
            {
                if (stage.Output is { } output
                    && !string.Equals(output, artifactKey, StringComparison.Ordinal)
                    && stage.Inputs.Contains(current, StringComparer.Ordinal)
                    && found.Add(output))
                {
                    queue.Enqueue(output);
                }
            }
        }

        var declared = definition.Artifacts.Select(a => a.Key).Where(found.Contains).ToList();
        return [.. declared, .. found.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The first stage whose <c>output</c> is <paramref name="artifactKey"/>, or <c>null</c> (an artifact a pack
    /// job produces outside any stage).</summary>
    public static string? ProducingStage(string artifactKey, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stages.FirstOrDefault(s => string.Equals(s.Output, artifactKey, StringComparison.Ordinal))?.Id;
    }
}
