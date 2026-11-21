namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-317: the SDLC's agent tool names (namespaced by the pack id, FR-278), for a stage's <c>agent.tools</c>. They are the
/// read-only repo tools of FR-71; the model sees them as <c>list_files</c>, <c>read_file</c> and <c>search_code</c>, in
/// this order, bound in the host's DI.
/// </summary>
public static class SdlcToolNames
{
    public const string ListFiles = "sdlc.list-files";

    public const string ReadFile = "sdlc.read-file";

    public const string SearchCode = "sdlc.search-code";

    /// <summary>Every SDLC tool name, in the order the model is offered them.</summary>
    public static IReadOnlyList<string> All { get; } = [ListFiles, ReadFile, SearchCode];
}
