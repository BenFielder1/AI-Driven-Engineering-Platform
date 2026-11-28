using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-295: runs one attempt of any agent stage. It loads the run's pinned definition, collects the latest locked, intact
/// version of each of the stage's <c>inputs</c> in the run's subject (a missing one is absent, D-0.7.0-27), asks the
/// pipeline's <see cref="IAgentContextProvider"/>s for extra context, renders the pack prompt, calls the model through
/// <see cref="ILlmResolver"/> with the stage's <see cref="IAgentTool"/>s, records the <see cref="AgentRun"/>, validates the
/// output (artifact schema and <c>validate:</c> guards) and stores a valid output as a new artifact version (draft, or
/// locked for <c>approval: auto_lock</c>). Retrying is <see cref="AgentStageCoordinator"/>'s job (scoped).
/// FR-310–FR-316: the steps run in §4.60's order around the pipeline's <see cref="AgentStageExtension"/>.
/// </summary>
public sealed partial class AgentStageRunner(
    PipelineDefinitionResolver definitions,
    IAgentStageStore store,
    ILlmResolver llm,
    IPromptSource prompts,
    ArtifactSchemas schemas,
    IAgentStageHooks hooks,
    IEnumerable<IAgentContextProvider> contextProviders,
    IEnumerable<AgentStageExtension> extensions,
    IServiceProvider services,
    PipelineRegistryHost registryHost,
    ILogger<AgentStageRunner> logger,
    TimeProvider time)
{
    /// <summary>The longest provider error stored on a run (as the SDLC's runner).</summary>
    public const int MaxErrorLength = 2000;

    public async Task<AgentStageOutcome> RunAsync(AgentStageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // §8: one span per agent stage attempt, tagged with the run, stage, agent and attempt.
        using var activity = EngineTelemetry.StartAgentStage(request.RunId, request.StageId, null, request.Attempt);
        try
        {
            var outcome = await RunAttemptAsync(request, activity, cancellationToken);
            activity?.SetTag(EngineTelemetry.OutcomeTag, outcome.Valid ? "valid" : "invalid");
            return outcome;
        }
        catch (JobDeferredException)
        {
            // FR-359: the attempt waits on a tool_approval gate; not an error.
            activity?.SetTag(EngineTelemetry.OutcomeTag, "waiting");
            throw;
        }
        catch (JobFailedException ex)
        {
            // An error span is described by its error code, or else the exception type, never the message (NFR-SEC).
            EngineTelemetry.SetError(activity, ex.ErrorCode ?? nameof(JobFailedException));
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineTelemetry.SetError(activity, ex.GetType().Name);
            throw;
        }
    }

    private async Task<AgentStageOutcome> RunAttemptAsync(AgentStageRequest request, Activity? activity, CancellationToken cancellationToken)
    {
        var run = await store.FindRunAsync(request.RunId, cancellationToken) ?? throw Permanent($"Pipeline run {request.RunId} does not exist.");
        var extension = AgentStageExtension.For(extensions, run.PipelineId);

        // FR-311: the host observes the attempt (its log scope and span cover every later step).
        using var observer = extension.ObserveAttempt(request);
        try
        {
            var outcome = await RunObservedAsync(request, run, extension, activity, cancellationToken);
            observer?.Completed(outcome);
            return outcome;
        }
        catch (Exception ex) when (observer is not null && ex is not JobDeferredException)
        {
            observer.Failed(ex);
            throw;
        }
    }

    private async Task<AgentStageOutcome> RunObservedAsync(
        AgentStageRequest request, PipelineRun run, AgentStageExtension extension, Activity? activity, CancellationToken cancellationToken)
    {
        // FR-277: the pinned definition, registered or stored (checked against its hash) with every name it uses registered.
        var loaded = await definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
        if (loaded is not { Available: true })
        {
            throw new JobFailedException(
                $"Run {run.Id} uses pipeline definition {run.DefinitionHash}, which this build can't run.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        }

        var definition = loaded.Definition;
        var defined = definition.FindAnyStage(request.StageId) ?? throw Permanent($"Pipeline '{definition.Pipeline}' has no stage '{request.StageId}'.");

        // D-0.7.0-42: the agent the request names (default: the primary), as a view of the stage with its binding.
        var stage = defined.ForAgent(request.AgentKey) ?? throw Permanent(request.AgentKey is null
            ? $"Stage '{defined.Id}' is not an agent stage."
            : $"Stage '{defined.Id}' binds no agent '{request.AgentKey}'.");
        if (request.Variant is { } variant)
        {
            // FR-345, §4.65: a fan-out branch reads the stage's declared inputs and stores the stage's own output (checked
            // by the stage's validate guards), labelled with its variant; only the agent (prompt, model, tools) is its own.
            // FR-354: a follow-up's label (followup:<duration>) is the variant its outputs are stored with.
            if (!FollowUpRules.IsDeclared(definition, defined.Id, variant) && FanOutRules.BranchOf(defined, variant) is null)
            {
                throw Permanent($"Stage '{defined.Id}' has no fan-out branch '{variant}'.");
            }

            stage = stage with { Inputs = defined.Inputs, Output = defined.Output, Validate = defined.Validate, OnInvalid = defined.OnInvalid };
        }

        if (request.ItemIndex is not null || request.Reduce)
        {
            // FR-355: a map item, or the reduce step, reads the stage's declared inputs and stores the stage's own output
            // (an item with its index); the binding gives only the agent (prompt, tools, job type) and its own checks.
            var role = MapRules.RoleOf(defined, request.AgentKey, request.ItemIndex);
            if (role == MapAgentRole.None || (role == MapAgentRole.Item) == request.Reduce)
            {
                throw Permanent($"Stage '{defined.Id}' has no map {(request.Reduce ? "reduce" : "item")} agent '{request.AgentKey}'.");
            }

            stage = stage with { Inputs = defined.Inputs, Output = defined.Output };
        }

        if (request.TurnIndex is not null)
        {
            // FR-356: a conversation turn: the participant reads the stage's inputs (and its binding's) and stores no
            // artifact; its reply is the run's output, and the driver stores the transcript as the stage's output.
            if (request.ItemIndex is not null || request.Reduce || request.Variant is not null
                || ConversationRules.ParticipantOf(defined, request.AgentKey, request.TurnRole) is null)
            {
                throw Permanent($"Stage '{defined.Id}' has no conversation participant '{request.AgentKey}' as '{request.TurnRole}'.");
            }

            stage = stage with { Inputs = [.. defined.Inputs.Union(stage.Inputs, StringComparer.Ordinal)], Output = null };
        }

        var agent = stage.Agent!;
        activity?.SetTag(EngineTelemetry.AgentTag, agent.Key);
        var existing = request.AgentRunId is { } runId ? await store.FindAgentRunAsync(runId, cancellationToken) : null;
        if (existing is not null && (existing.ProjectId != run.ProjectId || existing.PipelineRunId != run.Id))
        {
            // FR-310: a caller's id names a run of this pipeline run, never another's. The stage isn't compared: a host may
            // record another stage on the run (AgentRunMetadata.Stage).
            throw Permanent($"Agent run {existing.Id} belongs to another project or pipeline run.");
        }

        var separate = extension.Policy.CommitRunSeparately;
        var attempt = new AgentAttempt(request, run, loaded.PackId, definition, stage, agent, existing);

        // FR-359, §4.73: a run holding a saved destructive call goes on from it, before anything else: while its gate is
        // pending this re-defers the job with no model call and nothing saved.
        if (existing is { Status: AgentRunStatus.Running, ToolLoopState: not null })
        {
            return await ContinueToolLoopAsync(attempt, extension, existing, cancellationToken);
        }

        // 1. FR-316: the host's pre-run check.
        if (await extension.CheckBeforeRunAsync(attempt, cancellationToken) is { } refusal)
        {
            await SaveIfSeparateAsync(separate);
            throw Failed(refusal);
        }

        // 2. FR-26: the budget, for a new run only (a stored run already passed it, as 0.6.1).
        if (existing is null && await hooks.CheckBudgetAsync(run, agent.Key, cancellationToken) is { } refused)
        {
            await extension.OnBudgetExceededAsync(attempt, refused, cancellationToken);
            await SaveIfSeparateAsync(separate);
            throw new JobFailedException(refused, transient: false, EngineErrorCodes.BudgetExceeded);
        }

        // 3. FR-310: create the run, or load it by the caller's id.
        var template = Template(loaded.PackId, definition, agent);
        switch (existing?.Status)
        {
            case AgentRunStatus.Succeeded:
                return await RedeliverAsync(attempt, extension, existing, cancellationToken);
            case AgentRunStatus.Failed or AgentRunStatus.Cancelled:
                throw StoredFailure(existing);
        }

        var metadata = await StartRunAsync(attempt, extension, template.Version, cancellationToken);
        var agentRun = attempt.AgentRun!;
        PreparedCall prepared;
        try
        {
            // 4. FR-311.
            await extension.OnRunStartedAsync(attempt, cancellationToken);
            if (separate && !attempt.Resumed)
            {
                await CommitAsync(attempt, extension, AgentRunCommitPoint.Started, AgentRunVerdict.None, cancellationToken);
            }

            // 5. FR-316's checks around inputs, context and render, then the key check.
            prepared = await PrepareAsync(attempt, extension, template, metadata, cancellationToken);
        }
        catch (JobFailedException ex) when (!ex.Transient)
        {
            await FailBeforeCallAsync(attempt, extension, new AgentStageFailure(ex.ErrorCode ?? EngineErrorCodes.Validation, ex.Message));
            throw;
        }

        // 6. FR-312: the call with the host's options; the run is committed first when the host commits separately.
        var options = await extension.CallOptionsAsync(attempt, cancellationToken);
        RecordInput(agentRun, prepared.System, prepared.User, attempt.Llm!.ApiKey);
        agentRun.Attempts++;
        if (separate)
        {
            await CommitAsync(attempt, extension, AgentRunCommitPoint.Calling, AgentRunVerdict.None, cancellationToken);
        }
        else if (!attempt.Resumed)
        {
            store.Add(agentRun);
        }

        // FR-312: what the host prepares just before the call (after the run, with its attempt, is saved).
        options = await extension.PrepareCallAsync(attempt, options, cancellationToken);
        // FR-358: the stage's tools within its max_tier are offered; FR-359: a destructive one stops the loop for approval,
        // and an external one is refused when called (FR-360, until its gate).
        var executor = prepared.Tools.Count > 0 ? AgentToolExecutor.ForStage(prepared.Tools, prepared.MaxTier, options.ToolSession, logger) : null;

        var llmRequest = new LlmRequest
        {
            Model = agentRun.Model,
            System = prepared.System,
            Messages = [new LlmMessage(LlmRole.User, prepared.User)],
            MaxTokens = options.MaxTokens,
            ContextTokens = options.ContextTokens,
            Agent = agent.Key,
            ApiKey = attempt.Llm.ApiKey,
        };
        var answer = await CallAsync(attempt, extension, llmRequest, executor, options, cancellationToken);

        // 7–8. Validate, commit, store.
        var artifactDefinition = definition.FindArtifact(stage.Output);
        var content = AgentStagePrompt.Content(artifactDefinition?.Format ?? ArtifactFormat.Markdown, answer.Completion.Text);
        return await FinishAsync(attempt, extension, artifactDefinition, content, prepared.User, cancellationToken);
    }

    /// <summary>The old engine paths saved nothing on a refusal; a separately committing host gets its hook's changes saved.</summary>
    private Task SaveIfSeparateAsync(bool separate) => separate ? store.SaveChangesAsync(CancellationToken.None) : Task.CompletedTask;

    private PromptTemplate Template(string packId, PipelineDefinition definition, AgentStageDefinition agent)
    {
        var promptName = agent.Prompt ?? agent.Key;
        var source = prompts.Find(packId, definition.Pipeline, promptName) ?? throw Permanent($"No prompt '{promptName}' is registered.");
        var template = PromptTemplate.Parse(agent.Key, source);
        return template.IsSuccess ? template.Value : throw Permanent($"Prompt '{promptName}' is invalid: {template.Error!.Message}");
    }

    private static JobFailedException Permanent(string message) => new(message, transient: false, EngineErrorCodes.Validation);

    private static JobFailedException Failed(AgentStageFailure failure) => new(failure.Message, failure.Transient, failure.ErrorCode);

    /// <summary>FR-310: a failed or cancelled stored run fails the job with its stored code (as 0.6.1).</summary>
    private static JobFailedException StoredFailure(AgentRun run)
    {
        if (run.Error?.StartsWith(StageValidationRules.RejectedErrorPrefix, StringComparison.Ordinal) == true)
        {
            return new JobFailedException(run.Error, transient: false, EngineErrorCodes.Validation);
        }

        var code = run.Error?.Split(':', 2)[0] is { Length: > 0 } stored && stored.All(c => char.IsAsciiLetterUpper(c) || c == '_')
            ? stored
            : EngineErrorCodes.ProviderError;
        return new JobFailedException(run.Error ?? "The run already failed.", transient: false, code);
    }

    private static string Truncate(string value) => value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;
}
