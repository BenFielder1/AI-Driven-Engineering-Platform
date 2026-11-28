using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>FR-298–FR-303: pure mappings from engine models to the generic API's DTOs.</summary>
public static class PipelineDtoMapper
{
    /// <summary>The short form of a definition hash shown in the UI.</summary>
    public const int ShortHashLength = 12;

    public static string ShortHash(string hash) =>
        hash.Length <= ShortHashLength ? hash : hash[..ShortHashLength];

    /// <summary>
    /// FR-299: the definition summary. FR-362, FR-363: with its run control, approval modes, triggers, follow-ups, input
    /// artifacts and their schemas (<paramref name="schemaOf"/>: a pack schema's JSON by name, or <c>null</c>) and each
    /// stage's constructs.
    /// </summary>
    public static PipelineDefinitionDto ToDefinitionDto(
        PipelineDefinition definition, string hash, string packId, RunControl runControl = RunControl.Engine, Func<string, string?>? schemaOf = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PipelineDefinitionDto(
            definition.Pipeline,
            definition.Title,
            definition.Version,
            hash,
            packId,
            definition.Start,
            [.. definition.Stages.Select(s => ToStageDto(definition, s))],
            [.. definition.Artifacts.Select(a => new PipelineArtifactDefinitionDto(a.Key, a.Title, a.Format)
            {
                Scope = a.Scope,
                Input = a.Input,
                Schema = a.Schema is { } name && schemaOf?.Invoke(name) is { } json ? SchemaElement(json) : null,
            })],
            [.. definition.Statuses.Select(s => new PipelineStatusDefinitionDto(s.Name, s.Generic))])
        {
            RunControl = runControl,
            ApprovalModes = definition.ApprovalModes,
            DefaultApprovalMode = definition.DefaultApprovalMode,
            Triggers = [.. definition.DeclaredTriggers.Select(t => new PipelineTriggerDefinitionDto(t.Kind, t.Input))],
            FollowUps = [.. definition.FollowUps.Select(f => new PipelineFollowUpDefinitionDto(f.Stage, f.After))],
        };
    }

    /// <summary>A schema document as JSON, or <c>null</c> when it doesn't parse (the registry rejects such packs at load).</summary>
    private static JsonElement? SchemaElement(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A stage and its ways out: the implicit next stage (when it has no <c>on_success</c>), <c>on_success</c>,
    /// <c>on_reject</c> and <c>loop</c>.</summary>
    public static PipelineStageDefinitionDto ToStageDto(PipelineDefinition definition, StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(stage);
        var transitions = new List<PipelineStageTransitionDto>();
        if (stage.OnSuccess is { } onSuccess)
        {
            transitions.Add(new PipelineStageTransitionDto("on_success", onSuccess, null, null, null, null));
        }
        else if (definition.SuccessorOf(stage.Id) is { } next)
        {
            transitions.Add(new PipelineStageTransitionDto("next", next, null, null, null, null));
        }

        if (stage.OnReject is { } reject)
        {
            transitions.Add(new PipelineStageTransitionDto("on_reject", reject.To, null, null, reject.MaxIterations?.Value, reject.MaxIterations?.Setting));
        }

        if (stage.Loop is { } loop)
        {
            transitions.Add(new PipelineStageTransitionDto("loop", loop.To, loop.On, loop.EffectiveTitle, loop.MaxIterations?.Value, loop.MaxIterations?.Setting));
        }

        return new PipelineStageDefinitionDto(
            stage.Id,
            stage.Title,
            stage.Kind,
            stage.Status,
            stage.Agent?.Key,
            stage.Inputs,
            stage.Output,
            stage.Gate is { } gate ? new PipelineStageGateDto(gate.Type, gate.Modes, gate.Approves) : null,
            transitions)
        {
            Branches = stage.Parallel is { } parallel
                ? [.. parallel.Branches.Select((stages, i) => new PipelineBranchDefinitionDto(
                    RunDriverRules.BranchId(i), [.. stages.Select(s => ToStageDto(definition, s))]))]
                : null,
            FanOut = PipelineConstructDtos.FanOut(stage),
            Variants = PipelineConstructDtos.Variants(stage),
            Map = PipelineConstructDtos.Map(stage),
            Conversation = PipelineConstructDtos.Conversation(stage),
            Routes = stage.Routes,
            Veto = stage.Veto.Count > 0,
            OnVetoTo = stage.OnVeto?.To,
        };
    }

    /// <summary>FR-298: a prompt comparison row.</summary>
    public static PromptStatsDto ToDto(PromptStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new PromptStatsDto(
            stats.Agent, stats.PromptVersion, stats.Runs, stats.AvgInputTokens, stats.AvgOutputTokens, stats.AvgCost,
            stats.AvgLatencyMs, stats.AvgDurationMs, stats.FailureRate,
            [.. stats.Outcomes.Select(o => new PromptOutcomeDto(o.Metric, o.MeanScore, o.Count))]);
    }

    /// <summary>FR-301: a gate of a run; <paramref name="artifactKey"/>/<paramref name="artifactVersion"/> describe its
    /// gated artifact (when it has one).</summary>
    public static PipelineGateDto ToGateDto(ApprovalGate gate, Guid runId, PipelineDefinition? definition, string? artifactKey, int? artifactVersion)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var stage = definition?.Stages.FirstOrDefault(s => string.Equals(s.Gate?.Type, gate.Type, StringComparison.Ordinal));
        return new PipelineGateDto(
            gate.Id,
            runId,
            gate.ProjectId,
            gate.Type,
            KeyWords.Humanise(gate.Type),
            stage?.Id,
            gate.Status,
            gate.ArtifactId,
            artifactKey,
            artifactVersion,
            gate.CreatedAt,
            gate.DecidedAt,
            gate.ReviewerId,
            gate.Comment,
            gate.Decision,
            gate.TargetCheckpoint,
            gate.Overdue);
    }

    /// <summary>FR-303: the text of a stored agent output: the <c>text</c> of the JSON envelope both runners write, else
    /// the stored value as it is.</summary>
    public static string? OutputText(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || stored[0] != '{')
        {
            return stored;
        }

        try
        {
            using var document = JsonDocument.Parse(stored);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : stored;
        }
        catch (JsonException)
        {
            return stored;
        }
    }
}
