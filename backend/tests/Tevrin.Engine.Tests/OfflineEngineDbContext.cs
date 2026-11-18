using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Tests;

/// <summary>The engine's tables on a provider that is configured but never connected.</summary>
internal sealed class OfflineEngineDbContext : DbContext, IEngineDbContext
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    public DbSet<ApprovalLink> ApprovalLinks => Set<ApprovalLink>();

    public DbSet<PipelineDefinitionRecord> PipelineDefinitions => Set<PipelineDefinitionRecord>();

    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<StageExecution> StageExecutions => Set<StageExecution>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseNpgsql("Host=unused.invalid;Database=unused");

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.ApplyTevrinEngineConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyTevrinEngineModel();
}
