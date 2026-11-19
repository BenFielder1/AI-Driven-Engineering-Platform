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
}
