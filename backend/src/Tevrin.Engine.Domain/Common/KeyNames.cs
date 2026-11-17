namespace Tevrin.Engine.Domain.Common;

/// <summary>
/// FR-269–FR-271: helpers for string keys (agents, artifacts, gates), which are snake_case like the enum wire values they
/// replace (<see cref="EnumStrings"/>).
/// </summary>
public static class KeyNames
{
    /// <summary>The PascalCase name of a snake_case key (<c>code_review</c> → <c>CodeReview</c>): the name the key's enum
    /// member had, still accepted where configuration used it (FR-274).</summary>
    public static string ToPascalCase(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Concat(key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>The key in <paramref name="keys"/> equal to <paramref name="value"/> (ordinal), if any.</summary>
    public static bool TryFind(IReadOnlyList<string> keys, string? value, out string key)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var candidate in keys)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                key = candidate;
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    /// <summary>The position of <paramref name="key"/> in <paramref name="keys"/> (ordinal), or -1. Keys that replaced an
    /// enum keep its declaration order, so this is the order the enum's numeric values gave.</summary>
    public static int IndexOf(IReadOnlyList<string> keys, string? key)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (var i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A configuration name for one of <paramref name="keys"/>: the key itself (<c>code_review</c>, ordinal) or its
    /// PascalCase name in any case, trimmed (<c>CodeReview</c>, <c>codereview</c>), as enum-name binding accepted.
    /// </summary>
    public static bool TryParseName(IReadOnlyList<string> keys, string? value, out string key)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (value is null)
        {
            key = string.Empty;
            return false;
        }

        var trimmed = value.Trim();
        foreach (var candidate in keys)
        {
            if (string.Equals(ToPascalCase(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                return true;
            }
        }

        return TryFind(keys, value, out key);
    }
}
