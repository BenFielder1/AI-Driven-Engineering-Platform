namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-319, D-0.7.0-43: the SDLC's stage handler names (namespaced by the pack id, FR-278), bound in the host's DI. Both are
/// declared in the <c>execution</c> stage's <c>handlers:</c> list (<c>[sdlc.execution, sdlc.build-check]</c>), with no
/// <c>handler:</c>, so the stage stays manual.
/// </summary>
public static class SdlcStageHandlerNames
{
    /// <summary>One step attempt of the current plan: the code generator, the Guardian, the commit and push (FR-31–FR-35).</summary>
    public const string Execution = "sdlc.execution";

    /// <summary>One poll of a build check, before review (FR-255, §4.47).</summary>
    public const string BuildCheck = "sdlc.build-check";

    public static IReadOnlyList<string> All { get; } = [Execution, BuildCheck];
}
