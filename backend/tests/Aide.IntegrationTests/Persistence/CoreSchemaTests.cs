using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>SPEC §5 data model against real PostgreSQL 17 with the migrations applied (CoreSchema).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CoreSchemaTests(AideApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DATA_core_schema_migration_is_applied_with_citext()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Ct), m => m.EndsWith("_CoreSchema", StringComparison.Ordinal));

        var citext = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'citext'")
            .SingleAsync(Ct);
        Assert.Equal(1, citext);
    }

    [Fact]
    public async Task DATA_every_aggregate_round_trips()
    {
        var user = NewUser();
        var project = NewProject();
        var plan = NewArtifact(project.Id, ArtifactType.ImplementationPlan, 1, user.Id);
        var run = new AgentRun
        {
            ProjectId = project.Id,
            Agent = AgentKind.PlanGenerator,
            Phase = Phase.Planning,
            Provider = LlmProvider.Openai,
            Model = "fake-model",
            PromptVersion = "plan-generator@1",
            InputArtifacts = [new InputArtifactRef(plan.Id, ArtifactType.TechnicalSpecification, 2)],
            Output = """{"questions":["Who are the users?"]}""",
            Status = AgentRunStatus.Succeeded,
            Attempts = 1,
            InputTokens = 1200,
            OutputTokens = 800,
            CostEstimate = 0.0123m,
            DurationMs = 4567,
            CreatedAt = Now,
        };
        var step = new ExecutionStep
        {
            ProjectId = project.Id,
            PlanArtifactId = plan.Id,
            StepNumber = 2,
            Title = "Add API",
            Task = "Create the endpoints",
            DeclaredFiles = [new DeclaredFile("src/api.cs", FileAction.Create)],
            Dependencies = [1],
            Status = StepStatus.InProgress,
            Violations = [new GuardianViolation(GuardianCheck.FileLimit, "FILE_LIMIT", ViolationSeverity.Error, "Too many files")],
            FileManifest = [new FileManifestEntry("src/api.cs", FileAction.Create, new string('a', 64))],
        };
        var gate = new ApprovalGate
        {
            ProjectId = project.Id,
            Type = GateType.Escalation,
            ArtifactId = plan.Id,
            Status = GateStatus.Approved,
            Decision = EscalationDecision.Rollback,
            TargetVersion = 2,
            ReviewerId = user.Id,
            CreatedAt = Now,
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.Users.Add(user);
            db.Projects.Add(project);
            db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Owner, CreatedAt = Now });
            db.ProjectGitSettings.Add(new ProjectGitSettings { ProjectId = project.Id, Provider = GitProvider.Github, RepoUrl = "https://github.com/x/y" });
            db.ProjectLlmSettings.Add(new ProjectLlmSettings
            {
                ProjectId = project.Id,
                Provider = LlmProvider.Anthropic,
                DefaultModel = "model-a",
                ModelsByAgent = new Dictionary<string, string> { ["idea_refinement"] = "model-b" },
            });
            db.ProjectNotificationSettings.Add(new ProjectNotificationSettings
            {
                ProjectId = project.Id,
                Channels = [new NotificationChannelConfig(NotificationChannelType.Slack, "https://hooks.slack.test/x", null, ["gate_required"])],
            });
            db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "cli", TokenHash = Hash(), CreatedAt = Now });
            db.Artifacts.Add(plan);
            db.AgentRuns.Add(run);
            db.ExecutionSteps.Add(step);
            db.ApprovalGates.Add(gate);
            db.ApprovalLinks.Add(new ApprovalLink { GateId = gate.Id, Decision = ApprovalLinkDecision.Approve, NonceHash = Hash(), ExpiresAt = Now.AddHours(72), CreatedAt = Now });
            db.StateTransitions.Add(new StateTransition { ProjectId = project.Id, FromPhase = null, ToPhase = Phase.Refinement, Actor = "aide-system", Reason = "created", ProjectVersion = 1, CreatedAt = Now });
            db.ComplianceReports.Add(new ComplianceReport
            {
                ProjectId = project.Id,
                ReviewCycle = 1,
                ProjectVersion = 5,
                Score = 0.825m,
                CategoryScores = [new CategoryScoreEntry(ReviewCategory.ApiContracts, 0.8m, "src/api.cs: missing route")],
                Violations = [new ReviewFinding(ReviewCategory.Security, Severity.Critical, "Secret found", "src/x.cs")],
                MissingFeatures = ["FR-9 settings"],
                DriftLevel = DriftLevel.Low,
                DriftAreas = [new DriftAreaEntry("api", Severity.Minor, "Extra route")],
                Decision = ReviewDecision.ReviewRequired,
                AgentRunId = run.Id,
                CreatedAt = Now,
            });
            db.RollbackEvents.Add(new RollbackEvent
            {
                ProjectId = project.Id,
                FromVersion = 3,
                TargetVersion = 2,
                Reason = "bad plan",
                InvalidatedArtifactIds = [plan.Id],
                ResetStepIds = [step.Id],
                TriggeredBy = user.Id.ToString(),
                CreatedAt = Now,
            });
            db.Releases.Add(new Release { ProjectId = project.Id, ProjectVersion = 7, Tag = "v7-aide", Status = ReleaseStatus.Failed, ConflictingFiles = ["a.txt"], CreatedAt = Now });
            db.Events.Add(new AuditEvent { ProjectId = project.Id, Type = "PHASE_CHANGED", Payload = """{"to":"planning"}""", Actor = "aide-system", CreatedAt = Now });
            db.Jobs.Add(new Job { ProjectId = project.Id, Type = "agent_run", Payload = """{"runId":"x"}""", RunAfter = Now, CreatedAt = Now });
            db.Notifications.Add(new Notification { UserId = user.Id, ProjectId = project.Id, Type = "gate_required", Title = "Gate", Body = "Approve", Link = "/projects/x", CreatedAt = Now });
            db.NotificationDeliveries.Add(new NotificationDelivery { ProjectId = project.Id, NotificationKey = $"gate_required:{gate.Id}", Channel = "slack", CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();

            var savedUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
            Assert.Equal(SystemRole.Admin, savedUser.SystemRole);

            var savedProject = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id, Ct);
            Assert.Equal(0.90m, savedProject.ComplianceThreshold);
            Assert.Equal(ApprovalMode.Advisory, savedProject.ApprovalMode);
            Assert.NotEqual(0u, savedProject.RowVersion);
            Assert.Equal(0, savedProject.ReviewCycle);
            Assert.Equal(0, savedProject.ExtraReviewCycles);

            var savedStep = await db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id, Ct);
            Assert.Equal(StepStatus.InProgress, savedStep.Status);
            Assert.Equal([1], savedStep.Dependencies);
            Assert.Equal(step.DeclaredFiles, savedStep.DeclaredFiles);
            Assert.Equal(step.Violations, savedStep.Violations);
            Assert.Equal(step.FileManifest, savedStep.FileManifest);

            var savedRun = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct);
            Assert.Equal(0.0123m, savedRun.CostEstimate);
            Assert.Equal(run.InputArtifacts, savedRun.InputArtifacts);
            Assert.Equal(LlmProvider.Openai, savedRun.Provider);

            var savedGate = await db.ApprovalGates.AsNoTracking().SingleAsync(g => g.Id == gate.Id, Ct);
            Assert.Equal(EscalationDecision.Rollback, savedGate.Decision);
            Assert.Equal(2, savedGate.TargetVersion);

            var report = await db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == project.Id, Ct);
            Assert.Equal(0.825m, report.Score);
            Assert.Equal(5, report.ProjectVersion);
            Assert.Equal(ReviewCategory.ApiContracts, Assert.Single(report.CategoryScores).Category);
            Assert.Equal(Severity.Critical, Assert.Single(report.Violations).Severity);
            Assert.Equal(["FR-9 settings"], report.MissingFeatures);

            var rollback = await db.RollbackEvents.AsNoTracking().SingleAsync(r => r.ProjectId == project.Id, Ct);
            Assert.Equal([plan.Id], rollback.InvalidatedArtifactIds);
            Assert.Equal([step.Id], rollback.ResetStepIds);

            var llm = await db.ProjectLlmSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct);
            Assert.Equal("model-b", llm.ModelsByAgent["idea_refinement"]);
            var channels = await db.ProjectNotificationSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct);
            Assert.Equal(["gate_required"], Assert.Single(channels.Channels).Events);

            Assert.Equal(["a.txt"], (await db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == project.Id, Ct)).ConflictingFiles);
            Assert.Equal(JobStatus.Queued, (await db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == project.Id, Ct)).Status);

            // Enums are stored as snake_case wire strings, also inside jsonb.
            var storedStatus = await db.Database
                .SqlQuery<string>($"SELECT status AS \"Value\" FROM execution_steps WHERE id = {step.Id}")
                .SingleAsync(Ct);
            Assert.Equal("in_progress", storedStatus);
            var storedCheck = await db.Database
                .SqlQuery<string>($"SELECT violations->0->>'check' AS \"Value\" FROM execution_steps WHERE id = {step.Id}")
                .SingleAsync(Ct);
            Assert.Equal("file_limit", storedCheck);
        }
    }

    [Fact]
    public async Task DATA_email_is_unique_case_insensitively()
    {
        var local = $"case-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        db.Users.Add(NewUser($"{local}@Example.com"));
        await db.SaveChangesAsync(Ct);

        db.Users.Add(NewUser($"{local.ToUpperInvariant()}@example.COM"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task FR12_only_one_pending_gate_per_project_and_type()
    {
        var project = NewProject();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        db.Projects.Add(project);
        db.ApprovalGates.Add(new ApprovalGate { ProjectId = project.Id, Type = GateType.SpecApproval, Status = GateStatus.Rejected, CreatedAt = Now });
        db.ApprovalGates.Add(new ApprovalGate { ProjectId = project.Id, Type = GateType.SpecApproval, CreatedAt = Now });
        db.ApprovalGates.Add(new ApprovalGate { ProjectId = project.Id, Type = GateType.PreRelease, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);

        var duplicate = new ApprovalGate { ProjectId = project.Id, Type = GateType.SpecApproval, CreatedAt = Now };
        db.ApprovalGates.Add(duplicate);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("ux_approval_gates_project_id_type_pending", pg.ConstraintName);
    }

    [Fact]
    public async Task NFR_REL_only_one_active_execution_job_per_project()
    {
        var project = NewProject();
        var other = NewProject();
        var first = new Job { ProjectId = project.Id, Type = Job.ExecutionType, RunAfter = Now, CreatedAt = Now };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        db.Projects.AddRange(project, other);
        db.Jobs.Add(first);
        db.Jobs.Add(new Job { ProjectId = project.Id, Type = "agent_run", RunAfter = Now, CreatedAt = Now });
        db.Jobs.Add(new Job { ProjectId = other.Id, Type = Job.ExecutionType, Status = JobStatus.Running, RunAfter = Now, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);

        var duplicate = new Job { ProjectId = project.Id, Type = Job.ExecutionType, RunAfter = Now, CreatedAt = Now };
        db.Jobs.Add(duplicate);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("ux_jobs_project_id_active_execution", pg.ConstraintName);

        db.Entry(duplicate).State = EntityState.Detached;
        first.Status = JobStatus.Succeeded;
        await db.SaveChangesAsync(Ct);

        db.Jobs.Add(new Job { ProjectId = project.Id, Type = Job.ExecutionType, RunAfter = Now, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task FR11_stale_project_update_throws_concurrency_exception()
    {
        var project = NewProject();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.Projects.Add(project);
            await db.SaveChangesAsync(Ct);
        }

        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AideDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AideDbContext>();
        var a = await dbA.Projects.SingleAsync(p => p.Id == project.Id, Ct);
        var b = await dbB.Projects.SingleAsync(p => p.Id == project.Id, Ct);

        a.CurrentPhase = Phase.Specification;
        a.ProjectVersion = 2;
        await dbA.SaveChangesAsync(Ct);

        b.CurrentPhase = Phase.Specification;
        b.ProjectVersion = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task FR7_deleting_a_project_cascades_to_everything_it_owns()
    {
        var user = NewUser();
        var project = NewProject();
        var plan = NewArtifact(project.Id, ArtifactType.ImplementationPlan, 1, user.Id);
        var run = new AgentRun { ProjectId = project.Id, Agent = AgentKind.CodeGenerator, Phase = Phase.Execution, Model = "m", PromptVersion = "1", CreatedAt = Now };
        var step = new ExecutionStep { ProjectId = project.Id, PlanArtifactId = plan.Id, StepNumber = 1, Title = "t", Task = "t" };
        var gate = new ApprovalGate { ProjectId = project.Id, Type = GateType.PlanApproval, ArtifactId = plan.Id, CreatedAt = Now };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.Users.Add(user);
            db.Projects.Add(project);
            db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Owner, CreatedAt = Now });
            db.ProjectGitSettings.Add(new ProjectGitSettings { ProjectId = project.Id });
            db.Artifacts.Add(plan);
            db.AgentRuns.Add(run);
            db.ExecutionSteps.Add(step);
            await db.SaveChangesAsync(Ct);

            run.StepId = step.Id;
            db.Artifacts.Add(NewArtifact(project.Id, ArtifactType.TechnicalSpecification, 1, user.Id, run.Id));
            db.ApprovalGates.Add(gate);
            db.ApprovalLinks.Add(new ApprovalLink { GateId = gate.Id, NonceHash = Hash(), ExpiresAt = Now, CreatedAt = Now });
            db.ComplianceReports.Add(new ComplianceReport { ProjectId = project.Id, ReviewCycle = 1, AgentRunId = run.Id, CreatedAt = Now });
            db.StateTransitions.Add(new StateTransition { ProjectId = project.Id, ToPhase = Phase.Refinement, Actor = "a", Reason = "r", ProjectVersion = 1, CreatedAt = Now });
            db.Events.Add(new AuditEvent { ProjectId = project.Id, Type = "PROJECT_CREATED", Actor = "a", CreatedAt = Now });
            db.Jobs.Add(new Job { ProjectId = project.Id, Type = "x", RunAfter = Now, CreatedAt = Now });
            db.Notifications.Add(new Notification { UserId = user.Id, ProjectId = project.Id, Type = "x", Title = "x", Body = "x", Link = "/", CreatedAt = Now });
            db.Releases.Add(new Release { ProjectId = project.Id, ProjectVersion = 1, Tag = "v1-aide", Status = ReleaseStatus.Succeeded, CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            db.Projects.Remove(await db.Projects.SingleAsync(p => p.Id == project.Id, Ct));
            await db.SaveChangesAsync(Ct);

            Assert.False(await db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id, Ct));
            Assert.False(await db.ProjectGitSettings.AnyAsync(s => s.ProjectId == project.Id, Ct));
            Assert.False(await db.Artifacts.AnyAsync(a => a.ProjectId == project.Id, Ct));
            Assert.False(await db.AgentRuns.AnyAsync(r => r.ProjectId == project.Id, Ct));
            Assert.False(await db.ExecutionSteps.AnyAsync(s => s.ProjectId == project.Id, Ct));
            Assert.False(await db.ApprovalGates.AnyAsync(g => g.ProjectId == project.Id, Ct));
            Assert.False(await db.ApprovalLinks.AnyAsync(l => l.GateId == gate.Id, Ct));
            Assert.False(await db.ComplianceReports.AnyAsync(r => r.ProjectId == project.Id, Ct));
            Assert.False(await db.StateTransitions.AnyAsync(t => t.ProjectId == project.Id, Ct));
            Assert.False(await db.Events.AnyAsync(e => e.ProjectId == project.Id, Ct));
            Assert.False(await db.Jobs.AnyAsync(j => j.ProjectId == project.Id, Ct));
            Assert.False(await db.Notifications.AnyAsync(n => n.ProjectId == project.Id, Ct));
            Assert.False(await db.Releases.AnyAsync(r => r.ProjectId == project.Id, Ct));
            Assert.True(await db.Users.AnyAsync(u => u.Id == user.Id, Ct));
        }
    }

    private static AppUser NewUser(string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        return new AppUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = "Test User",
            SystemRole = SystemRole.Admin,
            CreatedAt = Now,
        };
    }

    private static Project NewProject() => new()
    {
        Name = "Demo",
        Slug = $"demo-{Guid.NewGuid():N}",
        Idea = "An idea",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static Artifact NewArtifact(Guid projectId, ArtifactType type, int version, Guid userId, Guid? runId = null) => new()
    {
        ProjectId = projectId,
        Type = type,
        Version = version,
        Content = "# Plan",
        ContentHash = Hash(),
        Status = ArtifactStatus.Locked,
        ProjectVersionAtCreation = 1,
        CreatedByUserId = userId,
        AgentRunId = runId,
        CreatedAt = Now,
    };

    private static string Hash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
}
