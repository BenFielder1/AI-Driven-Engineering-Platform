namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// Other wire values still read as this enum member (<see cref="EnumStrings"/>): the old form of a renamed member that
/// is stored or sent by clients. Aliases are read only; <see cref="EnumStrings.ToWire{TEnum}"/> always writes the
/// snake_case member name.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class WireAliasAttribute(params string[] aliases) : Attribute
{
    /// <summary>The other values read as this member.</summary>
    public IReadOnlyList<string> Aliases { get; } = aliases;
}
