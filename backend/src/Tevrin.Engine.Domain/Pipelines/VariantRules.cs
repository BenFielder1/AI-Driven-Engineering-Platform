using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-349: one stored variant offered for selection: its label and its artifact's id (never the content).</summary>
public sealed record VariantRef(string Label, Guid ArtifactId);

/// <summary>FR-349: an agent selector's answer, <c>{ "choice": "v2", "reason": "…" }</c>.</summary>
public sealed record VariantSelection(string Choice, string? Reason);

/// <summary>
/// FR-349, §4.67: the pure rules of multiple output variants. A <c>variants: { count: n }</c> stage runs n branches of its
/// own agent labelled <c>v1…vn</c> (the fan-out machinery, <see cref="FanOutRules.Branches"/>); a <c>variant_select</c>
/// gate or a selector agent picks one, which is promoted to the stage's plain output.
/// </summary>
public static class VariantRules
{
    /// <summary>The prefix of a variant label: <c>v1</c> … <c>vn</c>.</summary>
    public const string LabelPrefix = "v";

    /// <summary>The prompt placeholder holding a variant branch's own label (<c>{{VARIANT}}</c>).</summary>
    public const string LabelPlaceholder = "VARIANT";

    /// <summary>The run key prefix of a selector agent's run (FR-310): <c>select-&lt;key&gt;</c>.</summary>
    public const string SelectRunKeyPrefix = "select-";

    /// <summary>The label of the <paramref name="index"/>th (1-based) variant.</summary>
    public static string Label(int index) => LabelPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The labels <c>v1…vn</c> of <paramref name="stage"/>'s variants (empty without <c>variants</c>).</summary>
    public static IReadOnlyList<string> Labels(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Variants is { Count: var count } ? [.. Enumerable.Range(1, count).Select(Label)] : [];
    }

    /// <summary>Whether <paramref name="choice"/> is one of <paramref name="stage"/>'s labels (exact, ordinal).</summary>
    public static bool IsValidChoice(StageDefinition stage, string? choice) =>
        choice is not null && Labels(stage).Contains(choice, StringComparer.Ordinal);

    /// <summary>The run key of selector agent <paramref name="agentKey"/>'s run.</summary>
    public static string SelectRunKey(string agentKey) => SelectRunKeyPrefix + agentKey;

    /// <summary>
    /// The chosen variant among <paramref name="variants"/> (the newest stored version per label), or <c>null</c> when
    /// <paramref name="choice"/> is none of <paramref name="stage"/>'s labels or wasn't stored.
    /// </summary>
    public static VariantRef? Chosen(StageDefinition stage, IEnumerable<VariantRef> variants, string? choice)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return IsValidChoice(stage, choice)
            ? variants.FirstOrDefault(v => string.Equals(v.Label, choice, StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    /// An agent selector's answer: a JSON object (or one inside a fenced block) with a string <c>choice</c> and an optional
    /// <c>reason</c>. <c>null</c> when unreadable.
    /// </summary>
    public static VariantSelection? ReadSelection(string? output)
    {
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
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return new VariantSelection(choice.GetString()!.Trim(), reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// §5: a <c>variant_select</c> gate's redacted payload: the stage and each variant's label and artifact id, never the
    /// content: <c>{ "stage": "…", "variants": [{ "label": "v1", "artifactId": "…" }] }</c>.
    /// </summary>
    public static string GatePayload(string stageId, IEnumerable<VariantRef> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        var list = new JsonArray([.. variants.Select(v => (JsonNode)new JsonObject
        {
            ["label"] = v.Label,
            ["artifactId"] = v.ArtifactId.ToString(),
        })]);
        return new JsonObject { ["stage"] = stageId, ["variants"] = list }.ToJsonString();
    }

    /// <summary>The labels a <c>variant_select</c> gate's payload offers (empty when unreadable).</summary>
    public static IReadOnlyList<string> PayloadLabels(string? payload) =>
        Parse(payload)?["variants"] is JsonArray list
            ? [.. list.Select(v => v?["label"]?.GetValue<string>()).OfType<string>()]
            : [];

    /// <summary>The payload with the decider's <paramref name="choice"/> recorded (<c>"choice": "v2"</c>).</summary>
    public static string WithChoice(string? payload, string choice)
    {
        var node = Parse(payload) ?? new JsonObject();
        node["choice"] = choice;
        return node.ToJsonString();
    }

    /// <summary>The choice an approved <c>variant_select</c> gate's payload records, or <c>null</c>.</summary>
    public static string? PayloadChoice(string? payload) =>
        Parse(payload)?["choice"] is JsonValue value && value.TryGetValue<string>(out var choice) ? choice : null;

    private static JsonObject? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
