using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// D-0.7.0-12: the engine's part of the one Tevrin database. The host's DbContext applies
/// <see cref="ApplyTevrinEngineConventions"/> in <c>ConfigureConventions</c> and <see cref="ApplyTevrinEngineModel"/> in
/// <c>OnModelCreating</c>; migrations stay with the host (Tevrin.Infrastructure). The engine configures its tables, columns
/// and indexes; relationships to host tables (projects, users, gates) and host-only indexes stay in the host's
/// configurations of the same entities.
/// </summary>
public static class TevrinEngineModel
{
    /// <summary>Max length of enum wire-value columns.</summary>
    public const int EnumMaxLength = 32;

    /// <summary>Max length of pipeline, pack, stage and status ids.</summary>
    public const int IdMaxLength = 100;

    /// <summary>Every engine enum is stored as its snake_case wire value (<see cref="EnumWireConverter{TEnum}"/>).</summary>
    public static ModelConfigurationBuilder ApplyTevrinEngineConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        MapEnum<ApprovalMode>(configurationBuilder);
        MapEnum<ArtifactStatus>(configurationBuilder);
        MapEnum<GateStatus>(configurationBuilder);
        MapEnum<EscalationDecision>(configurationBuilder);
        MapEnum<AgentRunStatus>(configurationBuilder);
        MapEnum<LlmProvider>(configurationBuilder);
        MapEnum<JobStatus>(configurationBuilder);
        MapEnum<NotificationDeliveryStatus>(configurationBuilder);
        MapEnum<ApprovalLinkDecision>(configurationBuilder);
        MapEnum<GenericRunStatus>(configurationBuilder);
        return configurationBuilder;
    }

    /// <summary>The engine's tables: <c>events</c>, <c>jobs</c>, <c>notifications</c>, <c>notification_deliveries</c>,
    /// <c>approval_links</c>, <c>artifacts</c>, <c>approval_gates</c>, <c>pipeline_definitions</c>, <c>pipeline_runs</c>,
    /// <c>agent_runs</c> and <c>stage_executions</c>.</summary>
    public static ModelBuilder ApplyTevrinEngineModel(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<AuditEvent>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Type).HasMaxLength(100).IsRequired();
            b.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.Actor).HasMaxLength(200).IsRequired();

            b.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            b.HasIndex(e => e.Type);

            // FR-51 cross-project admin list: ORDER BY created_at DESC, id DESC.
            b.HasIndex(e => new { e.CreatedAt, e.Id }, "ix_events_created_at_id").IsDescending(true, true);
        });

        builder.Entity<Job>(b =>
        {
            b.ToTable("jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.Id).ValueGeneratedNever();
            b.Property(j => j.Type).HasMaxLength(100).IsRequired();
            b.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(j => j.LockedBy).HasMaxLength(200);

            // Claim query: WHERE status = 'queued' AND run_after <= now ORDER BY run_after FOR UPDATE SKIP LOCKED.
            b.HasIndex(j => new { j.Status, j.RunAfter });

            // Plain FK index (the host's partial unique index on project_id doesn't serve the project cascade or
            // per-project lookups).
            b.HasIndex(j => j.ProjectId, "ix_jobs_project_id");
        });

        builder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(n => n.Id);
            b.Property(n => n.Id).ValueGeneratedNever();
            b.Property(n => n.Type).HasMaxLength(100).IsRequired();
            b.Property(n => n.Title).HasMaxLength(300).IsRequired();
            b.Property(n => n.Body).IsRequired();
            b.Property(n => n.Link).HasMaxLength(1000).IsRequired();
            b.Property(n => n.NotificationKey).HasMaxLength(300);

            // Unread count: WHERE user_id = @u AND read_at IS NULL.
            b.HasIndex(n => new { n.UserId, n.ReadAt });

            // Keyset paging, newest first: WHERE user_id = @u AND (created_at, id) < (@c, @i) ORDER BY created_at DESC, id DESC.
            b.HasIndex(n => new { n.UserId, n.CreatedAt, n.Id }, "ix_notifications_user_id_created_at_id")
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_notifications_user_id_created_at_id");

            // FR-49: one in-app notification per (user, logical notification); keyless notifications are not deduplicated.
            b.HasIndex(n => new { n.UserId, n.NotificationKey }, "ux_notifications_user_id_notification_key")
                .IsUnique()
                .HasFilter("notification_key IS NOT NULL")
                .HasDatabaseName("ux_notifications_user_id_notification_key");
        });

        builder.Entity<NotificationDelivery>(b =>
        {
            b.ToTable("notification_deliveries");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.NotificationKey).HasMaxLength(300).IsRequired();
            b.Property(d => d.Channel).HasMaxLength(100).IsRequired();

            b.HasIndex(d => new { d.NotificationKey, d.Channel }).IsUnique();

            // Retry sweep: WHERE status = 'pending' AND next_attempt_at <= now ORDER BY next_attempt_at.
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
        });

        builder.Entity<ApprovalLink>(b =>
        {
            b.ToTable("approval_links");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.Property(l => l.NonceHash).HasMaxLength(64).IsRequired();

            b.HasIndex(l => l.NonceHash).IsUnique();
        });

        builder.Entity<Artifact>(b =>
        {
            b.ToTable("artifacts");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();

            // FR-270, FR-274: the artifact key (a pack's artifact keys), the same column as the 0.6.1 enum.
            b.Property(a => a.Type).HasMaxLength(EnumMaxLength);
            b.Property(a => a.Content).IsRequired();
            b.Property(a => a.ContentHash).HasMaxLength(64).IsRequired();
            XminRowVersion(b.Property(a => a.RowVersion));

            // Artifact versions keep counting across release versions (FR-58, FR-59), so uniqueness stays per project.
            b.HasIndex(a => new { a.ProjectId, a.Type, a.Version }).IsUnique();
            b.HasIndex(a => new { a.ProjectId, a.Type, a.Status });
            b.HasIndex(a => new { a.ProjectId, a.ReleaseVersionId });
            b.HasIndex(a => a.ReleaseVersionId);

            // FR-294: rows of the same project; they go together through the project cascade (NO ACTION, as for versions).
            b.HasOne<PipelineRun>().WithMany().HasForeignKey(a => a.PipelineRunId).OnDelete(DeleteBehavior.NoAction);

            // §8: the run timeline reads by run in time order (FR-300).
            b.HasIndex(a => new { a.PipelineRunId, a.CreatedAt });

            // Kept explicitly: the composite index would otherwise replace the foreign-key index (expand only).
            b.HasIndex(a => a.PipelineRunId);
        });

        builder.Entity<ApprovalGate>(b =>
        {
            b.ToTable("approval_gates");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).ValueGeneratedNever();

            // FR-271, FR-274: the gate key (EngineGates and a pack's gate keys), the same column as the 0.6.1 enum.
            b.Property(g => g.Type).HasMaxLength(EnumMaxLength);
            XminRowVersion(b.Property(g => g.RowVersion));

            b.HasOne<Artifact>().WithMany().HasForeignKey(g => g.ArtifactId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<PipelineRun>().WithMany().HasForeignKey(g => g.PipelineRunId).OnDelete(DeleteBehavior.NoAction);

            // §8: the run timeline reads by run in time order (FR-300).
            b.HasIndex(g => new { g.PipelineRunId, g.CreatedAt });

            // Kept explicitly: the composite index would otherwise replace the foreign-key index (expand only).
            b.HasIndex(g => g.PipelineRunId);

            // At most one pending gate per (project, type) — enforced by the database (§4.2).
            b.HasIndex(g => new { g.ProjectId, g.Type })
                .IsUnique()
                .HasFilter("status = 'pending'")
                .HasDatabaseName("ux_approval_gates_project_id_type_pending");
            b.HasIndex(g => new { g.ProjectId, g.Status });
            b.HasIndex(g => new { g.ProjectId, g.ReleaseVersionId });
            b.HasIndex(g => g.ReleaseVersionId);
        });

        ApplyPipelineRuns(builder);
        ApplyRunLog(builder);
        return builder;
    }

    /// <summary>FR-268, FR-297, §5: <c>agent_runs</c> and <c>stage_executions</c>. The host adds the agent run's relationships
    /// to its tables (projects, steps, release versions, LLM connections).</summary>
    private static void ApplyRunLog(ModelBuilder builder)
    {
        builder.Entity<AgentRun>(b =>
        {
            b.ToTable("agent_runs");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();

            // FR-269, FR-274: the agent key, the same column as the 0.6.1 enum.
            b.Property(r => r.Agent).HasMaxLength(EnumMaxLength);

            // D-0.7.0-32: the stage id lives in the existing phase column (same values, no stage_id column).
            b.Property(r => r.Stage).HasColumnName("phase").HasMaxLength(EnumMaxLength).IsRequired();
            b.Property(r => r.Model).HasMaxLength(100).IsRequired();
            b.Property(r => r.PromptVersion).HasMaxLength(50).IsRequired();
            b.Property(r => r.InputArtifacts)
                .HasConversion(InputArtifactsConverter, InputArtifactsComparer)
                .HasColumnType("jsonb")
                .IsRequired();
            b.Property(r => r.Output).HasColumnType("jsonb");
            b.Property(r => r.CostEstimate).HasPrecision(10, 4);
            b.Property(r => r.AgentKey).HasMaxLength(100);
            b.Property(r => r.PresetId).HasMaxLength(100);
            b.Property(r => r.PresetVersion).HasMaxLength(50);
            b.Property(r => r.Packs).HasColumnType("jsonb");
            b.Property(r => r.CatalogVersion).HasColumnType("text");
            b.Property(r => r.ToolCalls).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();

            // FR-297, D-0.7.0-16: additive; rows recorded before 0.7.0 keep a null input and latency.
            b.Property(r => r.Input).HasColumnType("text");
            b.Property(r => r.InputTruncated).HasDefaultValue(false).IsRequired();

            b.HasOne<PipelineRun>().WithMany().HasForeignKey(r => r.PipelineRunId).OnDelete(DeleteBehavior.NoAction);

            // §8: the run timeline reads by run in time order (FR-300).
            b.HasIndex(r => new { r.PipelineRunId, r.CreatedAt });

            // Kept explicitly: the composite index would otherwise replace the foreign-key index (expand only).
            b.HasIndex(r => r.PipelineRunId);

            b.HasIndex(r => new { r.ProjectId, r.CreatedAt });
            b.HasIndex(r => new { r.Status, r.CreatedAt });
            b.HasIndex(r => new { r.ProjectId, r.ReleaseVersionId });
            b.HasIndex(r => r.ReleaseVersionId);

            // FR-51 cross-project admin list: ORDER BY created_at DESC, id DESC.
            b.HasIndex(r => new { r.CreatedAt, r.Id }, "ix_agent_runs_created_at_id").IsDescending(true, true);
        });

        builder.Entity<StageExecution>(b =>
        {
            b.ToTable("stage_executions");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.StageId).HasMaxLength(IdMaxLength).IsRequired();
            b.Property(e => e.Outcome).HasMaxLength(EnumMaxLength);

            // A run's timings go with the run (and the run with its project).
            b.HasOne<PipelineRun>().WithMany().HasForeignKey(e => e.PipelineRunId).OnDelete(DeleteBehavior.Cascade);

            // §5: a run's executions in order.
            b.HasIndex(e => new { e.PipelineRunId, e.EnteredAt });
        });
    }

    private static readonly JsonSerializerOptions InputArtifactsJson = new(JsonSerializerDefaults.Web);

    // The same JSON as before the move (camelCase properties: artifactId, type, version).
    private static readonly ValueConverter<IReadOnlyList<InputArtifactRef>, string> InputArtifactsConverter = new(
        v => JsonSerializer.Serialize(v, InputArtifactsJson),
        s => JsonSerializer.Deserialize<List<InputArtifactRef>>(s, InputArtifactsJson) ?? new List<InputArtifactRef>());

    private static readonly ValueComparer<IReadOnlyList<InputArtifactRef>> InputArtifactsComparer = new(
        (a, b) => JsonSerializer.Serialize(a, InputArtifactsJson) == JsonSerializer.Serialize(b, InputArtifactsJson),
        v => JsonSerializer.Serialize(v, InputArtifactsJson).GetHashCode(StringComparison.Ordinal),
        v => JsonSerializer.Deserialize<List<InputArtifactRef>>(JsonSerializer.Serialize(v, InputArtifactsJson), InputArtifactsJson)!);

    /// <summary>FR-277, FR-294, §5: <c>pipeline_definitions</c> and <c>pipeline_runs</c>. The host adds the run's
    /// relationship to its projects table (D-0.7.0-11).</summary>
    private static void ApplyPipelineRuns(ModelBuilder builder)
    {
        builder.Entity<PipelineDefinitionRecord>(b =>
        {
            b.ToTable("pipeline_definitions");
            b.HasKey(d => d.Hash);
            HashColumn(b.Property(d => d.Hash));
            b.Property(d => d.PipelineId).HasMaxLength(IdMaxLength).IsRequired();
            b.Property(d => d.PackId).HasMaxLength(IdMaxLength).IsRequired();
            b.Property(d => d.CanonicalJson).HasColumnType("jsonb").IsRequired();
        });

        builder.Entity<PipelineRun>(b =>
        {
            b.ToTable("pipeline_runs");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.PipelineId).HasMaxLength(IdMaxLength).IsRequired();
            HashColumn(b.Property(r => r.DefinitionHash));
            b.Property(r => r.CurrentStage).HasMaxLength(IdMaxLength).IsRequired();
            b.Property(r => r.PackStatus).HasMaxLength(IdMaxLength).IsRequired();
            b.Property(r => r.Iterations)
                .HasConversion(IterationsConverter, IterationsComparer)
                .HasColumnType("jsonb")
                .IsRequired();
            XminRowVersion(b.Property(r => r.RowVersion));

            // FR-277: a run is pinned to a stored definition, which is never deleted.
            b.HasOne<PipelineDefinitionRecord>().WithMany().HasForeignKey(r => r.DefinitionHash).OnDelete(DeleteBehavior.Restrict);

            // D-0.7.0-10: one run per subject (the SDLC's release version) and pipeline.
            b.HasIndex(r => new { r.PipelineId, r.SubjectId })
                .IsUnique()
                .HasDatabaseName("ux_pipeline_runs_pipeline_id_subject_id");

            // §5: at most one run per project and pipeline that hasn't ended (final statuses, §4.57).
            b.HasIndex(r => new { r.ProjectId, r.PipelineId })
                .IsUnique()
                .HasFilter("status NOT IN ('completed', 'failed', 'cancelled')")
                .HasDatabaseName("ux_pipeline_runs_project_id_pipeline_id_open");

            // The project cascade and per-project lookups (the partial index covers open runs only).
            b.HasIndex(r => r.ProjectId, "ix_pipeline_runs_project_id");
        });
    }

    private static readonly JsonSerializerOptions IterationsJson = new();

    private static readonly ValueConverter<IReadOnlyDictionary<string, int>, string> IterationsConverter = new(
        v => SerializeIterations(v),
        s => DeserializeIterations(s));

    private static readonly ValueComparer<IReadOnlyDictionary<string, int>> IterationsComparer = new(
        (a, b) => SerializeIterations(a!) == SerializeIterations(b!),
        v => SerializeIterations(v).GetHashCode(StringComparison.Ordinal),
        v => DeserializeIterations(SerializeIterations(v)));

    private static string SerializeIterations(IReadOnlyDictionary<string, int> iterations) =>
        JsonSerializer.Serialize(
            new SortedDictionary<string, int>(iterations.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            IterationsJson);

    private static Dictionary<string, int> DeserializeIterations(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json, IterationsJson) is { } parsed
            ? new Dictionary<string, int>(parsed, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>A definition hash: SHA-256, lower-case hex, <c>character(64)</c>.</summary>
    private static void HashColumn(PropertyBuilder<string> property) =>
        property.HasMaxLength(64).IsFixedLength().IsRequired();

    /// <summary>Maps the property to PostgreSQL's <c>xmin</c> system column as an optimistic concurrency token (no physical
    /// column is created).</summary>
    private static void XminRowVersion(PropertyBuilder<uint> property) =>
        property.HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

    private static void MapEnum<TEnum>(ModelConfigurationBuilder configurationBuilder)
        where TEnum : struct, Enum =>
        configurationBuilder.Properties<TEnum>()
            .HaveConversion<EnumWireConverter<TEnum>>()
            .HaveMaxLength(EnumMaxLength);
}
