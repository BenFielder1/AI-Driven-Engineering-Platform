using System.Text.Json;
using System.Text.Json.Nodes;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-364: what a gate asks the decider to approve, read from the gate's stored payload. One shape for every gate type,
/// told apart by <c>kind</c>, with the fields of that kind set and the rest <c>null</c>:
/// <list type="bullet">
/// <item><c>variant_select</c> (FR-349): <c>stage</c>, <c>variants</c> (label, artifact id, key and version; never the
/// content) and, once decided, <c>choice</c>.</item>
/// <item><c>tool_call</c> (FR-359 <c>tool_approval</c>, FR-360 an external tool's <c>external_action</c>): <c>tool</c>,
/// <c>tier</c>, <c>arguments</c> (the redacted JSON the tool runs with), the agent's <c>reason</c> and its
/// <c>agentRunId</c>.</item>
/// <item><c>external_handler</c> (FR-360 an external handler's <c>external_action</c>): <c>handler</c>, <c>stage</c> and
/// <c>payload</c>, the exact text that is sent once approved.</item>
/// </list>
/// Internal bookkeeping (claims, send times, job and execution ids) is never shown.
/// </summary>
public sealed record PipelineGatePayloadDto
{
    public const string VariantSelectKind = "variant_select";
    public const string ToolCallKind = "tool_call";
    public const string ExternalHandlerKind = "external_handler";

    /// <summary><c>variant_select</c>, <c>tool_call</c> or <c>external_handler</c>.</summary>
    public required string Kind { get; init; }

    public string? Stage { get; init; }

    public IReadOnlyList<PipelineGateVariantDto>? Variants { get; init; }

    public string? Choice { get; init; }

    public string? Tool { get; init; }

    public string? Tier { get; init; }

    public string? Arguments { get; init; }

    public string? Reason { get; init; }

    public Guid? AgentRunId { get; init; }

    public string? Handler { get; init; }

    public string? Payload { get; init; }
}

/// <summary>FR-349, FR-364: one variant a <c>variant_select</c> gate offers; <c>artifactKey</c>/<c>artifactVersion</c>
/// fetch its content from the run's artifact endpoints.</summary>
public sealed record PipelineGateVariantDto(string Label, Guid ArtifactId, string? ArtifactKey, int? ArtifactVersion);

/// <summary>FR-364: pure reads of stored gate payloads into <see cref="PipelineGatePayloadDto"/>.</summary>
public static class PipelineGatePayloads
{
    /// <summary>The artifact ids a <c>variant_select</c> payload names (to look up their keys and versions).</summary>
    public static IReadOnlyList<Guid> VariantArtifactIds(string type, string? payload) =>
        type == EngineGates.VariantSelect && Parse(payload)?["variants"] is JsonArray list
            ? [.. list.Select(v => Id(v as JsonObject, "artifactId")).OfType<Guid>()]
            : [];

    /// <summary>
    /// The payload of a gate of <paramref name="type"/>, or <c>null</c> when it has none or it can't be read;
    /// <paramref name="artifact"/> gives a variant artifact's key and version.
    /// </summary>
    public static PipelineGatePayloadDto? Read(string type, string? payload, Func<Guid, (string Key, int Version)?> artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (Parse(payload) is not { } node)
        {
            return null;
        }

        if (type == EngineGates.VariantSelect)
        {
            var variants = node["variants"] is JsonArray list
                ? list.OfType<JsonObject>()
                    .Select(v => (Label: Text(v, "label"), Id: Id(v, "artifactId")))
                    .Where(v => v.Label is not null && v.Id is not null)
                    .Select(v => artifact(v.Id!.Value) is { } a
                        ? new PipelineGateVariantDto(v.Label!, v.Id!.Value, a.Key, a.Version)
                        : new PipelineGateVariantDto(v.Label!, v.Id!.Value, null, null))
                    .ToList()
                : [];
            return new PipelineGatePayloadDto
            {
                Kind = PipelineGatePayloadDto.VariantSelectKind,
                Stage = Text(node, "stage"),
                Variants = variants,
                Choice = VariantRules.PayloadChoice(payload),
            };
        }

        if (type is EngineGates.ToolApproval or EngineGates.ExternalAction && Text(node, "tool") is { } tool)
        {
            return new PipelineGatePayloadDto
            {
                Kind = PipelineGatePayloadDto.ToolCallKind,
                Tool = tool,
                Tier = Text(node, "tier"),
                Arguments = Text(node, "arguments"),
                Reason = Text(node, "reason"),
                AgentRunId = Id(node, "agentRunId"),
            };
        }

        if (type == EngineGates.ExternalAction && ExternalActionRules.ReadGatePayload(payload) is { } external)
        {
            return new PipelineGatePayloadDto
            {
                Kind = PipelineGatePayloadDto.ExternalHandlerKind,
                Handler = external.Handler,
                Stage = external.Stage,
                Payload = external.Payload,
            };
        }

        return null;
    }

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

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Guid? Id(JsonObject? node, string name)
    {
        if (node?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<Guid>(out var id) && id != Guid.Empty)
        {
            return id;
        }

        return value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var parsed) && parsed != Guid.Empty ? parsed : null;
    }
}
