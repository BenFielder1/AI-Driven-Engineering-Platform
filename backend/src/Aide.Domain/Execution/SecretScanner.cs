using System.Text.RegularExpressions;

namespace Aide.Domain.Execution;

/// <summary>A secret-pattern hit. Never carries the matched value, so it is safe to store and log.</summary>
public sealed record SecretFinding(string Pattern, string? Path, int Line);

/// <summary>
/// High-confidence secret patterns (D18), shared by the Guardian's secret-scan check (§4.6) and the
/// review's deterministic security check (§4.7).
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Name, Regex Regex)[] Patterns =
    [
        ("aws_access_key_id", AwsAccessKeyId()),
        ("aws_secret_access_key", AwsSecretAccessKey()),
        ("github_token", GitHubToken()),
        ("github_fine_grained_token", GitHubFineGrainedToken()),
        ("anthropic_api_key", AnthropicKey()),
        ("openai_api_key", OpenAiKey()),
        ("slack_token", SlackToken()),
        ("slack_webhook", SlackWebhook()),
        ("private_key", PrivateKey()),
        ("google_api_key", GoogleApiKey()),
        ("stripe_live_key", StripeLiveKey()),
        ("aide_api_token", AideToken()),
    ];

    public static IReadOnlyList<string> PatternNames { get; } = [.. Patterns.Select(p => p.Name)];

    /// <summary>Every pattern hit in <paramref name="content"/>, one per pattern per line.</summary>
    public static IReadOnlyList<SecretFinding> Scan(string content, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var findings = new List<SecretFinding>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (name, regex) in Patterns)
            {
                bool hit;
                try
                {
                    hit = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A line too pathological to scan in time can't be cleared, so it counts as a finding.
                    findings.Add(new SecretFinding("scan_timeout", path, i + 1));
                    continue;
                }

                if (hit)
                {
                    findings.Add(new SecretFinding(name, path, i + 1));
                }
            }
        }

        return findings;
    }

    private const int TimeoutMs = 250;

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex AwsAccessKeyId();

    [GeneratedRegex(@"aws_?secret_?access_?key[""']?\s*[:=]\s*[""']?[A-Za-z0-9/+=]{40}\b", RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex AwsSecretAccessKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex GitHubFineGrainedToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"\bsk-(?!ant-)(?:proj-|svcacct-|admin-)?[A-Za-z0-9_\-]{32,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9\-]{10,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/T[A-Z0-9]+/B[A-Z0-9]+/[A-Za-z0-9]+", RegexOptions.None, TimeoutMs)]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----", RegexOptions.None, TimeoutMs)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\b(?:sk|rk)_live_[0-9A-Za-z]{24,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex StripeLiveKey();

    [GeneratedRegex(@"\baide_pat_[A-Za-z0-9_\-]{20,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex AideToken();
}
