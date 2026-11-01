using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Notifications;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aide.Infrastructure.Persistence;

/// <summary>The AIDE database (PostgreSQL, snake_case names, plural table names). Entities are configured with
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this assembly; Identity tables are renamed to
/// <c>users</c>, <c>roles</c>, <c>user_claims</c>, … in <see cref="OnModelCreating"/>.</summary>
public sealed class AideDbContext(DbContextOptions<AideDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>Max length of enum wire-value columns.</summary>
    public const int EnumMaxLength = 32;

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectMembership> ProjectMembers => Set<ProjectMembership>();

    public DbSet<ProjectGitSettings> ProjectGitSettings => Set<ProjectGitSettings>();

    public DbSet<ProjectLlmSettings> ProjectLlmSettings => Set<ProjectLlmSettings>();

    public DbSet<ProjectNotificationSettings> ProjectNotificationSettings => Set<ProjectNotificationSettings>();

    public DbSet<LlmCredential> LlmCredentials => Set<LlmCredential>();

    public DbSet<GitHubApp> GitHubApps => Set<GitHubApp>();

    public DbSet<GitHubConnection> GitHubConnections => Set<GitHubConnection>();

    public DbSet<GitHostInstance> GitHostInstances => Set<GitHostInstance>();

    public DbSet<UserExternalLogin> UserExternalLogins => Set<UserExternalLogin>();

    public DbSet<GitConnection> GitConnections => Set<GitConnection>();

    public DbSet<PlatformConnection> PlatformConnections => Set<PlatformConnection>();

    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();

    public DbSet<DeploymentEnvVar> DeploymentEnvVars => Set<DeploymentEnvVar>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<ManagedDatabase> ManagedDatabases => Set<ManagedDatabase>();

    public DbSet<DatabaseMigrationRun> DatabaseMigrationRuns => Set<DatabaseMigrationRun>();

    public DbSet<Artifact> Artifacts => Set<Artifact>();

    public DbSet<ApprovalGate> ApprovalGates => Set<ApprovalGate>();

    public DbSet<ExecutionStep> ExecutionSteps => Set<ExecutionStep>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<StateTransition> StateTransitions => Set<StateTransition>();

    public DbSet<ComplianceReport> ComplianceReports => Set<ComplianceReport>();

    public DbSet<RollbackEvent> RollbackEvents => Set<RollbackEvent>();

    public DbSet<Release> Releases => Set<Release>();

    public DbSet<ReleaseVersion> ReleaseVersions => Set<ReleaseVersion>();

    public DbSet<ExecutionStepAttempt> ExecutionStepAttempts => Set<ExecutionStepAttempt>();

    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    public DbSet<ApprovalLink> ApprovalLinks => Set<ApprovalLink>();

    public DbSet<UserNotificationPreferences> UserNotificationPreferences => Set<UserNotificationPreferences>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every stored enum is a snake_case string (CLAUDE.md Conventions → Data).
        MapEnum<SystemRole>(configurationBuilder);
        MapEnum<ProjectRole>(configurationBuilder);
        MapEnum<Phase>(configurationBuilder);
        MapEnum<ProjectStatus>(configurationBuilder);
        MapEnum<ApprovalMode>(configurationBuilder);
        MapEnum<ArtifactType>(configurationBuilder);
        MapEnum<ArtifactStatus>(configurationBuilder);
        MapEnum<GateType>(configurationBuilder);
        MapEnum<GateStatus>(configurationBuilder);
        MapEnum<EscalationDecision>(configurationBuilder);
        MapEnum<StepStatus>(configurationBuilder);
        MapEnum<AgentKind>(configurationBuilder);
        MapEnum<AgentRunStatus>(configurationBuilder);
        MapEnum<LlmProvider>(configurationBuilder);
        MapEnum<LlmCredentialStatus>(configurationBuilder);
        MapEnum<GitProvider>(configurationBuilder);
        MapEnum<GitHubOwnerType>(configurationBuilder);
        MapEnum<GitHubConnectionStatus>(configurationBuilder);
        MapEnum<GitHostKind>(configurationBuilder);
        MapEnum<ExternalLoginProvider>(configurationBuilder);
        MapEnum<GitConnectionAuthKind>(configurationBuilder);
        MapEnum<GitConnectionStatus>(configurationBuilder);
        MapEnum<PlatformProvider>(configurationBuilder);
        MapEnum<PlatformConnectionStatus>(configurationBuilder);
        MapEnum<DeploymentProvider>(configurationBuilder);
        MapEnum<DeploymentEnvironment>(configurationBuilder);
        MapEnum<DeploymentComponent>(configurationBuilder);
        MapEnum<DeploymentTargetStatus>(configurationBuilder);
        MapEnum<DeploymentKind>(configurationBuilder);
        MapEnum<DeploymentEnvSource>(configurationBuilder);
        MapEnum<DeploymentStatus>(configurationBuilder);
        MapEnum<DeploymentTrigger>(configurationBuilder);
        MapEnum<ManagedDatabaseProvider>(configurationBuilder);
        MapEnum<ManagedDatabaseStatus>(configurationBuilder);
        MapEnum<MigrationRunStatus>(configurationBuilder);
        MapEnum<DriftLevel>(configurationBuilder);
        MapEnum<ReviewDecision>(configurationBuilder);
        MapEnum<EscalationReason>(configurationBuilder);
        MapEnum<ReleaseStatus>(configurationBuilder);
        MapEnum<ReleaseVersionStatus>(configurationBuilder);
        MapEnum<MergeStrategy>(configurationBuilder);
        MapEnum<StepAttemptOutcome>(configurationBuilder);
        MapEnum<JobStatus>(configurationBuilder);
        MapEnum<NotificationDeliveryStatus>(configurationBuilder);
        MapEnum<ApprovalLinkDecision>(configurationBuilder);
        MapEnum<NotificationType>(configurationBuilder);

        // UTC timestamptz everywhere.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamp with time zone");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("citext");

        // Rename the Identity principal tables first so dependent FK names use the final table names.
        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.ApplyConfigurationsFromAssembly(typeof(AideDbContext).Assembly);
    }

    private static void MapEnum<TEnum>(ModelConfigurationBuilder configurationBuilder)
        where TEnum : struct, Enum =>
        configurationBuilder.Properties<TEnum>()
            .HaveConversion<EnumWireConverter<TEnum>>()
            .HaveMaxLength(EnumMaxLength);
}
