using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Gates;

/// <summary>
/// FR-360: the <c>external_action</c> gate payload of an external stage handler: the handler, its stage and stage
/// execution, the <c>stage.run</c> job that raised it (only that job sends), the exact payload to be sent, once the
/// approved send was claimed <see cref="RanAt"/>, and once the handler reported it sent <see cref="SentAt"/>.
/// </summary>
public sealed record ExternalActionGatePayload(string Handler, string Stage, Guid StageExecutionId, Guid JobId, string Payload, DateTimeOffset? RanAt)
{
    /// <summary>When the handler's send completed (saved before the stage's transition), so a redelivery only transitions.</summary>
    public DateTimeOffset? SentAt { get; init; }
}

/// <summary>
/// FR-360 (D-0.8.0-31): the pure rules of the human gate before an external action. An external handler's payload is
/// prepared before anything is sent, checked (<see cref="PayloadProblem"/>), stored on the gate as is, shown to the
/// approver and, once approved, sent exactly as stored, at most once. A payload holding a credential-shaped value
/// (<see cref="PayloadProblem"/>: high-confidence key and token formats only, never ordinary words such as "Work
/// authorization" or "the bearer of this letter") is refused rather than stored or sent: credentials belong in the
/// handler's own configuration, never in a payload a human reviews, so what is shown, stored and sent stay the same text. The decision steps are <see cref="ToolApprovalRules.Next"/>
/// (the same states, timer and claim as FR-359); nothing here reads the run's approval mode.
/// </summary>
public static partial class ExternalActionRules
{
    /// <summary>The cap on a prepared payload, in UTF-8 bytes (as stored inputs, D-0.7.0-47).</summary>
    public const int MaxPayloadBytes = AgentRunInput.MaxBytes;

    /// <summary>
    /// FR-359, FR-360: the gate a call to an offered tool of <paramref name="tier"/> waits on before it runs, or <c>null</c>
    /// when it runs straight away. An external tool waits on <c>external_action</c> whatever its tier (one gate, never two,
    /// even when it is destructive too); a destructive one on <c>tool_approval</c>.
    /// </summary>
    public static string? ApprovalGateOf(ToolTier tier, bool external) =>
        external ? EngineGates.ExternalAction
        : tier == ToolTier.Destructive ? EngineGates.ToolApproval
        : null;

    /// <summary>The shortest run secret matched by exact value (shorter ones would match ordinary words).</summary>
    private const int MinSecretLength = 8;

    /// <summary>
    /// Why a prepared payload can't go on a gate, or <c>null</c>: it is empty, over <see cref="MaxPayloadBytes"/>, holds a
    /// NUL or an unpaired surrogate (which <c>jsonb</c> can't store), contains one of <paramref name="secrets"/> (8+
    /// characters), or a credential-shaped value (<see cref="CredentialPattern"/>), or the check couldn't finish in time. The
    /// reason never quotes the payload. Unlike <see cref="SecretRedaction"/> (which masks generously, for logs), this
    /// refuses only high-confidence formats, so ordinary application text passes.
    /// </summary>
    public static string? PayloadProblem(string? payload, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "The external handler prepared an empty payload.";
        }

        if (HasUnstorableChar(payload))
        {
            return "The external handler's payload contains a NUL character or an unpaired surrogate, which can't be stored.";
        }

        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            return $"The external handler's payload is over {MaxPayloadBytes / (1024 * 1024)} MB.";
        }

        const string Credential =
            "The external handler's payload contains a credential; credentials belong in the handler's configuration, not in the payload an approver sees.";
        if (secrets.Any(secret => secret is { Length: >= MinSecretLength } && payload.Contains(secret, StringComparison.Ordinal)))
        {
            return Credential;
        }

        try
        {
            return CredentialPattern().IsMatch(payload) ? Credential : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "The external handler's payload can't be checked for credentials in time.";
        }
    }

    /// <summary>Whether <paramref name="text"/> holds a NUL or a surrogate that isn't part of a pair.</summary>
    private static bool HasUnstorableChar(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// FR-360 (D-0.8.0-31): credential-shaped values only. Provider keys by prefix with a minimum length and charset
    /// (<c>sk-</c>/<c>sk-ant-</c>/<c>sk-proj-</c> 20+, GitHub <c>ghp_</c>…/<c>github_pat_</c>, GitLab <c>glpat-</c>, Slack
    /// <c>xox?-</c>, AWS <c>AKIA</c>, Google <c>AIza</c>), JWTs (three base64url parts starting <c>eyJ</c>), PEM private
    /// keys, and a <c>Bearer</c> token of 20+ token characters. Plain words never match.
    /// </summary>
    [GeneratedRegex(
        @"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}"
        + @"|\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}"
        + @"|\bgithub_pat_[A-Za-z0-9_]{22,}"
        + @"|\bglpat-[A-Za-z0-9_\-]{20,}"
        + @"|\bxox[abposr]-[A-Za-z0-9\-]{10,}"
        + @"|\bAKIA[0-9A-Z]{16}\b"
        + @"|\bAIza[0-9A-Za-z_\-]{35}"
        + @"|\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"
        + @"|-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----"
        + @"|\b[Bb]earer\s+[A-Za-z0-9\-._~+/]{20,}=*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialPattern();

    /// <summary>The <c>external_action</c> gate payload JSON of an external handler (no <c>ranAt</c> yet).</summary>
    public static string GatePayload(string handler, string stage, Guid stageExecutionId, Guid jobId, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(handler);
        ArgumentException.ThrowIfNullOrEmpty(stage);
        ArgumentNullException.ThrowIfNull(payload);
        return new JsonObject
        {
            ["handler"] = handler,
            ["stage"] = stage,
            ["stageExecutionId"] = stageExecutionId,
            ["jobId"] = jobId,
            ["payload"] = payload,
        }.ToJsonString();
    }

    /// <summary>An external handler's gate payload, or <c>null</c> when <paramref name="json"/> isn't one (e.g. an external
    /// tool's gate, which names an agent run instead).</summary>
    public static ExternalActionGatePayload? ReadGatePayload(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject node
                || Text(node, "handler") is not { Length: > 0 } handler
                || Text(node, "stage") is not { Length: > 0 } stage
                || Text(node, "payload") is not { } payload
                || Id(node, "stageExecutionId") is not { } execution
                || Id(node, "jobId") is not { } job)
            {
                return null;
            }

            return new ExternalActionGatePayload(handler, stage, execution, job, payload, Time(node, "ranAt")) { SentAt = Time(node, "sentAt") };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The failure message of a stage whose <c>external_action</c> gate was rejected (or timed out), naming the reviewer by
    /// user id (or <see cref="ToolApprovalRules.TimerUser"/>), as the FR-359 denial does.
    /// </summary>
    public static string RejectedMessage(string stage, Guid? reviewer, string? comment)
    {
        ArgumentException.ThrowIfNullOrEmpty(stage);
        var by = ToolApprovalRules.DenialText(reviewer?.ToString() ?? ToolApprovalRules.TimerUser, comment);
        return $"The external action of {stage} was not sent: {by}.";
    }

    /// <summary>The failure message of a claimed send whose delivery stopped before its result was saved (never sent twice).</summary>
    public static string OutcomeUnknown(string stage)
    {
        ArgumentException.ThrowIfNullOrEmpty(stage);
        return $"The approved external action of {stage} was started, but the job stopped before its result was saved, so it may have been sent; it was not sent again. Check the receiving system before retrying: a retry raises a new external_action gate and sends again if approved.";
    }

    /// <summary>The gate payload <paramref name="json"/> with <c>sentAt</c>: the handler's send completed.</summary>
    public static string WithSentAt(string? json, DateTimeOffset sentAt)
    {
        JsonObject node;
        try
        {
            node = JsonNode.Parse(json ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            node = new JsonObject();
        }

        node["sentAt"] = sentAt;
        return node.ToJsonString();
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset? Time(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var at) ? at : null;

    private static Guid? Id(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<Guid>(out var id) && id != Guid.Empty ? id : null;
}
