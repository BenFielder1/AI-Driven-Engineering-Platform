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

public sealed partial class AgentStageRunner
{
    /// <summary>The rendered prompt of an attempt and the executor of its tools (<c>null</c>: none).</summary>
    private sealed record PreparedCall(string System, string User, AgentToolExecutor? Executor);

    /// <summary>
    /// Step 3 (FR-310, FR-312): continues a run left <c>running</c> (its provider and model, with the key resolved again),
    /// or creates a new one (under the caller's id, with the host's metadata). With
    /// <see cref="AgentRunPolicy.CommitRunSeparately"/> the new run is added at once; otherwise just before the call.
    /// </summary>
    private async Task<AgentRunMetadata> StartRunAsync(AgentAttempt attempt, AgentStageExtension extension, string promptVersion, CancellationToken cancellationToken)
    {
        var run = attempt.Run;
        if (attempt.Existing is { } resumed)
        {
            var key = await llm.KeyAsync(run.ProjectId, resumed.Provider, cancellationToken);
            resumed.CredentialId = key.CredentialId;
            attempt.Llm = new ResolvedLlm(resumed.Provider, resumed.Model, key.ApiKey, key.CredentialId);
            attempt.AgentRun = resumed;
            return AgentRunMetadata.None;
        }

        var resolved = await llm.ResolveAsync(run.ProjectId, attempt.Agent.Key, cancellationToken);
        attempt.Llm = resolved;
        var agentRun = NewAgentRun(run, attempt.Stage, attempt.Agent, resolved, promptVersion, attempt.Request.Feedback);
        if (attempt.Request.AgentRunId is { } id)
        {
            agentRun.Id = id;
        }

        attempt.AgentRun = agentRun;
        var metadata = await extension.DescribeRunAsync(attempt, cancellationToken);
        agentRun.CatalogVersion = metadata.CatalogVersion;
        agentRun.Packs = metadata.Packs;
        agentRun.AgentKey = metadata.AgentKey;
        agentRun.StepId = metadata.StepId;
        agentRun.Stage = metadata.Stage ?? agentRun.Stage;
        if (metadata.SetsUserFeedback)
        {
            agentRun.UserFeedback = metadata.UserFeedback;
        }
        if (metadata.InputArtifacts is { } inputArtifacts)
        {
            agentRun.InputArtifacts = inputArtifacts;
        }

        if (extension.Policy.CommitRunSeparately)
        {
            store.Add(agentRun);
        }

        return metadata;
    }

    /// <summary>
    /// Step 5 (0.6.1's order): the pre-render check, the intact locked inputs, the context, the prompt (with the request's
    /// extra values), the pre-call check, the key check, and the tools.
    /// </summary>
    private async Task<PreparedCall> PrepareAsync(AgentAttempt attempt, AgentStageExtension extension, PromptTemplate template, AgentRunMetadata metadata, CancellationToken cancellationToken)
    {
        var (run, stage, agent, request) = (attempt.Run, attempt.Stage, attempt.Agent, attempt.Request);
        if (await extension.CheckBeforeRenderAsync(attempt, cancellationToken) is { } notRendered)
        {
            throw Failed(notRendered);
        }

        // A locked input whose content no longer matches its hash (FR-20) is absent, never fed to the model. An input whose
        // placeholder the host supplies isn't read (its value wins anyway), nor, when the host lists the run's inputs itself
        // or the run is resumed, one the template doesn't use.
        var supplied = new HashSet<string>(request.PromptValues?.Keys ?? [], StringComparer.Ordinal);
        var listsInputs = metadata.InputArtifacts is null && !attempt.Resumed;
        var used = template.Placeholders.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var keys = stage.Inputs.Where(k => !supplied.Contains(AgentStagePrompt.PlaceholderOf(k)) && (listsInputs || used.Contains(AgentStagePrompt.PlaceholderOf(k)))).ToList();
        var inputs = (await store.LatestLockedAsync(run, keys, cancellationToken))
            .Where(a => ArtifactRules.IntegrityOf(a.Status, a.Content, a.ContentHash) == ArtifactIntegrity.Ok)
            .ToList();
        var context = new List<AgentContextSection>();
        var contextRequest = new AgentContextRequest(run.Id, run.ProjectId, attempt.Definition.Pipeline, stage.Id, agent.Key) { SuppliedPlaceholders = supplied };
        foreach (var provider in contextProviders.Where(p => p.PipelineId == attempt.Definition.Pipeline))
        {
            context.AddRange(await provider.GetContextAsync(contextRequest, cancellationToken));
        }

        var values = AgentStagePrompt.Values(inputs, context, request.Feedback);
        foreach (var (name, value) in request.PromptValues ?? new Dictionary<string, string?>())
        {
            values[name] = value;
        }

        var rendered = template.Render(values);
        if (!rendered.IsSuccess)
        {
            throw new JobFailedException(rendered.Error!.Message, transient: false, rendered.Error.Code);
        }

        if (metadata.InputArtifacts is null && !attempt.Resumed)
        {
            attempt.AgentRun!.InputArtifacts = [.. inputs.Select(a => new InputArtifactRef(a.Id, a.Type, a.Version))];
        }

        if (await extension.CheckBeforeCallAsync(attempt, cancellationToken) is { } notCalled)
        {
            throw Failed(notCalled);
        }

        if (!attempt.Llm!.IsConfigured)
        {
            throw new JobFailedException($"No API key is configured for {attempt.Llm.Provider.ToWire()}.", transient: false, EngineErrorCodes.ProviderNotConfigured);
        }

        var user = AgentStagePrompt.UserMessage(template, rendered.Value, request.AppendFeedback ? request.Feedback : null);
        var tools = agent.Tools.Select(name => services.GetPipelineTool(name) ?? throw Permanent($"Tool '{name}' is not bound.")).ToList();
        return new PreparedCall(rendered.Value.System, user, tools.Count > 0 ? new AgentToolExecutor(tools) : null);
    }

    /// <summary>
    /// A failure before the call (steps 4–5) fails a stored run: the failed hook fires and the run is saved. Under the
    /// default policy a new run isn't stored yet, so nothing is (as before).
    /// </summary>
    private async Task FailBeforeCallAsync(AgentAttempt attempt, AgentStageExtension extension, AgentStageFailure failure)
    {
        var agentRun = attempt.AgentRun!;
        if (!extension.Policy.CommitRunSeparately && !attempt.Resumed)
        {
            agentRun.Status = AgentRunStatus.Failed;
            return;
        }

        agentRun.Status = AgentRunStatus.Failed;
        agentRun.Error = Truncate($"{failure.ErrorCode}: {failure.Message}");
        agentRun.CompletedAt = time.GetUtcNow();
        await extension.OnRunFailedAsync(attempt, failure, CancellationToken.None);
        await CommitAsync(attempt, extension, AgentRunCommitPoint.Failed, AgentRunVerdict.None, CancellationToken.None);
    }

    /// <summary>FR-315: one save of the run, through the extension's commit hook.</summary>
    private Task CommitAsync(AgentAttempt attempt, AgentStageExtension extension, AgentRunCommitPoint point, AgentRunVerdict verdict, CancellationToken cancellationToken) =>
        extension.CommitRunAsync(attempt, new AgentRunCommit(point, verdict, store.SaveChangesAsync), cancellationToken);

    private AgentRun NewAgentRun(
        PipelineRun run,
        StageDefinition stage,
        AgentStageDefinition agent,
        ResolvedLlm resolved,
        string promptVersion,
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
            UserFeedback = feedback,
            Status = AgentRunStatus.Running,
            Attempts = 0,
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
}
