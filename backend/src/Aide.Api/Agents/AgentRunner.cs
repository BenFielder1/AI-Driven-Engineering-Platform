using System.Diagnostics;
using System.Text.Json;
using Aide.Api.Jobs;
using Aide.Api.Logging;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Agents;

/// <summary>
/// One agent invocation requested by a job handler (FR-23). <see cref="Inputs"/> are the prompt placeholder values
/// (<c>PROJECT_REQUEST</c>, …); <see cref="InputArtifacts"/> are the artifact versions they came from.
/// <see cref="UserFeedback"/> is stored on the run and fills <c>{{USER_FEEDBACK?}}</c> unless an input overrides it.
/// <see cref="RunKey"/> distinguishes several runs inside one job (e.g. a corrective re-run, FR-28).
/// </summary>
public sealed record AgentRunRequest
{
    public required Guid ProjectId { get; init; }

    public required AgentKind Agent { get; init; }

    public required JobExecution Job { get; init; }

    public IReadOnlyDictionary<string, string?> Inputs { get; init; } = new Dictionary<string, string?>();

    public IReadOnlyList<InputArtifactRef> InputArtifacts { get; init; } = [];

    public Guid? StepId { get; init; }

    public string? UserFeedback { get; init; }

    public string? RunKey { get; init; }

    /// <summary>Output token cap; default <c>Llm:MaxOutputTokens</c>.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// A pure check of the provider's reply (FR-27–FR-29), applied before the run's completion is saved. A failure fails
    /// the run with the error's code (tokens are still counted, the reply is kept on the run) and the runner throws a
    /// non-transient <see cref="JobFailedException"/>. It must not write anything: what the run produces (e.g. the
    /// artifact draft) is created by the caller after the run's completion and tokens are committed, so a conflict while
    /// creating it can never roll back the token count or make a retried job call the provider again (FR-26). Not called
    /// when a redelivered job finds the run already succeeded (the reply passed it then).
    /// </summary>
    public Func<string, Result>? Check { get; init; }

    /// <summary>
    /// FR-71: the repository the run's read-only repo tools see (<see cref="RepoScope.None"/>: no tools). Used by the
    /// Repository Analyst, refinement, spec and plan (base commit) and the code generator and review (working tree).
    /// </summary>
    public RepoScope Repo { get; init; }

    /// <summary>Warnings known before the run (e.g. <c>summary_wait_timeout</c>), recorded with the run's own (FR-70, FR-71).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A succeeded run: its id, the provider's text, what it cost and when it completed.</summary>
public sealed record AgentRunOutcome(Guid RunId, string Text, string StopReason, LlmProvider Provider, string Model, string PromptVersion, int InputTokens, int OutputTokens, DateTimeOffset CompletedAt)
{
    /// <summary>The warnings recorded on the run (FR-70, FR-71).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Runs agents for background jobs (FR-22–FR-26). Never called from endpoints (CLAUDE.md rule 11). One run row per job
/// (and <see cref="AgentRunRequest.RunKey"/>): the run id is derived from the job id, so a retried or redelivered job
/// continues the same row, and a run that already succeeded is returned without calling the provider again.
/// <list type="number">
/// <item>New run: refuse with <c>BUDGET_EXCEEDED</c> when the token budget is used up (no run row; a
/// <c>BUDGET_EXCEEDED</c> event is written for Owner notifications). Otherwise resolve provider and model, insert the
/// run (<c>running</c>, prompt version, inputs) and <c>RUN_STARTED</c>.</item>
/// <item>Render the prompt: a missing input fails the run with <c>MISSING_INPUT</c>; a provider without a key fails it
/// with <c>PROVIDER_NOT_CONFIGURED</c>. Neither calls the provider.</item>
/// <item>Call the provider (<c>attempts</c> + 1). Success records tokens, cost, duration and output, adds the tokens to
/// the project, and writes <c>RUN_COMPLETED</c>, in one transaction that holds nothing else (results such as drafts are
/// created by the caller afterwards). A transient failure keeps the run <c>running</c>
/// and throws a transient <see cref="JobFailedException"/> so the worker retries (2/8/32 s); on the job's last attempt
/// the run is <c>failed</c>, <c>RUN_FAILED</c> is written and the job dead-letters. A permanent failure fails at once.
/// Any other exception from the call is a <c>PROVIDER_ERROR</c> (transient only for I/O, HTTP and timeout errors).
/// Provider error text is redacted (<see cref="SecretRedaction"/>) before it is stored, logged or published.</item>
/// </list>
/// <item>FR-71: with <see cref="AgentRunRequest.Repo"/>, the call is a tool loop (<see cref="LlmToolLoop"/>) over a
/// credential-free snapshot (<see cref="AgentRepoContext"/>); every tool call is stored in <c>tool_calls</c>. Warnings
/// (<c>tools_unsupported</c>, <c>snapshot_unavailable</c>, the request's own) are stored with the output and in
/// <c>RUN_COMPLETED</c>.</item>
/// <c>RUN_STARTED</c> records the job id, so <see cref="AgentRunJobEndedHook"/> can fail the runs a dead-lettered job
/// left <c>running</c> (e.g. after its lease expired on the last attempt).
/// Failures surface as <see cref="JobFailedException"/> carrying the error code. Every log entry carries <c>RunId</c>.
/// </summary>
public sealed partial class AgentRunner(
    AideDbContext db,
    ILlmResolver llm,
    IPromptLibrary prompts,
    ILlmProviderCatalog catalog,
    AgentRepoContext repoContext,
    IOptions<LlmOptions> options,
    TimeProvider time,
    ILogger<AgentRunner> logger)
{
    /// <summary>The longest error text stored on a run.</summary>
    public const int MaxErrorLength = 2000;

    public const string FeedbackInput = "USER_FEEDBACK";

    public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = AgentRunRules.RunId(request.Job.Id, request.RunKey);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RunId"] = runId,
            ["Agent"] = request.Agent.ToWire(),
            [ProjectLogScope.ProjectIdKey] = request.ProjectId,
        });
        using var activity = AideTelemetry.Source.StartActivity("agent run", ActivityKind.Internal);
        activity?.SetTag(AideTelemetry.RunIdTag, runId.ToString());
        activity?.SetTag(AideTelemetry.AgentTag, request.Agent.ToWire());
        activity?.SetTag(AideTelemetry.ProjectIdTag, request.ProjectId.ToString());
        try
        {
            var outcome = await RunCoreAsync(request, runId, cancellationToken);
            activity?.SetTag(AideTelemetry.RunStatusTag, AgentRunStatus.Succeeded.ToWire());
            activity?.SetStatus(ActivityStatusCode.Ok);
            return outcome;
        }
        catch (JobFailedException ex)
        {
            // NFR-OBS: a failed (or retried) run is an error span, described by its error code — never the message.
            activity?.SetTag(AideTelemetry.RunStatusTag, TrackedStatus(runId));
            AideTelemetry.SetError(activity, ex.ErrorCode ?? nameof(JobFailedException));
            throw;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag(AideTelemetry.RunStatusTag, TrackedStatus(runId));
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetTag(AideTelemetry.RunStatusTag, TrackedStatus(runId));
            AideTelemetry.SetError(activity, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>The run's status as this runner last left it, or <c>not_started</c> when it has no row.</summary>
    private string TrackedStatus(Guid runId) =>
        db.AgentRuns.Local.FirstOrDefault(r => r.Id == runId)?.Status.ToWire() ?? "not_started";

    private async Task<AgentRunOutcome> RunCoreAsync(AgentRunRequest request, Guid runId, CancellationToken cancellationToken)
    {
        var template = prompts.Template(request.Agent);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is { Status: AgentRunStatus.Succeeded })
        {
            LogAlreadySucceeded(logger);
            return Outcome(run);
        }

        if (run is { Status: AgentRunStatus.Failed or AgentRunStatus.Cancelled })
        {
            var code = run.Error?.Split(':', 2)[0] is { Length: > 0 } stored && stored.All(c => char.IsAsciiLetterUpper(c) || c == '_')
                ? stored
                : ErrorCodes.ProviderError;
            throw new JobFailedException(run.Error ?? "The run already failed.", transient: false, code);
        }

        run ??= await StartAsync(runId, request, template, cancellationToken);

        var inputs = new Dictionary<string, string?>(request.Inputs, StringComparer.Ordinal);
        if (!inputs.ContainsKey(FeedbackInput))
        {
            inputs[FeedbackInput] = request.UserFeedback;
        }

        var rendered = template.Render(inputs);
        if (rendered.IsFailure)
        {
            await FailAsync(run, rendered.Error!.Code, rendered.Error.Message, deadLetter: false);
            throw new JobFailedException(rendered.Error.Message, transient: false, rendered.Error.Code);
        }

        // D-V2-5: the project's bound connection pays for every run, whoever triggered it; the run records which one.
        var key = await llm.KeyAsync(run.ProjectId, run.Provider, cancellationToken);
        var apiKey = key.ApiKey;
        run.CredentialId = key.CredentialId;
        var resolved = new ResolvedLlm(run.Provider, run.Model, apiKey, key.CredentialId);
        if (!resolved.IsConfigured)
        {
            var message = $"{run.Provider.ToWire()} is not configured: a project Owner binds one of their {run.Provider.ToWire()} connections in the project's LLM settings.";
            await FailAsync(run, ErrorCodes.ProviderNotConfigured, message, deadLetter: false);
            throw new JobFailedException(message, transient: false, ErrorCodes.ProviderNotConfigured);
        }

        run.Attempts++;
        await db.SaveChangesAsync(cancellationToken);
        LogCalling(logger, run.Provider, run.Model, run.Attempts);

        // FR-71: the repo tools over a snapshot (none: a plain call). A missing snapshot is a warning, never a failure.
        using var context = request.Repo == RepoScope.None
            ? RepoToolContext.Empty
            : await repoContext.OpenAsync(run.ProjectId, request.Repo, cancellationToken);
        var started = time.GetTimestamp();
        LlmCompletion completion;
        IReadOnlyList<string> loopWarnings;

        // FR-26, FR-71: a tool loop's turns are paid for one by one. Their tokens are kept so a failure later in the loop
        // still counts them, and the budget is checked between turns (as of the run's start plus this run's turns).
        var budget = await db.Projects.AsNoTracking()
            .Where(p => p.Id == run.ProjectId)
            .Select(p => new { p.TokenBudget, p.TokensUsed })
            .FirstAsync(cancellationToken);
        var paid = default(LlmTurnTokens);
        Task<bool> OnToolTurn(LlmTurnTokens soFar, CancellationToken _)
        {
            paid = soFar;
            var runTokens = (long)run.InputTokens + run.OutputTokens + soFar.InputTokens + soFar.OutputTokens;
            return Task.FromResult(TokenBudget.CheckCanContinue(budget.TokenBudget, budget.TokensUsed, runTokens).IsSuccess);
        }

        try
        {
            var loop = await LlmToolLoop.RunAsync(
                llm.ProviderFor(run.Provider),
                new LlmRequest
                {
                    Model = run.Model,
                    System = rendered.Value.System,
                    Messages = [new LlmMessage(LlmRole.User, rendered.Value.User)],
                    MaxTokens = request.MaxTokens ?? options.Value.MaxOutputTokens,
                    Agent = request.Agent,
                    ApiKey = apiKey,
                },
                context.Tools,
                cancellationToken,
                context.Tools is null ? null : OnToolTurn);
            completion = loop.Completion;
            loopWarnings = loop.Warnings;
        }
        catch (LlmToolLoopStoppedException ex)
        {
            // FR-26: the budget ran out between turns: the turns made are counted, the run stops with BUDGET_EXCEEDED.
            RecordToolCalls(run, context);
            run.DurationMs += Elapsed(started);
            await RecordPaidTurnsAsync(run, ex.Tokens);
            var used = budget.TokensUsed + run.InputTokens + run.OutputTokens;
            var message = TokenBudget.CheckCanStart(budget.TokenBudget, used).Error?.Message ?? "The project's token budget is used up.";
            db.AddEvent(
                EventTypes.BudgetExceeded,
                run.ProjectId,
                new { agent = run.Agent.ToWire(), tokenBudget = budget.TokenBudget, tokensUsed = used, jobId = request.Job.Id, runId = run.Id },
                SystemActor.Name,
                time.GetUtcNow());
            LogBudgetExceeded(logger, used, budget.TokenBudget);
            await FailAsync(run, ErrorCodes.BudgetExceeded, message, deadLetter: false);
            throw new JobFailedException(message, transient: false, ErrorCodes.BudgetExceeded);
        }
        catch (LlmProviderException ex)
        {
            RecordToolCalls(run, context);
            run.DurationMs += Elapsed(started);
            await RecordPaidTurnsAsync(run, paid);
            var message = SecretRedaction.SanitizeProviderError(ex.Message, apiKey);
            LogAttemptFailed(logger, run.Attempts, ex.Transient, ex.StatusCode, message);
            if (LlmConnectionRules.RunFailureInvalidatesKey(ex.StatusCode) && key.CredentialId is { } credentialId)
            {
                // FR-78: the bound key was rejected: the connection is marked invalid and the run fails for good.
                var rejected = $"The bound {run.Provider.ToWire()} connection's key was rejected (HTTP {ex.StatusCode}); its owner must replace it.";
                await InvalidateCredentialAsync(run, credentialId);
                await FailAsync(run, ErrorCodes.CredentialInvalid, rejected, deadLetter: false);
                throw new JobFailedException(rejected, transient: false, ErrorCodes.CredentialInvalid);
            }

            await OnProviderFailureAsync(run, request.Job.Attempt, ex.Transient, ex.ErrorCode, message);
            throw new JobFailedException(message, ex.Transient, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else from the provider layer (e.g. a FormatException from an HTTP header) must still finish the run,
            // or it would stay running. Its text may quote the request (and the key), so only the exception type is kept.
            RecordToolCalls(run, context);
            run.DurationMs += Elapsed(started);
            await RecordPaidTurnsAsync(run, paid);
            var transient = ex is HttpRequestException or TimeoutException or IOException;
            var message = $"{run.Provider.ToWire()} call failed unexpectedly ({ex.GetType().Name}).";
            LogUnexpectedFailure(logger, run.Attempts, transient, ex.GetType().Name, SecretRedaction.SanitizeProviderError(ex.Message, apiKey));
            await OnProviderFailureAsync(run, request.Job.Attempt, transient, ErrorCodes.ProviderError, message);
            throw new JobFailedException(message, transient, ErrorCodes.ProviderError);
        }

        RecordToolCalls(run, context);
        IReadOnlyList<string> warnings =
        [
            .. request.Warnings
                .Concat(context.Warning is { } warning ? [warning] : [])
                .Concat(loopWarnings)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct(StringComparer.Ordinal),
        ];
        await CompleteAsync(run, completion, Elapsed(started), request.Check, warnings);
        return Outcome(run);
    }

    /// <summary>
    /// FR-26, FR-71: adds the tokens (and cost) of tool-loop turns that completed before the attempt failed or stopped to the
    /// run and to the project's <c>tokens_used</c>, in one transaction, so retries add up and the budget sees them.
    /// </summary>
    private async Task RecordPaidTurnsAsync(AgentRun run, LlmTurnTokens paid)
    {
        if (paid.InputTokens == 0 && paid.OutputTokens == 0)
        {
            return;
        }

        run.InputTokens += paid.InputTokens;
        run.OutputTokens += paid.OutputTokens;
        run.CostEstimate = AgentRunRules.EstimateCost(run.InputTokens, run.OutputTokens, catalog.PriceOf(run.Model));
        long tokens = paid.InputTokens + paid.OutputTokens;
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await db.Projects
            .Where(p => p.Id == run.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokensUsed, p => p.TokensUsed + tokens), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>FR-71: the tool calls the run made (refused ones included) as its <c>tool_calls</c> log.</summary>
    private static void RecordToolCalls(AgentRun run, RepoToolContext context)
    {
        if (context.Tools is { } tools)
        {
            run.ToolCalls = tools.ToJson();
        }
    }

    /// <summary>
    /// FR-78: marks the run's bound connection <c>invalid</c> and records <c>CONNECTION_INVALID</c> (no secret). The run's
    /// <c>RUN_FAILED</c> notifies the project's Owners; the dedicated notification to the connection's owner is FR-92.
    /// Saved with the run's failure.
    /// </summary>
    private async Task InvalidateCredentialAsync(AgentRun run, Guid credentialId)
    {
        var credential = await db.LlmCredentials.FirstOrDefaultAsync(c => c.Id == credentialId, CancellationToken.None);
        if (credential is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        credential.Status = LlmCredentialStatus.Invalid;
        credential.LastValidatedAt = now;
        db.AddEvent(
            EventTypes.ConnectionInvalid,
            run.ProjectId,
            new { credentialId, userId = credential.UserId, provider = credential.Provider.ToWire(), runId = run.Id },
            SystemActor.Name,
            now);
        LogCredentialInvalid(logger, credentialId, credential.Provider);
    }

    /// <summary>FR-25: a transient failure with attempts left keeps the run <c>running</c> (the job retries); otherwise
    /// the run fails, dead-lettered when the retries are used up.</summary>
    private async Task OnProviderFailureAsync(AgentRun run, int jobAttempt, bool transient, string errorCode, string message)
    {
        var outcome = AgentRunRules.OnProviderFailure(transient, jobAttempt);
        if (outcome.RunStatus == AgentRunStatus.Running)
        {
            run.Error = Truncate($"{errorCode}: {message}");
            await db.SaveChangesAsync(CancellationToken.None);
        }
        else
        {
            await FailAsync(run, errorCode, message, outcome.DeadLetter);
        }
    }

    private async Task<AgentRun> StartAsync(Guid runId, AgentRunRequest request, PromptTemplate template, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken)
            ?? throw new JobFailedException("The project no longer exists.", transient: false, ErrorCodes.NotFound);
        var now = time.GetUtcNow();

        // FR-26 soft cap: the budget is checked only when a run starts (tokens are counted when it completes), so runs
        // in flight together — at most one execution job per project, each running its steps one after another — can
        // finish past the budget by what they use. The next run is then refused. Logged as a decision in PROGRESS.md.
        var budget = TokenBudget.CheckCanStart(project.TokenBudget, project.TokensUsed);
        if (budget.IsFailure)
        {
            db.AddEvent(
                EventTypes.BudgetExceeded,
                project.Id,
                new { agent = request.Agent.ToWire(), tokenBudget = project.TokenBudget, tokensUsed = project.TokensUsed, jobId = request.Job.Id },
                SystemActor.Name,
                now);
            await db.SaveChangesAsync(cancellationToken);
            LogBudgetExceeded(logger, project.TokensUsed, project.TokenBudget);
            throw new JobFailedException(budget.Error!.Message, transient: false, ErrorCodes.BudgetExceeded);
        }

        var resolved = await llm.ResolveAsync(project.Id, request.Agent, cancellationToken);
        var run = new AgentRun
        {
            Id = runId,
            ProjectId = project.Id,
            ReleaseVersionId = project.CurrentReleaseVersionId,
            Agent = request.Agent,
            Phase = project.CurrentPhase,
            StepId = request.StepId,
            Provider = resolved.Provider,
            Model = resolved.Model,
            CredentialId = resolved.CredentialId,
            PromptVersion = template.Version,
            InputArtifacts = request.InputArtifacts,
            UserFeedback = request.UserFeedback,
            Status = AgentRunStatus.Running,
            CreatedAt = now,
        };
        db.AgentRuns.Add(run);
        db.AddEvent(
            EventTypes.RunStarted,
            project.Id,
            new { runId, jobId = request.Job.Id, agent = request.Agent.ToWire(), provider = resolved.Provider.ToWire(), model = resolved.Model, promptVersion = template.Version, stepId = request.StepId },
            SystemActor.Name,
            now);
        await db.SaveChangesAsync(cancellationToken);
        LogStarted(logger, resolved.Provider, resolved.Model, template.Version);
        return run;
    }

    private async Task CompleteAsync(AgentRun run, LlmCompletion completion, long elapsedMs, Func<string, Result>? check, IReadOnlyList<string> warnings)
    {
        // timestamptz keeps microseconds: truncate so the outcome (and the draft dated by it) matches what a redelivered
        // job reads back from the stored run.
        var utcNow = time.GetUtcNow();
        var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        run.Status = AgentRunStatus.Succeeded;

        // Added: earlier attempts' paid tool-loop turns (RecordPaidTurnsAsync) are already on the run.
        run.InputTokens += completion.InputTokens;
        run.OutputTokens += completion.OutputTokens;
        run.CostEstimate = AgentRunRules.EstimateCost(run.InputTokens, run.OutputTokens, catalog.PriceOf(run.Model));
        run.DurationMs += elapsedMs;
        run.Output = AideJson.Serialize(new RunOutput(completion.Text, completion.StopReason) { Warnings = warnings.Count == 0 ? null : warnings });
        run.Error = null;
        run.CompletedAt = now;
        long tokens = completion.InputTokens + completion.OutputTokens;

        // The provider was paid for: record it even if the job is being cancelled. Only the run, its event and the token
        // count are saved here (nothing that can collide with other writers), so the tokens are never lost to a retry.
        var accepted = check is null ? Result.Success() : check(completion.Text);
        if (accepted.IsFailure)
        {
            MarkFailed(db, run, accepted.Error!.Code, accepted.Error.Message, deadLetter: false, now);
        }
        else
        {
            db.AddEvent(
                EventTypes.RunCompleted,
                run.ProjectId,
                new { runId = run.Id, agent = run.Agent.ToWire(), inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings },
                SystemActor.Name,
                now);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await db.Projects
            .Where(p => p.Id == run.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokensUsed, p => p.TokensUsed + tokens), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        if (accepted.IsFailure)
        {
            LogOutputRejected(logger, accepted.Error!.Code, run.InputTokens, run.OutputTokens);
            throw new JobFailedException(accepted.Error.Message, transient: false, accepted.Error.Code);
        }

        LogSucceeded(logger, run.InputTokens, run.OutputTokens, run.DurationMs, run.CostEstimate);
    }

    private async Task FailAsync(AgentRun run, string errorCode, string message, bool deadLetter)
    {
        MarkFailed(db, run, errorCode, message, deadLetter, time.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
        LogFailed(logger, errorCode, run.Attempts, deadLetter);
    }

    /// <summary>Fails <paramref name="run"/> and adds its <c>RUN_FAILED</c> event (the caller saves).</summary>
    internal static void MarkFailed(AideDbContext db, AgentRun run, string errorCode, string message, bool deadLetter, DateTimeOffset now)
    {
        run.Status = AgentRunStatus.Failed;
        run.Error = Truncate($"{errorCode}: {message}");
        run.CompletedAt = now;
        db.AddEvent(
            EventTypes.RunFailed,
            run.ProjectId,
            new { runId = run.Id, agent = run.Agent.ToWire(), errorCode, error = run.Error, attempts = run.Attempts, deadLettered = deadLetter },
            SystemActor.Name,
            now);
    }

    private static AgentRunOutcome Outcome(AgentRun run)
    {
        var output = ReadOutput(run.Output);
        return new AgentRunOutcome(run.Id, output?.Text ?? string.Empty, output?.StopReason ?? string.Empty, run.Provider, run.Model, run.PromptVersion, run.InputTokens, run.OutputTokens, run.CompletedAt ?? run.CreatedAt)
        {
            Warnings = output?.Warnings ?? [],
        };
    }

    /// <summary>The stored output of a run (<c>{"text": …, "stopReason": …}</c>), or <c>null</c>.</summary>
    public static RunOutput? ReadOutput(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return AideJson.Deserialize<RunOutput>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private long Elapsed(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;

    private static string Truncate(string value) => value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run started on {Provider} model {Model} (prompt v{PromptVersion})")]
    private static partial void LogStarted(ILogger logger, LlmProvider provider, string model, string promptVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Calling {Provider} model {Model} (attempt {Attempt})")]
    private static partial void LogCalling(ILogger logger, LlmProvider provider, string model, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run succeeded: {InputTokens} input / {OutputTokens} output tokens in {DurationMs} ms (cost {CostEstimate})")]
    private static partial void LogSucceeded(ILogger logger, int inputTokens, int outputTokens, long durationMs, decimal costEstimate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run failed: its output was rejected with {ErrorCode} ({InputTokens} input / {OutputTokens} output tokens)")]
    private static partial void LogOutputRejected(ILogger logger, string errorCode, int inputTokens, int outputTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run already succeeded; returning its output")]
    private static partial void LogAlreadySucceeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider attempt {Attempt} failed (transient: {Transient}, status {StatusCode}): {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, bool transient, int? statusCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider attempt {Attempt} failed unexpectedly (transient: {Transient}) with {ExceptionType}: {Reason}")]
    private static partial void LogUnexpectedFailure(ILogger logger, int attempt, bool transient, string exceptionType, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent run failed with {ErrorCode} after {Attempts} provider attempts (dead letter: {DeadLetter})")]
    private static partial void LogFailed(ILogger logger, string errorCode, int attempts, bool deadLetter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The provider rejected the key of LLM connection {CredentialId} ({Provider}); it is marked invalid")]
    private static partial void LogCredentialInvalid(ILogger logger, Guid credentialId, LlmProvider provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run refused: token budget used up ({TokensUsed} of {TokenBudget})")]
    private static partial void LogBudgetExceeded(ILogger logger, long tokensUsed, long? tokenBudget);
}

/// <summary>
/// What a run stores in <c>agent_runs.output</c>: the provider's text, why it stopped, and the run's warnings (FR-70,
/// FR-71; omitted when none).
/// </summary>
public sealed record RunOutput(string Text, string StopReason)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}
