using System.Globalization;
using System.Text.RegularExpressions;
using Aide.Domain.Execution;
using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-126: the composition's deterministic review findings as the review job records them: every check the packs register
/// (<see cref="StackReviewChecks.Run"/>), without the <c>no_committed_secrets</c> findings v1's secret scan
/// (<see cref="DeterministicChecks.SecretScan"/>) already reported for the same file and line, so one secret is penalised once.
/// </summary>
public static partial class StackReviewFindings
{
    /// <summary>The message prefix of a <c>no_committed_secrets</c> finding.</summary>
    public const string CommittedSecretPrefix = "Possible committed secret (";

    /// <summary>The composition's findings, minus the committed-secret ones already in <paramref name="v1Findings"/>.</summary>
    public static IReadOnlyList<ReviewFinding> Run(StackResolution resolution, StackReviewInput input, IReadOnlyList<ReviewFinding> v1Findings)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(v1Findings);
        return WithoutDuplicateSecrets(StackReviewChecks.Run(resolution, input), v1Findings);
    }

    /// <summary>
    /// <paramref name="stackFindings"/> without each <c>no_committed_secrets</c> finding whose file and line already have a
    /// critical security finding in <paramref name="v1Findings"/> (v1's secret scan: "Possible … at line n.").
    /// </summary>
    public static IReadOnlyList<ReviewFinding> WithoutDuplicateSecrets(IReadOnlyList<ReviewFinding> stackFindings, IReadOnlyList<ReviewFinding> v1Findings)
    {
        ArgumentNullException.ThrowIfNull(stackFindings);
        ArgumentNullException.ThrowIfNull(v1Findings);
        var scanned = v1Findings
            .Where(f => f.Category == ReviewCategory.Security && f.Severity == Severity.Critical && f.Path is not null && Line(f.Message) is not null)
            .Select(f => (PathRules.Normalize(f.Path!), Line(f.Message)!.Value))
            .ToHashSet();
        return
        [
            .. stackFindings.Where(f => !(f.Message.StartsWith(CommittedSecretPrefix, StringComparison.Ordinal)
                && f.Path is not null
                && Line(f.Message) is { } line
                && scanned.Contains((PathRules.Normalize(f.Path), line)))),
        ];
    }

    private static int? Line(string message) =>
        LinePattern().Match(message) is { Success: true } m && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"\bat line (?<n>\d+)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinePattern();
}
