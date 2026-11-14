using System.Text;
using Aide.Domain.Entities;
using Aide.Domain.Review;

namespace Aide.Domain.Execution;

/// <summary>
/// A failure finding of a build check's sources (from <see cref="Deployments.RolloutFindings.For"/> for the preview rollout and
/// <see cref="ExternalCheckRules.Findings"/> for the required checks, as review builds them) and the whole stored log it
/// came from (the build log tail or the check's job log; <c>null</c> when there is none), for the error summary.
/// </summary>
public sealed record BuildCheckFailureInput(ReviewFinding Finding, string? Log);

/// <summary>
/// FR-256, §4.48: the failures a build check stores (<see cref="BuildCheck.Failures"/>): one per failure finding of its
/// sources plus the timed-out sources of <see cref="BuildCheckDecision.TimedOut"/>, each with its failure id (unique,
/// <see cref="FailureIds.Disambiguate"/>), fingerprint (§4.37), message, source (<c>preview</c> or <c>check</c>), excerpt
/// (§4.38, the finding's evidence) and error summary (<see cref="BuildErrorSummary"/>). Findings without a failure id
/// (Supabase advisors) aren't build failures. Pure.
/// </summary>
public static class BuildCheckFailures
{
    public static IReadOnlyList<BuildCheckFailure> Collect(IReadOnlyList<BuildCheckFailureInput> inputs, IReadOnlyList<BuildCheckFailure>? timedOut = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var failures = inputs.Where(i => i.Finding.FailureId is not null).ToList();
        var unique = FailureIds.Disambiguate([.. failures.Select(f => f.Finding)]);
        var result = new List<BuildCheckFailure>(unique.Count + (timedOut?.Count ?? 0));
        for (var i = 0; i < unique.Count; i++)
        {
            result.Add(From(unique[i], failures[i].Log));
        }

        foreach (var t in timedOut ?? [])
        {
            if (result.All(r => r.FailureId != t.FailureId))
            {
                result.Add(t);
            }
        }

        return result;
    }

    /// <summary>One failure from a failure finding and its stored log (the excerpt is used when there is no log).</summary>
    public static BuildCheckFailure From(ReviewFinding finding, string? log)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var id = finding.FailureId ?? throw new ArgumentException("The finding has no failure id.", nameof(finding));
        return new BuildCheckFailure(
            id,
            FailureFingerprint.Of(finding),
            finding.Message,
            FailureIds.SourceOf(id) ?? FailureIds.Check,
            finding.Evidence,
            BuildErrorSummary.Lines(string.IsNullOrWhiteSpace(log) ? finding.Evidence : log));
    }

    /// <summary>FR-239: the failures as fix-step evidence (id, message, excerpt) for <see cref="FailureEvidenceSteps.Attach"/>.</summary>
    public static IReadOnlyList<FailureEvidence> Evidence(IEnumerable<BuildCheckFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var result = new List<FailureEvidence>();
        foreach (var f in failures)
        {
            if (result.All(r => r.Id != f.FailureId))
            {
                result.Add(new FailureEvidence(f.FailureId, f.Message, f.Excerpt ?? string.Empty));
            }
        }

        return result;
    }
}

/// <summary>
/// §4.48 <c>ERROR_SUMMARY</c>: every distinct line of a stored log that matches the §4.37 error pattern, in log order,
/// de-duplicated after normalisation (<see cref="FailureFingerprint.Normalise"/>: timestamps, temp paths, hex runs and
/// digits), at most <see cref="MaxLines"/> lines and <see cref="MaxChars"/> characters per failure. The log is cleaned like
/// an excerpt (§4.38: redacted, ANSI stripped, redacted again; long lines cut). Pure.
/// </summary>
public static class BuildErrorSummary
{
    public const int MaxLines = 30;

    public const int MaxChars = 3000;

    public static IReadOnlyList<string> Lines(string? log)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chars = 0;
        foreach (var raw in FailureExcerpt.LogLines(log))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !FailureExcerpt.IsErrorLine(line) || !seen.Add(FailureFingerprint.Normalise(line)))
            {
                continue;
            }

            var cost = line.Length + (result.Count == 0 ? 0 : 1);
            if (chars + cost > MaxChars)
            {
                break;
            }

            result.Add(line);
            chars += cost;
            if (result.Count >= MaxLines)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>{{ERROR_SUMMARY}}</c> input: per failure, its id and its summary lines (<c>- line</c>), or a note when the
    /// log had no error line.
    /// </summary>
    public static string Render(IEnumerable<BuildCheckFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var sb = new StringBuilder();
        foreach (var f in failures)
        {
            sb.Append(f.FailureId).Append(":\n");
            if (f.ErrorSummary.Count == 0)
            {
                sb.Append("- (no error lines in the log)\n");
            }

            foreach (var line in f.ErrorSummary)
            {
                sb.Append("- ").Append(line).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.Length == 0 ? "No failures." : sb.ToString().TrimEnd();
    }
}
