using System.CommandLine.Parsing;
using System.Globalization;

namespace Aide.Cli.Commands;

/// <summary>Custom parsers that report bad values as parse errors (exit code 2) instead of throwing.</summary>
internal static class Parsers
{
    /// <summary>An integer in [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static Func<ArgumentResult, int> IntInRange(string name, int min, int max) => result =>
    {
        var raw = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        result.AddError(max == int.MaxValue
            ? string.Create(CultureInfo.InvariantCulture, $"{name} must be a whole number of at least {min} (got '{raw}').")
            : string.Create(CultureInfo.InvariantCulture, $"{name} must be a whole number from {min} to {max} (got '{raw}')."));
        return default;
    };
}
