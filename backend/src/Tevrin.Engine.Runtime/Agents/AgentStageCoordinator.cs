using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-296, §4.55: runs an agent stage until its output is valid or its <c>on_invalid</c> policy ends it. An invalid output
/// is retried with the findings as feedback (trigger <c>validation_failed</c>) while <c>retry_with_feedback</c> and the
/// attempt is below <c>max_attempts</c>; then <c>escalate</c> raises a pending <c>escalation</c> gate on the run, and
/// <c>fail</c> leaves the last agent run failed by the runner (the job layer fails the job with <see cref="AgentStageResult.ThrowIfFailed"/>).
/// </summary>
public sealed partial class AgentStageCoordinator(
    AgentStageRunner runner,
    IAgentStageStore store,
    IAgentStageHooks hooks,
    IEnumerable<AgentStageExtension> extensions,
    TimeProvider time,
    ILogger<AgentStageCoordinator> logger)
{
    public async Task<AgentStageResult> RunAsync(AgentStageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempts = new List<AgentStageOutcome>();
        var current = request;
        while (true)
        {
            var outcome = await runner.RunAsync(current, cancellationToken);
            attempts.Add(outcome);
            if (outcome.Valid)
            {
                return new AgentStageResult(AgentStageEnd.Stored, attempts, null);
            }

            switch (StageValidationRules.OnInvalid(outcome.Stage.OnInvalid, current.Attempt))
            {
                case InvalidOutputAction.Retry:
                    // §8: each validation retry is logged with counts only, never the findings' content (NFR-SEC).
                    var findingCount = FindingCount(outcome.Findings!);
                    LogValidationRetry(logger, outcome.Run.Id, outcome.Stage.Id, current.Attempt + 1, findingCount);

                    // §4.55, FR-314: the extension builds the retry (default: the findings appended to the request's own
                    // feedback, so a rejection comment stays). The attempt number is always the engine's.
                    var extension = AgentStageExtension.For(extensions, outcome.Run.PipelineId);
                    var next = await extension.NextAttemptAsync(request, current, outcome, cancellationToken);
                    current = next with { Attempt = current.Attempt + 1 };
                    break;

                case InvalidOutputAction.Escalate:
                    var gate = await EscalateAsync(outcome.Run, outcome.Findings!, cancellationToken);
                    return new AgentStageResult(AgentStageEnd.Escalated, attempts, gate);

                default:
                    // The runner already failed the run (FR-311's failed hook) and carries the failure (FR-314).
                    return new AgentStageResult(AgentStageEnd.Failed, attempts, null);
            }
        }
    }

    /// <summary>The number of findings (one per line, D-0.7.0-33's <c>pointer: message</c> list).</summary>
    public static int FindingCount(string findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: output invalid, retrying with feedback as attempt {Attempt} ({FindingCount} findings)")]
    private static partial void LogValidationRetry(ILogger logger, Guid pipelineRunId, string stageId, int attempt, int findingCount);

    /// <summary>The run's pending escalation gate, else a new one on the run (with an audit hook carrying the findings).</summary>
    private async Task<ApprovalGate> EscalateAsync(PipelineRun run, string findings, CancellationToken cancellationToken)
    {
        if (await store.PendingGateAsync(run, EngineGates.Escalation, cancellationToken) is { } pending)
        {
            return pending;
        }

        var gate = new ApprovalGate
        {
            ProjectId = run.ProjectId,
            ReleaseVersionId = AgentStageScope.ReleaseVersionOf(run),
            PipelineRunId = run.Id,
            Type = EngineGates.Escalation,
            Status = GateStatus.Pending,
            CreatedAt = time.GetUtcNow(),
        };
        store.Add(gate);
        await hooks.OnGateCreatedAsync(run, gate, findings, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return gate;
    }
}

/// <summary>FR-295, FR-296: DI registration of the agent stage runner.</summary>
public static class AgentStageServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="AgentStageRunner"/>, <see cref="AgentStageCoordinator"/>, <see cref="EngineAgentStageStore"/> and
    /// <see cref="NoAgentStageHooks"/> (scoped),
    /// <see cref="ArtifactSchemas"/> and the pack <see cref="IPromptSource"/> (singletons; a host may register its own
    /// source first). Needs the pipeline registry, an <see cref="Llm.ILlmResolver"/>, the host's
    /// <see cref="Persistence.IEngineDbContext"/> and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddAgentStages(this IServiceCollection services)
    {
        services.AddLogging(); // the coordinator logs validation retries (§8); idempotent
        services.TryAddScoped<IAgentStageStore, EngineAgentStageStore>();
        services.TryAddScoped<IAgentStageHooks, NoAgentStageHooks>();
        services.TryAddScoped<AgentStageRunner>();
        services.TryAddScoped<AgentStageCoordinator>();
        services.TryAddSingleton<ArtifactSchemas>();
        services.TryAddSingleton<IPromptSource, PackPromptSource>();
        return services;
    }

    /// <summary>FR-310–FR-316: registers a pipeline's <see cref="AgentStageExtension"/> (scoped; one per pipeline).</summary>
    public static IServiceCollection AddAgentStageExtension<T>(this IServiceCollection services)
        where T : AgentStageExtension
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<AgentStageExtension, T>());
        return services;
    }
}
