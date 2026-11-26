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
    /// <summary>The rendered prompt of an attempt and the stage's tools (the executor is built with the host's options).</summary>
    private sealed record PreparedCall(string System, string User, IReadOnlyList<IAgentTool> Tools);

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

        // FR-355: a map item's run records its index.
        agentRun.ItemIndex = attempt.Request.ItemIndex;

        // FR-356: a conversation turn's run records its 1-based turn and the speaker's role.
        agentRun.TurnIndex = attempt.Request.TurnIndex;
        agentRun.TurnRole = attempt.Request.TurnRole;

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
        var contextRequest = new AgentContextRequest(run.Id, run.ProjectId, attempt.Definition.Pipeline, stage.Id, agent.Key)
        {
            SuppliedPlaceholders = supplied,
            Branch = request.Variant,
        };
        foreach (var provider in contextProviders.Where(p => p.PipelineId == attempt.Definition.Pipeline))
        {
            context.AddRange(await provider.GetContextAsync(contextRequest, cancellationToken));
        }

        var values = AgentStagePrompt.Values(inputs, context, request.Feedback);

        // FR-346: an aggregate stage gets every branch output of its source as {{<KEY>_ALL}}, each item labelled.
        foreach (var (placeholder, items) in await AggregateInputs.LoadAsync(store, run, attempt.Definition, stage.Id, cancellationToken))
        {
            values[placeholder] = FanOutRules.RenderList(items);
        }

        // FR-357: each from_runs input the template uses is a list over the project's earlier runs, {{<ARTIFACT>_RUNS}}.
        var runsUsed = stage.FromRuns.Select(RunsInputRules.Placeholder).Where(p => used.Contains(p) && !supplied.Contains(p)).ToHashSet(StringComparer.Ordinal);
        if (runsUsed.Count > 0)
        {
            foreach (var list in await RunsInputs.LoadAsync(store, run, stage, time.GetUtcNow(), cancellationToken, runsUsed))
            {
                values[list.Placeholder] = FanOutRules.RenderList(list.Items);
            }
        }

        // FR-349, §4.67: a branch's prompt context holds its own label as {{VARIANT}} (v1…vn for a variants stage).
        if (request.Variant is { } variant)
        {
            values[VariantRules.LabelPlaceholder] = variant;
        }

        // FR-354: a follow-up reads the run's outcome scores as {{OUTCOME_SCORES}} (one "metric: score" line each).
        if (FollowUpRules.IsLabel(request.Variant))
        {
            values[FollowUpRules.OutcomeScoresPlaceholder] = FollowUpRules.RenderScores(await store.RunOutcomeScoresAsync(run, cancellationToken));
        }

        // FR-351, §4.75: a stage with feedback.inject gets its agent's feedback entries as {{AGENT_FEEDBACK}}, and the agent
        // run records the entries it used. A stage without it is unchanged (FeedbackIds stays null).
        if (stage.Feedback is { Inject: true } feedback && string.Equals(agent.Key, stage.Agent?.Key, StringComparison.Ordinal))
        {
            var entries = await store.FeedbackAsync(run, attempt.Definition.Pipeline, agent.Key, FeedbackInjectionRules.MaxLimit, cancellationToken);
            var selection = FeedbackInjectionRules.Select(entries, feedback.Limit);
            values[FeedbackInjectionRules.Placeholder] = selection.Rendered;
            // A resumed run keeps the ids recorded for its first prompt.
            attempt.AgentRun!.FeedbackIds ??= selection.Ids;
        }

        foreach (var (name, value) in request.PromptValues ?? new Dictionary<string, string?>())
        {
            values[name] = value;
        }

        var rendered = template.Render(values);
        if (!rendered.IsSuccess)
        {
            throw new JobFailedException(rendered.Error!.Message, transient: false, rendered.Error.Code);
        }

        // FR-319: the host's transform of the rendered prompt (FR-121's specialist composition); default unchanged.
        var prompt = await extension.TransformPromptAsync(attempt, rendered.Value, cancellationToken);

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

        var user = AgentStagePrompt.UserMessage(template, prompt, request.AppendFeedback ? request.Feedback : null);
        await CheckIsolationAsync(attempt, string.Join('\n', prompt.System, user), cancellationToken);
        var tools = agent.Tools.Select(name => services.GetPipelineTool(name) ?? throw Permanent($"Tool '{name}' is not bound.")).ToList();
        return new PreparedCall(prompt.System, user, tools);
    }

    /// <summary>
    /// §4.65: a fan-out branch's rendered input must hold no sibling branch's output (any stored version, so a retry is
    /// checked against outputs its siblings stored meanwhile); else the branch fails with <c>FANOUT_ISOLATION_BROKEN</c>.
    /// </summary>
    private async Task CheckIsolationAsync(AgentAttempt attempt, string rendered, CancellationToken cancellationToken)
    {
        if (attempt.Request.ItemIndex is { } index && attempt.Stage.Output is { } itemKey)
        {
            // FR-355, §4.65: map items are isolated as fan-out branches are: no other item's output in the input.
            var own = MapRules.ItemLabel(index);
            var since = attempt.Request.ItemsSince ?? DateTimeOffset.MinValue;
            var others = (await store.RunItemsAsync(attempt.Run, itemKey, since, cancellationToken))
                .Select(a => new AggregateItem(MapRules.ItemLabel(a.ItemIndex!.Value), a.Content));
            if (FanOutRules.IsolationBreach(rendered, own, others) is { } leakedItem)
            {
                throw new JobFailedException(
                    $"Map item {index} of {attempt.Stage.Id} was given {leakedItem}'s output.", transient: false, EngineErrorCodes.FanoutIsolationBroken);
            }

            return;
        }

        if (attempt.Request.Variant is not { } label || attempt.Stage.Output is not { } key || FollowUpRules.IsLabel(label))
        {
            return;
        }

        var siblings = (await store.RunVariantsAsync(attempt.Run, key, cancellationToken))
            .Select(a => new AggregateItem(a.Variant!, a.Content));
        if (FanOutRules.IsolationBreach(rendered, label, siblings) is { } leaked)
        {
            throw new JobFailedException(
                $"Fan-out branch '{label}' of {attempt.Stage.Id} was given branch '{leaked}''s output.", transient: false, EngineErrorCodes.FanoutIsolationBroken);
        }
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
