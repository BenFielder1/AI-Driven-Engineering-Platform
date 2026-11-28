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

    /// <summary>
    /// FR-360: a handler or tool registered <c>external: true</c> waits on a human just before it sends, publishes, submits
    /// or pays outside Tevrin. The payload holds exactly what will be sent; only an Owner decides it, and no approval mode
    /// skips it. A tool that is both external and destructive gets this gate only (D-0.8.0-31).
    /// </summary>
    public const string ExternalAction = "external_action";

    /// <summary>Whether <paramref name="type"/> is raised by the engine itself, never as a stage's entry gate.</summary>
    public static bool IsEngineRaised(string? type) => type is Escalation or ToolApproval or ExternalAction;

    /// <summary>
    /// FR-359, FR-360: whether <paramref name="type"/> is decided while a job waits on it in the middle of a stage
    /// (<c>tool_approval</c>, <c>external_action</c>): the decision only wakes that job, and the gate never counts as the
    /// stage's entry gate or as one of its pending requirements.
    /// </summary>
    public static bool IsMidStage(string? type) => type is ToolApproval or ExternalAction;
}
