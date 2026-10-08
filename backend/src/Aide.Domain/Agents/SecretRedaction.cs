using System.Text.RegularExpressions;

namespace Aide.Domain.Agents;

/// <summary>
/// NFR-SEC: provider error text is stored on runs, pushed live in <c>RUN_FAILED</c> events and logged, so anything that
/// looks like a credential is masked first: the caller's known secrets (exact values, and their whitespace-separated
/// parts), <c>sk-…</c> / <c>sk-ant-…</c> keys (including providers' own masked echoes such as <c>sk-proj-****abcd</c>),
/// <c>Bearer …</c> tokens and <c>x-api-key</c>/<c>api_key</c>/<c>authorization</c> assignments.
/// </summary>
public static partial class SecretRedaction
{
    public const string Mask = "[redacted]";

    /// <summary>The longest provider error text kept after redaction.</summary>
    public const int MaxProviderErrorLength = 500;

    /// <summary>Shortest secret part masked by exact value (shorter fragments would mask ordinary words).</summary>
    private const int MinSecretPartLength = 6;

    /// <summary>Masks credentials in <paramref name="text"/>.</summary>
    public static string Redact(string text, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);
        var result = text;
        var parts = secrets
            .Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(s => SeparatorPattern().Split(s!).Append(s!))
            .Where(s => s.Length >= MinSecretPartLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length);
        foreach (var part in parts)
        {
            result = result.Replace(part, Mask, StringComparison.Ordinal);
        }

        result = KeyPattern().Replace(result, Mask);
        result = BearerPattern().Replace(result, m => m.Groups["scheme"].Value + " " + Mask);
        return AssignmentPattern().Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
    }

    /// <summary>
    /// Provider error text as stored and logged: redacted (see <see cref="Redact"/>), on one line, and at most
    /// <see cref="MaxProviderErrorLength"/> characters.
    /// </summary>
    public static string SanitizeProviderError(string text, params string?[] secrets)
    {
        var redacted = Redact(text, secrets);
        var oneLine = string.Concat(redacted.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return oneLine.Length > MaxProviderErrorLength ? oneLine[..MaxProviderErrorLength] + "…" : oneLine;
    }

    /// <summary>True when <paramref name="value"/> contains whitespace or a control character.</summary>
    public static bool HasWhitespaceOrControl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    [GeneratedRegex(@"[\s\p{Cc}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SeparatorPattern();

    [GeneratedRegex(@"\bsk-(?:[A-Za-z0-9_\-\*…]|\.(?=[A-Za-z0-9_\-\*…])){3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"(?<scheme>\b[Bb]earer)\s+[^\s""',;]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<name>\b(?:x-api-key|api[_-]?key|authorization|access[_-]?token)\b)(?<sep>[""']?\s*[:=]\s*[""']?)(?!\[redacted\])[^\s""',;]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AssignmentPattern();
}
