namespace Tevrin.Engine.Runtime.Llm;

/// <summary>One scripted call: the tool and its arguments as JSON.</summary>
public sealed record FakeToolCall(string Tool, string ArgumentsJson);

/// <summary>
/// The repo tool calls the Fake provider makes for one agent before answering (FR-71, D13), one call per turn, in order.
/// Register implementations in DI (<c>AddSingleton&lt;IFakeToolScript, …&gt;</c>); for an agent with several scripts the
/// last registered wins, so tests can override the built-in ones.
/// </summary>
public interface IFakeToolScript
{
    string Agent { get; }

    IReadOnlyList<FakeToolCall> Calls { get; }
}

/// <summary>A fixed <see cref="IFakeToolScript"/>.</summary>
public sealed class FakeToolScript(string agent, IReadOnlyList<FakeToolCall> calls) : IFakeToolScript
{
    public string Agent { get; } = agent;

    public IReadOnlyList<FakeToolCall> Calls { get; } = calls;
}
