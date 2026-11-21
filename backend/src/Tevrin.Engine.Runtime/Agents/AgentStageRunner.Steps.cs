using System.Text.Json;
using System.Text.Json.Serialization;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// An agent run's <c>output</c> (as the SDLC runner's): the answer, why it stopped, loop warnings, and (FR-312) the output
/// token limit the call used when the host set one.
/// </summary>
public sealed record AgentStageRunOutput(string Text, string StopReason)
{
    public IReadOnlyList<string>? Warnings { get; init; }

    public int? MaxOutputTokens { get; init; }

    public int? Continuations { get; init; }
}

public sealed partial class AgentStageRunner
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Step 6: calls the model. Success records tokens, cost, output and latency. A provider error is stored sanitized and
    /// goes to <see cref="AgentStageExtension.OnCallFailedAsync"/> (FR-315: fail, or stay <c>running</c> for the queue);
    /// any other exception is stored by type only (its text may quote the request); a tool loop stopped between turns is
    /// <c>BUDGET_EXCEEDED</c> (FR-26). Cancellation stores the run as cancelled, except under
    /// <see cref="AgentRunPolicy.CommitRunSeparately"/> or for a run under the caller's id, where the run is left
    /// <c>running</c> for the queue's retry (as 0.6.1). After the answer, the token hook gets
    /// <see cref="CancellationToken.None"/>: the call was paid for.
    /// </summary>
    private async Task<LlmToolLoopResult> CallAsync(
        AgentAttempt attempt, AgentStageExtension extension, LlmRequest request, AgentToolExecutor? executor, AgentCallOptions options, CancellationToken cancellationToken)
    {
        var (agentRun, resolved) = (attempt.AgentRun!, attempt.Llm!);
        using var firstToken = LlmFirstTokenProbe.Start(time);
        var started = time.GetTimestamp();
        var paid = default(LlmTurnTokens);
        string? stopped = null;
        async Task<bool> OnTurn(LlmTurnTokens soFar, CancellationToken token)
        {
            paid = soFar;
            stopped = await extension.CheckToolTurnAsync(attempt, soFar, token);
            return stopped is null;
        }

        LlmToolLoopResult answer;
        try
        {
            // FR-317: the host's tools, else the stage's; an executor with no tools (AgentToolExecutor.None) is a plain call.
            var tools = options.Tools ?? executor;
            answer = await LlmToolLoop.RunAsync(llm.ProviderFor(resolved.Provider), request, tools is { Definitions.Count: 0 } ? null : tools, cancellationToken, OnTurn);
        }
        catch (LlmToolLoopStoppedException ex)
        {
            var message = stopped ?? "The tool loop was stopped between turns.";
            await RecordCallAsync(attempt, started, firstToken, executor, options, ex.Tokens);
            await extension.OnBudgetExceededAsync(attempt, message, CancellationToken.None);
            var failure = new AgentStageFailure(EngineErrorCodes.BudgetExceeded, message);
            await EndFailedCallAsync(attempt, extension, new AgentRunFailureDecision(failure));
            throw Failed(failure);
        }
        catch (LlmProviderException ex)
        {
            var message = SecretRedaction.SanitizeProviderError(ex.Message, resolved.ApiKey);
            await RecordCallAsync(attempt, started, firstToken, executor, options, paid);
            var decision = await extension.OnCallFailedAsync(attempt, new AgentStageFailure(ex.ErrorCode, message, ex.Transient), ex.StatusCode, CancellationToken.None);
            await EndFailedCallAsync(attempt, extension, decision);
            throw Failed(decision.Failure);
        }
        catch (OperationCanceledException)
        {
            if (!extension.Policy.CommitRunSeparately && attempt.Request.AgentRunId is null)
            {
                await RecordCallAsync(attempt, started, firstToken, executor, options, paid);
                agentRun.Status = AgentRunStatus.Cancelled;
                agentRun.Error = "The run was cancelled.";
                await store.SaveChangesAsync(CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            var transient = ex is HttpRequestException or TimeoutException or IOException;
            var message = $"{resolved.Provider.ToWire()} call failed unexpectedly ({ex.GetType().Name}).";
            await RecordCallAsync(attempt, started, firstToken, executor, options, paid);
            var decision = await extension.OnCallFailedAsync(attempt, new AgentStageFailure(EngineErrorCodes.ProviderError, message, transient)
            {
                ExceptionType = ex.GetType().Name,
                ExceptionDetail = SecretRedaction.SanitizeProviderError(ex.Message, resolved.ApiKey),
            }, null, CancellationToken.None);
            await EndFailedCallAsync(attempt, extension, decision);
            throw Failed(decision.Failure);
        }

        var completion = answer.Completion;
        Finish(agentRun, started, firstToken, executor, options, completion.InputTokens, completion.OutputTokens);
        agentRun.Status = AgentRunStatus.Succeeded;
        agentRun.Error = null;
        IReadOnlyList<string> warnings = [.. options.Warnings.Concat(answer.Warnings).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal)];
        agentRun.Output = extension.SerializeOutput(
            attempt,
            new AgentStageRunOutput(completion.Text, completion.StopReason)
            {
                Warnings = warnings.Count == 0 ? null : warnings,
                MaxOutputTokens = options.MaxTokens,
                Continuations = answer.Continuations,
            });
        await hooks.OnTokensUsedAsync(attempt.Run, agentRun, (long)completion.InputTokens + completion.OutputTokens, CancellationToken.None);
        return answer;
    }

    /// <summary>The engine's <c>output</c> JSON: web JSON without nulls.</summary>
    public static string SerializeOutput(AgentStageRunOutput output) => JsonSerializer.Serialize(output, OutputJson);

    /// <summary>A failed call's timings, tool calls and paid turns (FR-26: counted for failed calls too).</summary>
    private async Task RecordCallAsync(AgentAttempt attempt, long started, LlmFirstTokenProbe firstToken, AgentToolExecutor? executor, AgentCallOptions options, LlmTurnTokens paid)
    {
        var agentRun = attempt.AgentRun!;
        Finish(agentRun, started, firstToken, executor, options, paid.InputTokens, paid.OutputTokens);
        if (paid.InputTokens + paid.OutputTokens > 0)
        {
            await hooks.OnTokensUsedAsync(attempt.Run, agentRun, (long)paid.InputTokens + paid.OutputTokens, CancellationToken.None);
        }
    }

    /// <summary>FR-315: a failed call either stays <c>running</c> (the queue retries) or fails the run and fires the failed hook.</summary>
    private async Task EndFailedCallAsync(AgentAttempt attempt, AgentStageExtension extension, AgentRunFailureDecision decision)
    {
        var agentRun = attempt.AgentRun!;
        var failure = decision.Failure;
        agentRun.Error = Truncate($"{failure.ErrorCode}: {failure.Message}");
        if (decision.KeepRunning && failure.Transient)
        {
            agentRun.Status = AgentRunStatus.Running;
            agentRun.CompletedAt = null;
            await CommitAsync(attempt, extension, AgentRunCommitPoint.KeptRunning, AgentRunVerdict.None, CancellationToken.None);
            return;
        }

        agentRun.Status = AgentRunStatus.Failed;
        await extension.OnRunFailedAsync(attempt, failure, CancellationToken.None);
        await CommitAsync(attempt, extension, AgentRunCommitPoint.Failed, AgentRunVerdict.None, CancellationToken.None);
    }

    /// <summary>
    /// Tokens, cost, duration, latency (FR-297) and tool calls of the attempt, added to what a resumed run already
    /// recorded (a new run starts at zero).
    /// </summary>
    private void Finish(AgentRun agentRun, long started, LlmFirstTokenProbe firstToken, AgentToolExecutor? executor, AgentCallOptions options, int inputTokens, int outputTokens)
    {
        agentRun.InputTokens += inputTokens;
        agentRun.OutputTokens += outputTokens;
        agentRun.CostEstimate = AgentRunRules.EstimateCost(agentRun.InputTokens, agentRun.OutputTokens, services.GetService(typeof(ILlmCatalog)) is ILlmCatalog catalog ? catalog.PriceOf(agentRun.Model) : null);
        agentRun.DurationMs += (long)time.GetElapsedTime(started).TotalMilliseconds;
        agentRun.LatencyMs = firstToken.LatencyMs(started) ?? agentRun.LatencyMs;
        agentRun.CompletedAt = time.GetUtcNow();
        agentRun.ToolCalls = options.ToolCalls is { } log ? log() ?? agentRun.ToolCalls : executor?.CallsJson() ?? "[]";
    }

    /// <summary>The findings of the output (schema errors, failed <c>validate:</c> guards and the host's checks), or <c>null</c> when valid.</summary>
    private async Task<string?> ValidateAsync(
        AgentAttempt attempt, AgentStageExtension extension, ArtifactDefinition? artifact, string content, CancellationToken cancellationToken)
    {
        IReadOnlyList<SchemaFinding> schemaErrors = artifact is { Format: ArtifactFormat.Json, Schema: { } schema }
            ? schemas.Validate(attempt.PackId, schema, content)
            : [];
        var reasons = new List<string>();
        var facts = FactBag.Empty.With(new StageOutputFact(artifact?.Key ?? string.Empty, artifact?.Format ?? ArtifactFormat.Markdown, content));
        var (run, stage) = (attempt.Run, attempt.Stage);
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

        // FR-314: the host's own checks come after the guards' reasons.
        reasons.AddRange(await extension.ValidateOutputAsync(attempt, content, cancellationToken));
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
