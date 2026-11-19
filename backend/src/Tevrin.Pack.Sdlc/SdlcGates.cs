using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-271, D-0.7.0-23: the SDLC's approval gate keys, which (with the engine's <see cref="EngineGates.Escalation"/>)
/// replace the <c>GateType</c> enum. Each key is the 0.6.1 wire and stored value.
/// </summary>
public static class SdlcGates
{
    public const string SpecApproval = "spec_approval";

    public const string PlanApproval = "plan_approval";

    public const string PreRelease = "pre_release";

    /// <summary>Every gate key the SDLC uses, in the order of the 0.6.1 enum: its approval gates, then the engine's
    /// <see cref="EngineGates.Escalation"/>.</summary>
    public static IReadOnlyList<string> All { get; } = [SpecApproval, PlanApproval, PreRelease, EngineGates.Escalation];

    /// <summary>Whether <paramref name="value"/> is a gate key (ordinal).</summary>
    public static bool IsKnown(string? value) => KeyNames.TryFind(All, value, out _);

    /// <summary>The gate key of a wire value, or <c>false</c> for anything else (ordinal).</summary>
    public static bool TryParse(string? value, out string type) => KeyNames.TryFind(All, value, out type);

    /// <summary>The key of a wire value; throws <see cref="FormatException"/> for anything else (as parsing the 0.6.1
    /// enum did).</summary>
    public static string Parse(string value) =>
        TryParse(value, out var type) ? type : throw new FormatException($"'{value}' is not a valid GateType.");

    /// <summary>The position of a gate key in <see cref="All"/> (the 0.6.1 enum's numeric order), or -1.</summary>
    public static int Order(string type) => KeyNames.IndexOf(All, type);
}
