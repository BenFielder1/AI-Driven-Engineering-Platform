using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-282, §4.52: the fixed messages of the built-in entry requirements and the entry gate, and their checks. The wording
/// is 0.6.1's (FR-290: the SDLC's entry checks keep their exact text), made generic: it names the definition's artifact
/// titles and gate keys, never a pack's keys.
/// </summary>
public static class BuiltInRequirements
{
    /// <summary>
    /// <c>artifact_locked</c> unmet: "The {title, first letter lower-cased} is not approved." (title "Technical
    /// specification" → "The technical specification is not approved.").
    /// </summary>
    public static string ArtifactNotLocked(string artifactTitle)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactTitle);
        return $"The {LowerFirst(artifactTitle)} is not approved.";
    }

    /// <summary><c>no_pending_gates</c> unmet: "Other gates are pending: {keys, comma-separated}."</summary>
    public static string OtherGatesPending(IEnumerable<string> gateTypes)
    {
        ArgumentNullException.ThrowIfNull(gateTypes);
        return $"Other gates are pending: {string.Join(", ", gateTypes)}.";
    }

    /// <summary>The entry gate (FR-283) is required and not approved: "The {gate key} gate is not approved."</summary>
    public static string GateNotApproved(string gateType)
    {
        ArgumentException.ThrowIfNullOrEmpty(gateType);
        return $"The {gateType} gate is not approved.";
    }

    /// <summary>
    /// The unmet reason of a built-in requirement, or <c>null</c> when it's met. <paramref name="lockedArtifacts"/> are
    /// the keys whose latest version is locked; <paramref name="pendingGates"/> the types of the run's pending gates, in
    /// the order they're listed (duplicates are listed once). A <see cref="GuardRequirement"/> isn't built in: it throws
    /// <see cref="ArgumentException"/> (guards are evaluated through <see cref="IGuard"/>).
    /// </summary>
    public static string? Unmet(
        PipelineDefinition definition,
        StageRequirement requirement,
        IReadOnlyCollection<string> lockedArtifacts,
        IEnumerable<string> pendingGates)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(lockedArtifacts);
        ArgumentNullException.ThrowIfNull(pendingGates);

        switch (requirement)
        {
            case ArtifactLockedRequirement locked:
                if (lockedArtifacts.Contains(locked.ArtifactKey, StringComparer.Ordinal))
                {
                    return null;
                }

                var title = definition.FindArtifact(locked.ArtifactKey)?.Title;
                return ArtifactNotLocked(string.IsNullOrEmpty(title) ? locked.ArtifactKey : title);

            case NoPendingGatesRequirement noPending:
                var others = pendingGates
                    .Where(g => !noPending.Except.Contains(g, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return others.Count == 0 ? null : OtherGatesPending(others);

            default:
                throw new ArgumentException($"'{requirement.GetType().Name}' is not a built-in requirement.", nameof(requirement));
        }
    }

    /// <summary>
    /// §4.53: the gate that must be approved to enter <paramref name="stage"/> in <paramref name="mode"/>, or <c>null</c>
    /// when entry needs none.
    /// </summary>
    public static string? RequiredGate(StageDefinition stage, ApprovalMode mode)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Gate is { } gate && gate.Modes.Contains(mode) ? gate.Type : null;
    }

    private static string LowerFirst(string text) =>
        char.IsUpper(text[0]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
