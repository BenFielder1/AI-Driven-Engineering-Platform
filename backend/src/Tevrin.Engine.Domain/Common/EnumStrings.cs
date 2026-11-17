using System.Text;

namespace Aide.Domain.Common;

/// <summary>
/// Stable string form of domain enums: the snake_case names used in SPEC.md, the database and the API
/// (e.g. <c>StepStatus.InProgress</c> ⇄ <c>"in_progress"</c>). Renaming an enum member changes its stored
/// value, so member names are part of the contract.
/// </summary>
public static class EnumStrings
{
    public static string ToWire<TEnum>(this TEnum value)
        where TEnum : struct, Enum =>
        Cache<TEnum>.ToWire.TryGetValue(value, out var wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Undefined {typeof(TEnum).Name} value.");

    public static bool TryParse<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        if (value is not null && Cache<TEnum>.FromWire.TryGetValue(value, out result))
        {
            return true;
        }

        result = default;
        return false;
    }

    public static TEnum Parse<TEnum>(string value)
        where TEnum : struct, Enum =>
        TryParse<TEnum>(value, out var result)
            ? result
            : throw new FormatException($"'{value}' is not a valid {typeof(TEnum).Name}.");

    /// <summary>All wire values of an enum, in declaration order.</summary>
    public static IReadOnlyList<string> Values<TEnum>()
        where TEnum : struct, Enum =>
        Cache<TEnum>.Ordered;

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var startsWord = i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (startsWord)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static class Cache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly Dictionary<TEnum, string> ToWire = Enum.GetValues<TEnum>()
            .ToDictionary(v => v, v => ToSnakeCase(v.ToString()));

        public static readonly Dictionary<string, TEnum> FromWire = ToWire
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

        public static readonly IReadOnlyList<string> Ordered = [.. Enum.GetValues<TEnum>().Select(v => ToWire[v])];
    }
}
