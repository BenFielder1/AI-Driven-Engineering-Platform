using Tevrin.Domain.RepoTools;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Infrastructure.Agents;

/// <summary>Ready-made Fake scripts (FR-71) for the built-in Fake agents, E2E and tests.</summary>
public static class FakeToolScripts
{
    /// <summary>What every agent does by default: look at the repository root, then read the README.</summary>
    public static IReadOnlyList<FakeToolCall> Explore { get; } =
    [
        new(RepoToolNames.ListFiles, """{"path":".","depth":2}"""),
        new(RepoToolNames.ReadFile, """{"path":"README.md","startLine":1,"endLine":200}"""),
    ];

    /// <summary>FR-71 acceptance: an attempt to read outside the repository (returns <c>UNSAFE_PATH</c>).</summary>
    public static IReadOnlyList<FakeToolCall> PathEscape { get; } =
    [
        new(RepoToolNames.ReadFile, """{"path":"../../etc/passwd"}"""),
    ];

    /// <summary>FR-71 acceptance: <paramref name="count"/> calls, enough to hit the per-run call limit (26 or more).</summary>
    public static IReadOnlyList<FakeToolCall> Many(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => new FakeToolCall(RepoToolNames.ListFiles, """{"path":".","depth":1}"""))];

    /// <summary>The default scripts: <see cref="Explore"/> for every agent.</summary>
    public static IEnumerable<IFakeToolScript> Defaults() =>
        SdlcAgents.All.Select(agent => (IFakeToolScript)new FakeToolScript(agent, Explore));
}
