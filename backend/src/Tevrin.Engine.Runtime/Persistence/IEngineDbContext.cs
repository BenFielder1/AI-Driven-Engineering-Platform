using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// D-0.7.0-12, D-0.7.0-20: the engine's view of the host's one DbContext: the engine tables (configured by
/// <see cref="TevrinEngineModel"/>) and the unit of work. The host's DbContext implements it and is registered as it, so
/// the engine and the host share one scoped instance and one transaction.
/// </summary>
public interface IEngineDbContext
{
    DbSet<Job> Jobs { get; }

    DbSet<AuditEvent> Events { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<NotificationDelivery> NotificationDeliveries { get; }

    DbSet<ApprovalLink> ApprovalLinks { get; }

    /// <summary>FR-277: stored definitions, by hash.</summary>
    DbSet<PipelineDefinitionRecord> PipelineDefinitions { get; }

    /// <summary>FR-294: pipeline runs.</summary>
    DbSet<PipelineRun> PipelineRuns { get; }

    /// <summary>FR-268, FR-297: agent runs (the run log).</summary>
    DbSet<AgentRun> AgentRuns { get; }

    /// <summary>FR-297: per-stage timings of pipeline runs.</summary>
    DbSet<StageExecution> StageExecutions { get; }

    /// <summary>FR-295: artifact versions (the agent stage runner reads inputs and stores outputs).</summary>
    DbSet<Artifact> Artifacts { get; }

    /// <summary>FR-296: approval gates (the agent stage runner raises <c>escalation</c> gates).</summary>
    DbSet<ApprovalGate> ApprovalGates { get; }

    /// <summary>FR-336: <c>pipeline_schedules</c>.</summary>
    DbSet<PipelineSchedule> PipelineSchedules { get; }

    /// <summary>FR-337: <c>pipeline_webhooks</c>.</summary>
    DbSet<PipelineWebhook> PipelineWebhooks { get; }

    /// <summary>FR-337: <c>webhook_deliveries</c>.</summary>
    DbSet<WebhookDelivery> WebhookDeliveries { get; }

    /// <summary>FR-351: <c>agent_feedback</c>.</summary>
    DbSet<AgentFeedback> AgentFeedback { get; }

    /// <summary>FR-353: <c>outcome_scores</c>.</summary>
    DbSet<OutcomeScore> OutcomeScores { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
