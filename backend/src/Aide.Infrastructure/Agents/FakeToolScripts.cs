using Aide.Domain.Agents;
using Aide.Domain.RepoTools;

namespace Aide.Infrastructure.Agents;

/// <summary>One scripted call: the tool and its arguments as JSON.</summary>
public sealed record FakeToolCall(string Tool, string ArgumentsJson);

/// <summary>
/// The repo tool calls the Fake provider makes for one agent before answering (FR-71, D13), one call per turn, in order.
/// Register implementations in DI (<c>AddSingleton&lt;IFakeToolScript, …&gt;</c>); for an agent with several scripts the
/// last registered wins, so tests can override the built-in ones.
/// </summary>
public interface IFakeToolScript
{
    AgentKind Agent { get; }

    IReadOnlyList<FakeToolCall> Calls { get; }
}

/// <summary>A fixed <see cref="IFakeToolScript"/>.</summary>
public sealed class FakeToolScript(AgentKind agent, IReadOnlyList<FakeToolCall> calls) : IFakeToolScript
{
    public AgentKind Agent { get; } = agent;

    public IReadOnlyList<FakeToolCall> Calls { get; } = calls;
}

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
        Enum.GetValues<AgentKind>().Select(agent => (IFakeToolScript)new FakeToolScript(agent, Explore));
}
