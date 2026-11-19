using System.Text.Json;
using System.Text.Json.Serialization;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>An agent run's <c>output</c> (as the SDLC runner's): the answer, why it stopped, and loop warnings.</summary>
public sealed record AgentStageRunOutput(string Text, string StopReason)
{
    public IReadOnlyList<string>? Warnings { get; init; }

    public int? Continuations { get; init; }
}

public sealed partial class AgentStageRunner
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Calls the model. Every outcome finishes the agent run: success records tokens, cost, output and latency; a provider
    /// error is stored sanitized and failed, and fails the job transiently or not as the provider said (FR-25); any other
    /// exception is stored by type only (its text may quote the request); cancellation stores the run as cancelled.
    /// </summary>
    private async Task<LlmToolLoopResult> CallAsync(
        PipelineRun run, AgentRun agentRun, LlmRequest request, AgentToolExecutor? executor, ResolvedLlm resolved, CancellationToken cancellationToken)
    {
        using var firstToken = LlmFirstTokenProbe.Start(time);
        var started = time.GetTimestamp();
        var paid = default(LlmTurnTokens);
        Task<bool> OnTurn(LlmTurnTokens soFar, CancellationToken _)
        {
            paid = soFar;
            return Task.FromResult(true);
        }

        LlmToolLoopResult answer;
        try
        {
            answer = await LlmToolLoop.RunAsync(llm.ProviderFor(resolved.Provider), request, executor, cancellationToken, OnTurn);
        }
        catch (LlmProviderException ex)
        {
            var message = SecretRedaction.SanitizeProviderError(ex.Message, resolved.ApiKey);
            await FailAsync(run, agentRun, AgentRunStatus.Failed, $"{ex.ErrorCode}: {message}", started, firstToken, executor, paid);
            throw new JobFailedException(message, ex.Transient, ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            await FailAsync(run, agentRun, AgentRunStatus.Cancelled, "The run was cancelled.", started, firstToken, executor, paid);
            throw;
        }
        catch (Exception ex)
        {
            var transient = ex is HttpRequestException or TimeoutException or IOException;
            var message = $"{resolved.Provider.ToWire()} call failed unexpectedly ({ex.GetType().Name}).";
            await FailAsync(run, agentRun, AgentRunStatus.Failed, $"{EngineErrorCodes.ProviderError}: {message}", started, firstToken, executor, paid);
            throw new JobFailedException(message, transient, EngineErrorCodes.ProviderError);
        }

        var completion = answer.Completion;
        Finish(agentRun, started, firstToken, executor, completion.InputTokens, completion.OutputTokens);
        agentRun.Status = AgentRunStatus.Succeeded;
        agentRun.Output = JsonSerializer.Serialize(
            new AgentStageRunOutput(completion.Text, completion.StopReason)
            {
                Warnings = answer.Warnings.Count == 0 ? null : answer.Warnings,
                Continuations = answer.Continuations,
            },
            OutputJson);
        await hooks.OnTokensUsedAsync(run, agentRun, (long)completion.InputTokens + completion.OutputTokens, cancellationToken);
        return answer;
    }

    private async Task FailAsync(
        PipelineRun run, AgentRun agentRun, AgentRunStatus status, string error, long started, LlmFirstTokenProbe firstToken, AgentToolExecutor? executor, LlmTurnTokens paid)
    {
        Finish(agentRun, started, firstToken, executor, paid.InputTokens, paid.OutputTokens);
        agentRun.Status = status;
        agentRun.Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        if (paid.InputTokens + paid.OutputTokens > 0)
        {
            await hooks.OnTokensUsedAsync(run, agentRun, (long)paid.InputTokens + paid.OutputTokens, CancellationToken.None);
        }

        await store.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Tokens, cost, duration, latency (FR-297) and tool calls of the attempt.</summary>
    private void Finish(AgentRun agentRun, long started, LlmFirstTokenProbe firstToken, AgentToolExecutor? executor, int inputTokens, int outputTokens)
    {
        agentRun.InputTokens = inputTokens;
        agentRun.OutputTokens = outputTokens;
        agentRun.CostEstimate = AgentRunRules.EstimateCost(inputTokens, outputTokens, services.GetService(typeof(ILlmCatalog)) is ILlmCatalog catalog ? catalog.PriceOf(agentRun.Model) : null);
        agentRun.DurationMs = (long)time.GetElapsedTime(started).TotalMilliseconds;
        agentRun.LatencyMs = firstToken.LatencyMs(started);
        agentRun.CompletedAt = time.GetUtcNow();
        agentRun.ToolCalls = executor?.CallsJson() ?? "[]";
    }

    /// <summary>The findings of the output (schema errors and failed <c>validate:</c> guards), or <c>null</c> when valid.</summary>
    private async Task<string?> ValidateAsync(
        string packId, PipelineRun run, StageDefinition stage, ArtifactDefinition? artifact, string content, CancellationToken cancellationToken)
    {
        IReadOnlyList<SchemaFinding> schemaErrors = artifact is { Format: ArtifactFormat.Json, Schema: { } schema }
            ? schemas.Validate(packId, schema, content)
            : [];
        var reasons = new List<string>();
        var facts = FactBag.Empty.With(new StageOutputFact(artifact?.Key ?? string.Empty, artifact?.Format ?? ArtifactFormat.Markdown, content));
        var guardContext = new GuardContext(run.Id, run.ProjectId, stage.Id, run.ApprovalMode, facts);
        foreach (var validation in stage.Validate)
        {
            var guard = services.GetPipelineGuard(validation.Guard) ?? throw Permanent($"Guard '{validation.Guard}' is not bound.");
            var result = await guard.EvaluateAsync(guardContext, cancellationToken);
            if (!result.Ok)
            {
                reasons.AddRange(result.Reasons.Count > 0 ? result.Reasons : [$"{validation.Guard} rejected the output."]);
            }
        }

        return reasons.Count == 0 && schemaErrors.Count == 0 ? null : StageValidationRules.Findings(reasons, schemaErrors);
    }

    /// <summary>
    /// A new version of the artifact: numbered project-wide (FR-58); only open drafts of the run's own subject are
    /// superseded (§4.10), never another subject's.
    /// </summary>
    private async Task<Artifact> StoreAsync(PipelineRun run, ArtifactDefinition definition, string content, AgentRun agentRun, CancellationToken cancellationToken)
    {
        var plan = ArtifactRules.NewDraft(await store.VersionsAsync(run.ProjectId, definition.Key, cancellationToken));
        var drafts = await store.SubjectVersionsAsync(run, definition.Key, plan.SupersededVersions, cancellationToken);
        foreach (var draft in drafts.Where(d => ArtifactRules.CanTransition(d.Status, ArtifactStatus.Superseded)))
        {
            draft.Status = ArtifactStatus.Superseded;
            await hooks.OnArtifactSupersededAsync(run, draft, plan.Version, cancellationToken);
        }

        var now = time.GetUtcNow();
        var autoLock = definition.Approval == ArtifactApproval.AutoLock;
        var artifact = new Artifact
        {
            ProjectId = run.ProjectId,
            Type = definition.Key,
            Version = plan.Version,
            ParentVersion = plan.ParentVersion,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = autoLock ? ArtifactStatus.Locked : ArtifactStatus.Draft,
            ApprovedAt = autoLock ? now : null,
            CheckpointAtCreation = run.Checkpoint,
            ReleaseVersionId = AgentStageScope.ReleaseVersionOf(run),
            PipelineRunId = run.Id,
            AgentRunId = agentRun.Id,
            CreatedAt = now,
        };
        store.Add(artifact);
        await hooks.OnArtifactCreatedAsync(run, artifact, cancellationToken);
        return artifact;
    }
}
