using System.Text;
using Aide.Domain.Common;

namespace Aide.Domain.Projects;

/// <summary>FR-5, FR-7: project naming and editable-field rules.</summary>
public static class ProjectRules
{
    /// <summary>Longest kebab-case name part of a slug (the suffix is added after it).</summary>
    public const int SlugBaseMaxLength = 50;

    /// <summary>Hex characters of the project id appended to the slug.</summary>
    public const int SlugSuffixLength = 6;

    /// <summary>Hex characters used when the short suffix collides with an existing slug.</summary>
    public const int SlugLongSuffixLength = 12;

    public const string SlugFallback = "project";

    public const string CreatedReason = "project created";

    /// <summary>
    /// <c>kebab(name)</c> (lowercase ASCII letters and digits, accented Latin letters folded to ASCII, other runs become one '-', at most
    /// <see cref="SlugBaseMaxLength"/> characters) + '-' + the last <paramref name="suffixLength"/> hex characters of
    /// the id (the random part of a UUIDv7). A name with no ASCII letters or digits uses <see cref="SlugFallback"/>.
    /// </summary>
    public static string Slug(string name, Guid id, int suffixLength = SlugSuffixLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(suffixLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(suffixLength, 32);

        var sb = new StringBuilder(Math.Min(name.Length, SlugBaseMaxLength));
        var pendingDash = false;
        foreach (var c in name)
        {
            var folded = Fold(c);
            if (folded is null)
            {
                // Combining marks (accents written as separate characters) are dropped.
                continue;
            }

            if (folded.Length == 0)
            {
                // Anything else separates words.
                pendingDash = true;
                continue;
            }

            if (pendingDash && sb.Length > 0)
            {
                sb.Append('-');
            }

            pendingDash = false;
            sb.Append(folded);
            if (sb.Length >= SlugBaseMaxLength)
            {
                break;
            }
        }

        var baseSlug = sb.ToString(0, Math.Min(sb.Length, SlugBaseMaxLength)).TrimEnd('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = SlugFallback;
        }

        var hex = id.ToString("N");
        return $"{baseSlug}-{hex[^suffixLength..]}";
    }

    // Latin-1 Supplement (U+00C0–U+00FF) and Latin Extended-A (U+0100–U+017F) folded to lowercase ASCII, one
    // character per code point: '-' separates words, '*' is a two-letter fold (see MultiLetterFolds). An explicit
    // table rather than Unicode normalization, which the app can't rely on (InvariantGlobalization is on).
    private const string Latin1Folds = "aaaaaa*ceeeeiiii" + "dnooooo-ouuuuy**" + "aaaaaa*ceeeeiiii" + "dnooooo-ouuuuy*y";
    private const string LatinExtendedAFolds =
        "aaaaaa" + "cccccccc" + "dddd" + "eeeeeeeeee" + "gggggggg" + "hhhh" + "iiiiiiiiii" + "**" + "jj" + "kkk"
        + "llllllllll" + "nnnnnnnnn" + "oooooo" + "**" + "rrrrrr" + "ssssssss" + "tttttt" + "uuuuuuuuuuuu" + "ww" + "yyy"
        + "zzzzzz" + "s";

    private static string MultiLetterFold(char c) => c switch
    {
        'Æ' or 'æ' => "ae",
        'Þ' or 'þ' => "th",
        'ß' => "ss",
        'Ĳ' or 'ĳ' => "ij",
        'Œ' or 'œ' => "oe",
        _ => string.Empty,
    };

    /// <summary>The lowercase ASCII letters/digits a character contributes to a slug: empty for a word separator,
    /// <c>null</c> for a combining mark (dropped).</summary>
    private static string? Fold(char c)
    {
        if (char.IsAsciiLetterOrDigit(c))
        {
            return char.ToLowerInvariant(c).ToString();
        }

        var table = c switch
        {
            >= 'À' and <= 'ÿ' => (Folds: Latin1Folds, Index: c - 'À'),
            >= 'Ā' and <= 'ſ' => (Folds: LatinExtendedAFolds, Index: c - 'Ā'),
            _ => (Folds: string.Empty, Index: -1),
        };
        if (table.Index >= 0)
        {
            return table.Folds[table.Index] switch
            {
                '-' => string.Empty,
                '*' => MultiLetterFold(c),
                var letter => letter.ToString(),
            };
        }

        return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark
            ? null
            : string.Empty;
    }

    /// <summary>Checks the parts of a new project that annotations can't: non-blank name and idea, and a threshold
    /// with at most two decimal places (stored as <c>numeric(3,2)</c>).</summary>
    public static Result ValidateNew(string? name, string? idea, decimal complianceThreshold)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("name: A project name is required.");
        }

        if (string.IsNullOrWhiteSpace(idea))
        {
            problems.Add("idea: Describe the idea.");
        }

        AddThresholdProblem(problems, complianceThreshold);
        return ToResult(problems, "The project is invalid.");
    }

    /// <summary>Checks a partial update (FR-7): a sent name isn't blank, the threshold has two decimals at most, and
    /// a token budget isn't both set and cleared.</summary>
    public static Result ValidateUpdate(string? name, decimal? complianceThreshold, long? tokenBudget, bool clearTokenBudget)
    {
        var problems = new List<string>();
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            problems.Add("name: A project name can't be blank.");
        }

        if (complianceThreshold is { } threshold)
        {
            AddThresholdProblem(problems, threshold);
        }

        if (tokenBudget is not null && clearTokenBudget)
        {
            problems.Add("tokenBudget: Send either tokenBudget or clearTokenBudget, not both.");
        }

        return ToResult(problems, "The project update is invalid.");
    }

    internal static Result ToResult(List<string> problems, string title) =>
        problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, title) { Details = problems });

    private static void AddThresholdProblem(List<string> problems, decimal threshold)
    {
        if (decimal.Round(threshold, 2) != threshold)
        {
            problems.Add("complianceThreshold: Use at most two decimal places.");
        }
    }
}
