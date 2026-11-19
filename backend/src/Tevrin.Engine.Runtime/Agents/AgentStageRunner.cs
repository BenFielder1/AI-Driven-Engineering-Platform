using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
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
/// </summary>
public sealed partial class AgentStageRunner(
    PipelineRegistry registry,
    IAgentStageStore store,
    ILlmResolver llm,
    IPromptSource prompts,
    ArtifactSchemas schemas,
    IAgentStageHooks hooks,
    IEnumerable<IAgentContextProvider> contextProviders,
    IServiceProvider services,
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
        catch (JobFailedException ex)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.ErrorCode ?? nameof(JobFailedException));
            throw;
        }
    }

    private async Task<AgentStageOutcome> RunAttemptAsync(AgentStageRequest request, System.Diagnostics.Activity? activity, CancellationToken cancellationToken)
    {
        var run = await store.FindRunAsync(request.RunId, cancellationToken) ?? throw Permanent($"Pipeline run {request.RunId} does not exist.");
        var loaded = registry.GetByHash(run.DefinitionHash) ?? throw Permanent($"The definition {run.DefinitionHash} of run {run.Id} is not registered.");
        var definition = loaded.Definition!;
        var stage = definition.FindStage(request.StageId) ?? throw Permanent($"Pipeline '{definition.Pipeline}' has no stage '{request.StageId}'.");
        var agent = stage.Agent ?? throw Permanent($"Stage '{stage.Id}' is not an agent stage.");
        activity?.SetTag(EngineTelemetry.AgentTag, agent.Key);

        // A locked input whose content no longer matches its hash (FR-20) is absent, never fed to the model.
        var inputs = (await store.LatestLockedAsync(run, stage.Inputs, cancellationToken))
            .Where(a => ArtifactRules.IntegrityOf(a.Status, a.Content, a.ContentHash) == ArtifactIntegrity.Ok)
            .ToList();
        var context = new List<AgentContextSection>();
        var contextRequest = new AgentContextRequest(run.Id, run.ProjectId, definition.Pipeline, stage.Id, agent.Key);
        foreach (var provider in contextProviders.Where(p => p.PipelineId == definition.Pipeline))
        {
            context.AddRange(await provider.GetContextAsync(contextRequest, cancellationToken));
        }

        var promptName = agent.Prompt ?? agent.Key;
        var source = prompts.Find(loaded.PackId, definition.Pipeline, promptName) ?? throw Permanent($"No prompt '{promptName}' is registered.");
        var template = PromptTemplate.Parse(agent.Key, source);
        if (!template.IsSuccess)
        {
            throw Permanent($"Prompt '{promptName}' is invalid: {template.Error!.Message}");
        }

        var rendered = template.Value.Render(AgentStagePrompt.Values(inputs, context, request.Feedback));
        if (!rendered.IsSuccess)
        {
            throw new JobFailedException(rendered.Error!.Message, transient: false, rendered.Error.Code);
        }

        if (await hooks.CheckBudgetAsync(run, agent.Key, cancellationToken) is { } refused)
        {
            throw new JobFailedException(refused, transient: false, EngineErrorCodes.BudgetExceeded);
        }

        var user = AgentStagePrompt.UserMessage(template.Value, rendered.Value, request.Feedback);
        var resolved = await llm.ResolveAsync(run.ProjectId, agent.Key, cancellationToken);
        if (!resolved.IsConfigured)
        {
            throw new JobFailedException($"No API key is configured for {resolved.Provider.ToWire()}.", transient: false, EngineErrorCodes.ProviderNotConfigured);
        }

        var tools = agent.Tools.Select(name => services.GetPipelineTool(name) ?? throw Permanent($"Tool '{name}' is not bound.")).ToList();
        var executor = tools.Count > 0 ? new AgentToolExecutor(tools) : null;
        var agentRun = NewAgentRun(run, stage, agent, resolved, template.Value.Version, inputs, request.Feedback);
        RecordInput(agentRun, rendered.Value.System, user, resolved.ApiKey);
        store.Add(agentRun);

        var llmRequest = new LlmRequest
        {
            Model = agentRun.Model,
            System = rendered.Value.System,
            Messages = [new LlmMessage(LlmRole.User, user)],
            Agent = agent.Key,
            ApiKey = resolved.ApiKey,
        };
        var answer = await CallAsync(run, agentRun, llmRequest, executor, resolved, cancellationToken);

        var artifactDefinition = definition.FindArtifact(stage.Output);
        var content = AgentStagePrompt.Content(artifactDefinition?.Format ?? ArtifactFormat.Markdown, answer.Completion.Text);
        var findings = await ValidateAsync(loaded.PackId, run, stage, artifactDefinition, content, cancellationToken);
        Artifact? artifact = null;
        if (findings is null)
        {
            artifact = artifactDefinition is null ? null : await StoreAsync(run, artifactDefinition, content, agentRun, cancellationToken);
        }
        else
        {
            // D-0.7.0-33: the call worked, so the run is succeeded, but the marker counts it as a failure in FR-298.
            agentRun.Error = StageValidationRules.RejectedError(findings);
        }

        await store.SaveChangesAsync(cancellationToken);
        return new AgentStageOutcome(findings is null, agentRun, artifact, findings, user, run, stage);
    }

    private AgentRun NewAgentRun(
        PipelineRun run,
        StageDefinition stage,
        AgentStageDefinition agent,
        ResolvedLlm resolved,
        string promptVersion,
        IReadOnlyList<Artifact> inputs,
        string? feedback) => new()
        {
            ProjectId = run.ProjectId,
            ReleaseVersionId = run.SubjectId,
            PipelineRunId = run.Id,
            Agent = agent.Key,
            Stage = stage.Id,
            Provider = resolved.Provider,
            Model = agent.Model ?? resolved.Model,
            CredentialId = resolved.CredentialId,
            PromptVersion = promptVersion,
            InputArtifacts = [.. inputs.Select(a => new InputArtifactRef(a.Id, a.Type, a.Version))],
            UserFeedback = feedback,
            Status = AgentRunStatus.Running,
            Attempts = 1,
            CreatedAt = time.GetUtcNow(),
        };

    /// <summary>FR-297: the prompt as sent, redacted and capped; input the redaction can't process in time is left out.</summary>
    private static void RecordInput(AgentRun agentRun, string system, string user, string? apiKey)
    {
        try
        {
            var recorded = AgentRunInput.Record([new("system", system), new("user", user)], apiKey);
            agentRun.Input = recorded.Text;
            agentRun.InputTruncated = recorded.Truncated;
        }
        catch (RegexMatchTimeoutException)
        {
            agentRun.Input = null;
            agentRun.InputTruncated = false;
        }
    }

    private static JobFailedException Permanent(string message) => new(message, transient: false, EngineErrorCodes.Validation);
}
