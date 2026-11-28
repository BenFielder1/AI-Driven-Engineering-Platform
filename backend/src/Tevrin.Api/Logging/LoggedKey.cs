using Tevrin.Engine.Domain.Common;

namespace Tevrin.Api.Logging;

/// <summary>
/// FR-269–FR-271: an agent, artifact or gate key as a log value. 0.6.1 logged the <c>AgentKind</c>, <c>ArtifactType</c>
/// and <c>GateType</c> enum values, which render by member name (<c>PreRelease</c>, <c>TechnicalSpecification</c>); this
/// renders a key the same way (<see cref="KeyNames.ToPascalCase"/>), so log messages and their state are unchanged.
/// The conversion is lazy: the name is only built when a log line is written.
/// </summary>
public readonly record struct LoggedKey(string Key)
{
    public static implicit operator LoggedKey(string key) => new(key);

    public static LoggedKey FromString(string key) => new(key);

    public override string ToString() => KeyNames.ToPascalCase(Key);
}
