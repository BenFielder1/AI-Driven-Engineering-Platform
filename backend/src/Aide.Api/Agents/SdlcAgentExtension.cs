using Aide.Domain.Common;
using Aide.Infrastructure.Llm;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-310–FR-316, FR-318: the SDLC's agent stages on the engine runner, with 0.6.1's runner and artifact
/// agent flow behaviour (FR-320: the only SDLC agent path): the run commits on its own (started, before the call, answered with its
/// tokens) before the draft is stored; the same checks, codes and order (budget, source items, render, provider enabled,
/// key); the same run fields, events, output JSON and failure decisions; the reply check with one corrective re-run; and
/// the draft through <see cref="Features.Artifacts.AgentDraftWriter"/>. Each attempt's 0.6.1 request is its
/// <see cref="SdlcAgentStageCall"/> (scoped).
/// </summary>
public sealed partial class SdlcAgentExtension(
    ISdlcAgentRunData data,
    SdlcAgentStageHooks hooks,
    ILlmProviderCatalog catalog,
    ILlmOutputLimits outputLimits,
    SdlcRepoToolScope tools,
    TimeProvider time,
    ILogger<SdlcAgentExtension> logger) : AgentStageExtension
{
    private bool _deadLetter;
    private (long? TokenBudget, long TokensUsed) _callBudget;
    private SdlcAttemptObserver? _observer;

    public override string PipelineId => SdlcPack.PipelineId;

    /// <summary>FR-315: 0.6.1's order: the run is saved when it starts, before the call and when answered.</summary>
    public override AgentRunPolicy Policy => AgentRunPolicy.Separate;

    /// <summary>Whether the attempt is saving its draft: a conflict then is the job's transient <c>CONFLICT</c>.</summary>
    public bool SavingDraft { get; private set; }

    internal static SdlcAgentStageCall CallOf(AgentAttempt attempt) =>
        attempt.Request.HostState as SdlcAgentStageCall
            ?? throw new JobFailedException($"The {attempt.Agent.Key} stage was started without its SDLC request.", transient: false, ErrorCodes.Validation);

    /// <summary>FR-311, NFR-OBS: 0.6.1's per-run log scope and <c>agent run</c> span (<see cref="SdlcAttemptObserver"/>).</summary>
    public override IAgentAttemptObserver? ObserveAttempt(AgentStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _observer = request.HostState is SdlcAgentStageCall call ? new SdlcAttemptObserver(logger, request, call) : null;
        return _observer;
    }

    /// <summary>0.6.1: a new run of a project that is gone fails with <c>NOT_FOUND</c> (before the budget).</summary>
    public override async Task<AgentStageFailure?> CheckBeforeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        _ = CallOf(attempt);
        if (_observer is not null)
        {
            _observer.Attempt = attempt;
        }

        SavingDraft = false;
        _deadLetter = false;
        hooks.StartProject = null;
        if (attempt.Existing is not null)
        {
            return null;
        }

        // 0.6.1 read the project once at a run's start: its existence, budget, phase and version (shared with the hooks).
        hooks.StartProject = await data.ProjectAsync(attempt.Run.ProjectId, cancellationToken);
        return hooks.StartProject is null
            ? new AgentStageFailure(ErrorCodes.NotFound, "The project no longer exists.")
            : null;
    }

    /// <summary>FR-26: <c>BUDGET_EXCEEDED</c> at the start (no run) or between tool turns (with the run), as 0.6.1.</summary>
    public override Task OnBudgetExceededAsync(AgentAttempt attempt, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var call = CallOf(attempt);
        if (attempt.AgentRun is not { } run)
        {
            var (budget, used) = hooks.StartBudget;
            data.AddEvent(EventTypes.BudgetExceeded, attempt.Run.ProjectId, SdlcRunEvents.BudgetRefused(call.Request.Agent, budget, used, call.Request.Job.Id), time.GetUtcNow());
            LogBudgetExceeded(logger, used, budget);
            return Task.CompletedTask;
        }

        var tokensUsed = _callBudget.TokensUsed + run.InputTokens + run.OutputTokens;
        data.AddEvent(EventTypes.BudgetExceeded, run.ProjectId, SdlcRunEvents.BudgetStopped(run, _callBudget.TokenBudget, tokensUsed, call.Request.Job.Id), time.GetUtcNow());
        LogBudgetExceeded(logger, tokensUsed, _callBudget.TokenBudget);
        return Task.CompletedTask;
    }

    /// <summary>
    /// FR-312: 0.6.1's run fields: the phase as the stage, the release version, the step, the full input list, the
    /// feedback, and the stack stamp (<see cref="SdlcAgentRuns.StampStack"/>).
    /// </summary>
    public override async Task<AgentRunMetadata> DescribeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var request = CallOf(attempt).Request;
        var project = hooks.StartProject ?? await data.ProjectAsync(attempt.Run.ProjectId, cancellationToken)
            ?? throw new JobFailedException("The project no longer exists.", transient: false, ErrorCodes.NotFound);
        attempt.AgentRun!.ReleaseVersionId = project.CurrentReleaseVersionId;

        var stamped = new AgentRun { Agent = request.Agent };
        SdlcAgentRuns.StampStack(stamped, request);
        return new AgentRunMetadata
        {
            CatalogVersion = stamped.CatalogVersion,
            Packs = stamped.Packs,
            AgentKey = stamped.AgentKey,
            StepId = request.StepId,
            Stage = project.CurrentPhase.ToWire(),
            InputArtifacts = request.InputArtifacts,
            SetsUserFeedback = true,
            UserFeedback = request.UserFeedback,
        };
    }

    /// <summary>FR-311: <c>RUN_STARTED</c> of a new run (saved with it), as 0.6.1.</summary>
    public override Task OnRunStartedAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (_observer is not null)
        {
            _observer.Run = attempt.AgentRun;
        }

        if (attempt.Resumed)
        {
            return Task.CompletedTask;
        }

        var request = CallOf(attempt).Request;
        var run = attempt.AgentRun!;
        data.AddEvent(EventTypes.RunStarted, run.ProjectId, SdlcRunEvents.RunStarted(run, request.Job.Id, request.Stack?.CatalogLabel, request.LegacyAgentKey), run.CreatedAt);
        LogStarted(logger, run.Provider, run.Model, run.PromptVersion);
        return Task.CompletedTask;
    }

    /// <summary>FR-172, §4.26: the source items must fit the run's model (catalog; unlisted: the fixed budget).</summary>
    public override Task<AgentStageFailure?> CheckBeforeRenderAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (CallOf(attempt).Request.SourceItemsBytes is not { } sourceBytes)
        {
            return Task.FromResult<AgentStageFailure?>(null);
        }

        var run = attempt.AgentRun!;
        var budgeted = Domain.Trackers.TrackerRules.CheckRunBudget(sourceBytes, ContextWindow(run));
        return Task.FromResult(budgeted.IsFailure ? new AgentStageFailure(budgeted.Error!.Code, budgeted.Error.Message) : null);
    }

    /// <summary>FR-157, then 0.6.1's <c>PROVIDER_NOT_CONFIGURED</c> wording, before the engine's own key check.</summary>
    public override Task<AgentStageFailure?> CheckBeforeCallAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var run = attempt.AgentRun!;
        var provider = run.Provider.ToWire();
        if (!catalog.IsEnabled(run.Provider))
        {
            return Task.FromResult<AgentStageFailure?>(new AgentStageFailure(
                ErrorCodes.LlmProviderUnavailable,
                $"The {provider} AI provider is turned off on this server: a project Owner chooses another provider in the project's LLM settings."));
        }

        return Task.FromResult(attempt.Llm!.IsConfigured
            ? null
            : new AgentStageFailure(
                ErrorCodes.ProviderNotConfigured,
                $"{provider} is not configured: a project Owner binds one of their {provider} connections in the project's LLM settings."));
    }

    /// <summary>FR-162: the context window of the run's model in the catalog, or <c>null</c> when unknown.</summary>
    private int? ContextWindow(AgentRun run) => LlmOutputLimits.Find(catalog.Catalog(run.Provider), run.Model)?.ContextTokens;

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run started on {Provider} model {Model} (prompt v{PromptVersion})")]
    private static partial void LogStarted(ILogger logger, LlmProvider provider, string model, string promptVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run refused: token budget used up ({TokensUsed} of {TokenBudget})")]
    private static partial void LogBudgetExceeded(ILogger logger, long tokensUsed, long? tokenBudget);
}
