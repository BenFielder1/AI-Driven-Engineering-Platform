using System.Globalization;
using System.Text.RegularExpressions;

namespace Aide.Domain.Releases;

/// <summary>
/// A release version (§4.10): <c>MAJOR.MINOR.PATCH[-prerelease]</c>, SemVer 2.0.0 without build metadata. Parsing strips
/// one leading <c>v</c>/<c>V</c> and surrounding whitespace, and refuses leading zeros (<c>01.2.0</c>). Ordering follows
/// SemVer 2.0.0 precedence (a pre-release is lower than its release; identifiers compare numerically, then ordinally).
/// </summary>
public sealed partial class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    /// <summary>The longest version text accepted (well beyond any real version).</summary>
    public const int MaxLength = 64;

    private readonly string[] _prerelease;

    private SemVer(long major, long minor, long patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public long Major { get; }

    public long Minor { get; }

    public long Patch { get; }

    /// <summary>The dot-separated pre-release identifiers; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease => _prerelease;

    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>Parses <paramref name="text"/> (see the type remarks); <c>false</c> for anything else.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed[0] is 'v' or 'V')
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        var match = Pattern().Match(trimmed);
        if (!match.Success
            || !long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !long.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var pre = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    /// <summary>Parses or throws <see cref="FormatException"/>.</summary>
    public static SemVer Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version (MAJOR.MINOR.PATCH[-prerelease]).");

    /// <summary>The next minor release: <c>1.1.0 → 1.2.0</c>, <c>1.1.3 → 1.2.0</c>, <c>2.0.0-beta.1 → 2.1.0</c>.</summary>
    public SemVer NextMinor() => new(Major, Minor + 1, 0, []);

    /// <summary>Normalised text (no leading <c>v</c>), e.g. <c>1.2.0-rc.1</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPrerelease ? "-" + string.Join('.', _prerelease) : string.Empty);

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        // A release ranks above any of its pre-releases.
        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var compared = CompareIdentifier(_prerelease[i], other._prerelease[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(SemVer? left, SemVer? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemVer? left, SemVer? right) => !(left == right);

    public static bool operator <(SemVer left, SemVer right) => Compare(left, right) < 0;

    public static bool operator >(SemVer left, SemVer right) => Compare(left, right) > 0;

    public static bool operator <=(SemVer left, SemVer right) => Compare(left, right) <= 0;

    public static bool operator >=(SemVer left, SemVer right) => Compare(left, right) >= 0;

    private static int Compare(SemVer left, SemVer right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    /// <summary>Numeric identifiers compare numerically and rank below alphanumeric ones, which compare in ASCII order.</summary>
    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            // No leading zeros, so a longer number is larger; equal lengths compare as text.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    // SemVer 2.0.0 (semver.org regex) without build metadata.
    [GeneratedRegex(
        @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern();
}
