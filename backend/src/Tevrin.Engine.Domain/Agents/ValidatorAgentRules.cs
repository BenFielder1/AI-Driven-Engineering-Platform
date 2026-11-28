using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>FR-350: a validator finding's severity. <c>error</c> rejects the output; <c>warning</c> is kept only.</summary>
public enum ValidatorSeverity
{
    Error = 0,
    Warning = 1,
}

/// <summary>FR-350: one finding of validator agent <paramref name="Agent"/> on a stage output.</summary>
public sealed record ValidatorFinding(string Agent, string? Path, string? Claim, string Reason, ValidatorSeverity Severity);

/// <summary>FR-350: a validator agent's verdict: <c>pass</c> or <c>reject</c>, and its findings.</summary>
public sealed record ValidatorVerdict(bool Rejected, IReadOnlyList<ValidatorFinding> Findings)
{
    /// <summary>The findings with severity <c>error</c>.</summary>
    public IEnumerable<ValidatorFinding> Errors => Findings.Where(f => f.Severity == ValidatorSeverity.Error);

    /// <summary>
    /// Whether the output is rejected: an <c>error</c> finding, or a <c>reject</c> verdict. <c>warning</c> findings alone
    /// never reject.
    /// </summary>
    public bool Rejects => Rejected || Errors.Any();
}

/// <summary>
/// FR-350, §4.55: the pure rules of validator agents (<c>validate: [{ agent: &lt;key&gt; }]</c>). The agent answers
/// <c>{ "verdict": "pass" | "reject", "findings": [{ "path"?, "claim"?, "reason", "severity": "error" | "warning" }] }</c>,
/// checked here against that engine schema; its rejecting findings become the attempt's findings exactly like a failed
/// guard's reasons (retry with feedback, cap, <c>then</c>).
/// </summary>
public static class ValidatorAgentRules
{
    /// <summary>The run key prefix of a validator agent's run (FR-310): <c>validate-&lt;key&gt;</c>.</summary>
    public const string RunKeyPrefix = "validate-";

    /// <summary>The prompt placeholder holding the stage output under validation (<c>{{OUTPUT}}</c>).</summary>
    public const string OutputPlaceholder = "OUTPUT";

    private static readonly string[] Properties = ["verdict", "findings"];
    private static readonly string[] FindingProperties = ["path", "claim", "reason", "severity"];

    /// <summary>The run key of validator agent <paramref name="agentKey"/>'s run.</summary>
    public static string RunKey(string agentKey) => RunKeyPrefix + agentKey;

    /// <summary>
    /// Reads validator <paramref name="agentKey"/>'s answer: a JSON object (or one inside a fenced block) matching the
    /// verdict schema. <c>null</c> when it isn't JSON or breaks the schema (<c>VALIDATOR_INVALID_OUTPUT</c>).
    /// </summary>
    public static ValidatorVerdict? Read(string agentKey, string? output)
    {
        ArgumentNullException.ThrowIfNull(agentKey);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var text = output.Trim();
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return Read(agentKey, doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValidatorVerdict? Read(string agentKey, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(p => !Properties.Contains(p.Name, StringComparer.Ordinal))
            || !root.TryGetProperty("verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String
            || verdict.GetString() is not ("pass" or "reject")
            || !root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var read = new List<ValidatorFinding>();
        foreach (var item in findings.EnumerateArray())
        {
            if (ReadFinding(agentKey, item) is not { } finding)
            {
                return null;
            }

            read.Add(finding);
        }

        return new ValidatorVerdict(verdict.GetString() == "reject", read);
    }

    private static ValidatorFinding? ReadFinding(string agentKey, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || item.EnumerateObject().Any(p => !FindingProperties.Contains(p.Name, StringComparer.Ordinal))
            || !item.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reason.GetString())
            || !item.TryGetProperty("severity", out var severity) || severity.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        ValidatorSeverity? level = severity.GetString() switch
        {
            "error" => ValidatorSeverity.Error,
            "warning" => ValidatorSeverity.Warning,
            _ => null,
        };
        if (level is null || !OptionalString(item, "path", out var path) || !OptionalString(item, "claim", out var claim))
        {
            return null;
        }

        return new ValidatorFinding(agentKey, path, claim, reason.GetString()!.Trim(), level.Value);
    }

    private static bool OptionalString(JsonElement item, string name, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    /// <summary>
    /// The attempt's findings from a rejecting verdict, one per line as a guard's reasons
    /// (<c>&lt;agent&gt;: &lt;path&gt;: &lt;reason&gt;</c>); a <c>reject</c> verdict without an <c>error</c> finding gives one
    /// generic line. Empty when the verdict doesn't reject.
    /// </summary>
    public static IReadOnlyList<string> Reasons(ValidatorVerdict verdict, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (!verdict.Rejects)
        {
            return [];
        }

        var errors = verdict.Errors.Select(Reason).ToList();
        return errors.Count > 0 ? errors : [$"{agentKey}: rejected the output."];
    }

    private static string Reason(ValidatorFinding f)
    {
        var at = f.Path ?? f.Claim;
        return at is null ? $"{f.Agent}: {f.Reason}" : $"{f.Agent}: {at}: {f.Reason}";
    }

    /// <summary>The findings stored on the attempt's agent run: a JSON array of <c>{ agent, path?, claim?, reason, severity }</c>.</summary>
    public static string StoredFindings(IEnumerable<ValidatorFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var list = new JsonArray();
        foreach (var f in findings)
        {
            var o = new JsonObject { ["agent"] = f.Agent };
            if (f.Path is not null)
            {
                o["path"] = f.Path;
            }

            if (f.Claim is not null)
            {
                o["claim"] = f.Claim;
            }

            o["reason"] = f.Reason;
            o["severity"] = f.Severity == ValidatorSeverity.Error ? "error" : "warning";
            list.Add(o);
        }

        return list.ToJsonString();
    }
}
