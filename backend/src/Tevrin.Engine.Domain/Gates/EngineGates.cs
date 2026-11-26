namespace Tevrin.Engine.Domain.Gates;

/// <summary>
/// FR-271, D-0.7.0-23: the gate keys the engine owns. Every pipeline can escalate, so <c>escalation</c> is the engine's;
/// a pack defines its own approval gates (the SDLC's are <c>Tevrin.Pack.Sdlc.SdlcGates</c>). Keys are the stored and wire
/// values.
/// </summary>
public static class EngineGates
{
    /// <summary>An escalation gate (FR-14, D3): a step or run needs a human decision.</summary>
    public const string Escalation = "escalation";

    /// <summary>FR-343–FR-349: a human picks one of a stage's variants; the gate payload holds the variant labels.</summary>
    public const string VariantSelect = "variant_select";

    /// <summary>
    /// FR-359, §4.73: a destructive tool call waits on a human, in the middle of a stage. The payload holds the tool, its
    /// tier, its redacted arguments and the agent's reason; only an Owner decides it, and no approval mode skips it.
    /// </summary>
    public const string ToolApproval = "tool_approval";

    /// <summary>Whether <paramref name="type"/> is raised by the engine itself, never as a stage's entry gate.</summary>
    public static bool IsEngineRaised(string? type) => type is Escalation or ToolApproval;
}
