using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Review;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Presets;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Presets;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Presets;

/// <summary>
/// FR-74–FR-77 end to end with the Fake provider and Local Git: run stamping, the spec's preset sections and corrective
/// re-run, preset plan generation and its warnings, specialist execution, the preset review rules, and the projects list
/// <c>preset=none</c> filter.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PresetAgentTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Task Board\n\n## Summary\nA task board.";

    private static readonly string[] Fr1 = ["FR-1"];

    private static readonly int[] DependsOnStep1 = [1];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Preset Shipped(string id) => factory.Services.GetRequiredService<IPresetCatalog>().Find(id)!;

    private static Task<int> SetPresetAsync(WebApplicationFactory<Program> app, Guid projectId, Preset preset) =>
        ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == projectId).ExecuteUpdateAsync(
            s => s.SetProperty(v => v.PresetId, preset.Id).SetProperty(v => v.PresetVersion, preset.Version).SetProperty(v => v.PresetHash, preset.ContentHash),
            Ct));

    private static string Spec(Preset preset, params string[] missing) =>
        string.Join("\n\n", SpecSections.Required
            .Select((name, i) => name == "Functional Requirements"
                ? $"## {i + 1}. {name}\n- **FR-1** Add a task. Given a title, when it is posted, then it is stored."
                : $"## {i + 1}. {name}\nDetails about {name}.")
            .Concat(preset.SpecSectionTitles.Where(t => !missing.Contains(t)).Select(t => $"## {t}\nDetails about {t}.")));

    // ---------- FR-74 / FR-75: spec ----------

    [Fact]
    public async Task FR74_FR75_A_nextjs_frontend_spec_run_records_the_preset_and_the_Fake_spec_has_its_sections()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var preset = Shipped("nextjs-frontend");
        await SetPresetAsync(factory, id, preset);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(factory, id));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var run = Assert.Single(await AgentJobs.RunsAsync(factory, id));
        Assert.Equal((AgentKind.SpecGenerator, "nextjs-frontend", preset.Version, (string?)null), (run.Agent, run.PresetId, run.PresetVersion, run.AgentKey));
        Assert.Equal($"nextjs-frontend@{preset.Version}", PresetRulesLabel(run));
        var spec = Assert.Single(await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.TechnicalSpecification));
        Assert.All(preset.SpecSectionTitles, t => Assert.Contains($"## {t}\n", spec.Content, StringComparison.Ordinal));
    }

    private static string PresetRulesLabel(AgentRun run) => PresetRules.RunLabel(run.PresetId!, run.PresetVersion!);

    [Fact]
    public async Task FR75_A_nextjs_supabase_spec_lacking_Data_Model_and_RLS_Policies_is_re_run_once_with_that_section_named()
    {
        var preset = Shipped("nextjs-supabase");
        var responder = new ScriptedResponder(AgentKind.SpecGenerator, Spec(preset, "Data Model & RLS Policies"), Spec(preset));
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetPresetAsync(app, id, preset);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, responder.Requests.Count);
        Assert.Contains("#### Data Model & RLS Policies", responder.Requests[0].System, StringComparison.Ordinal);
        Assert.Contains("\"Data Model & RLS Policies\"", responder.Prompts[1], StringComparison.Ordinal);
        var runs = await AgentJobs.RunsAsync(app, id);
        Assert.All(runs, r => Assert.Equal("nextjs-supabase", r.PresetId));
        Assert.Contains("## Data Model & RLS Policies", Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification)).Content, StringComparison.Ordinal);
    }

    // ---------- FR-76: plan ----------

    private static async Task PlanningReadyAsync(WebApplicationFactory<Program> app, Guid id, Preset preset)
    {
        await SetPresetAsync(app, id, preset);
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Specification, 2, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: Spec(preset), projectVersionAtCreation: 2);
    }

    [Fact]
    public async Task FR76_The_Fake_plans_a_nextjs_frontend_project_with_specialist_agents_valid_for_the_preset()
    {
        var preset = Shipped("nextjs-frontend");
        var recording = new CapturingResponder(new FakePlanGeneratorResponder());
        await using var app = AgentJobs.WithResponders(factory, recording);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id, preset);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("- `ui-engineer` (UI engineer):", recording.Requests[0].System, StringComparison.Ordinal);
        Assert.Contains("This repository is empty", recording.Requests[0].System, StringComparison.Ordinal);
        var plan = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/implementation_plan/latest"));
        Assert.Empty(plan.Warnings);
        var steps = PlanDocument.ParseMarkdown(plan.Content, 20, PlanAgents.For(preset, repoEmpty: true)).Value;
        Assert.Equal(["project-scaffolder", "domain-logic-engineer", "api-engineer", "ui-engineer", "test-engineer", "devops-engineer"], steps.Select(s => s.Agent));
        Assert.Equal("nextjs-frontend", Assert.Single(await AgentJobs.RunsAsync(app, id)).PresetId);

        // Approving it creates steps carrying their agents.
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, plan.Id, "approve"));
        var agents = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().Where(s => s.PlanArtifactId == plan.Id).OrderBy(s => s.StepNumber).Select(s => s.AgentKey).ToListAsync(Ct));
        Assert.Equal(steps.Select(s => s.Agent), agents);
    }

    [Fact]
    public async Task FR76_A_step_declaring_files_outside_its_agent_is_an_AGENT_SCOPE_warning_on_the_plan()
    {
        var preset = Shipped("nextjs-frontend");
        var reply = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { number = 1, title = "Skeleton", task = "Scaffold.", files = new[] { new { path = "package.json", action = "create" } }, dependencies = Array.Empty<int>(), agent = "project-scaffolder", covers = Fr1 },
                new { number = 2, title = "API", task = "Routes.", files = new[] { new { path = "app/api/tasks/route.ts", action = "create" }, new { path = "components/TaskList.tsx", action = "create" } }, dependencies = DependsOnStep1, agent = "api-engineer", covers = Fr1 },
            },
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.PlanGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id, preset);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        Assert.Equal(JobStatus.Succeeded, Assert.Single(await AgentJobs.RunQueuedAsync(app, id)).Status);

        var plan = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/implementation_plan/latest"));
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal(ErrorCodes.AgentScope, warning.Code);
        Assert.Contains("'components/TaskList.tsx'", Assert.Single(warning.Ids), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR76_A_plan_naming_ml_engineer_under_nextjs_frontend_is_re_run_then_fails_with_INVALID_PLAN_naming_it()
    {
        var reply = System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = new[] { new { number = 1, title = "Model", task = "Train.", files = new[] { new { path = "lib/model/train.ts", action = "create" } }, dependencies = Array.Empty<int>(), agent = "ml-engineer" } },
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.PlanGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id, Shipped("nextjs-frontend"));
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        Assert.Equal(JobStatus.Failed, Assert.Single(await AgentJobs.RunQueuedAsync(app, id)).Status);

        var runs = await AgentJobs.RunsAsync(app, id);
        Assert.StartsWith("INVALID_PLAN: ", runs[^1].Error, StringComparison.Ordinal);
        Assert.Contains("'ml-engineer'", runs[^1].Error, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));
    }

    // ---------- FR-76: execution ----------

    [Fact]
    public async Task FR76_Step_3_assigned_db_engineer_runs_as_code_generator_with_agent_key_and_the_supabase_migration_skill()
    {
        var preset = Shipped("nextjs-supabase");
        var recording = new CapturingResponder(new FakeCodeGeneratorResponder());
        await using var app = AgentJobs.WithResponders(factory, recording);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetPresetAsync(app, id, preset);
        PlanStep[] plan =
        [
            new(1, "Skeleton", "Scaffold.", [new DeclaredFile("package.json", FileAction.Create)], [], Agent: "project-scaffolder"),
            new(2, "Profiles", "Profiles table.", [new DeclaredFile("supabase/migrations/0001_profiles.sql", FileAction.Create)], [1], Agent: "db-engineer"),
            new(3, "Tasks", "Tasks table with RLS.", [new DeclaredFile("supabase/migrations/0002_tasks.sql", FileAction.Create)], [2], Agent: "db-engineer"),
        ];
        var steps = await SeedExecutionAsync(app, id, plan, completedThrough: 2);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        await AgentJobs.RunQueuedAsync(app, id);

        var run = (await AgentJobs.RunsAsync(app, id)).Single(r => r.StepId == steps[2].Id);
        Assert.Equal((AgentKind.CodeGenerator, "db-engineer", "nextjs-supabase", preset.Version), (run.Agent, run.AgentKey, run.PresetId, run.PresetVersion));
        var system = recording.Requests[0].System;
        Assert.Contains("## Your role: Database engineer (db-engineer)", system, StringComparison.Ordinal);
        Assert.Contains("## Skill: supabase-migration", system, StringComparison.Ordinal);
        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == steps[2].Id, Ct));
        Assert.Equal(StepStatus.Completed, step.Status);
    }

    [Fact]
    public async Task S4_11_A_step_writing_an_undeclared_preset_protected_path_is_rejected_by_the_Guardian()
    {
        var preset = Shipped("nextjs-supabase");
        var reply = System.Text.Json.JsonSerializer.Serialize(new
        {
            stepNumber = 1,
            files = new[] { new { path = "supabase/migrations/0001_tasks.sql", content = "create table tasks (id int);\nalter table tasks enable row level security;\n" }, new { path = "lib/database.types.ts", content = "export type Database = {};\n" } },
            notes = "scripted",
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetPresetAsync(app, id, preset);
        var steps = await SeedExecutionAsync(app, id, [new(1, "Tasks", "Tasks table.", [new DeclaredFile("supabase/migrations/0001_tasks.sql", FileAction.Create)], [], Agent: "db-engineer")]);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        await AgentJobs.RunQueuedAsync(app, id);

        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == steps[0].Id, Ct));
        Assert.Contains(step.Violations!, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Path == "lib/database.types.ts");
    }

    [Fact]
    public async Task S4_11_A_declared_migration_that_exists_on_the_base_commit_is_rejected_by_the_Guardian_at_execution()
    {
        var preset = Shipped("nextjs-supabase");
        var reply = System.Text.Json.JsonSerializer.Serialize(new
        {
            stepNumber = 1,
            files = new[] { new { path = "supabase/migrations/0001_init.sql", content = "create table tasks (id int);\nalter table tasks enable row level security;\n" } },
            notes = "scripted",
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetPresetAsync(app, id, preset);
        var steps = await SeedExecutionAsync(app, id, [new(1, "Init", "Edit the init migration.", [new DeclaredFile("supabase/migrations/0001_init.sql", FileAction.Modify)], [], Agent: "db-engineer")]);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        // The base commit holds the migration (committed before the step runs).
        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        var baseStep = Guid.NewGuid();
        var commit = await app.Services.GetRequiredService<Aide.Infrastructure.Git.IWorkingCopies>().CommitStepAsync(
            id, branch, baseStep, [new ProducedFile("supabase/migrations/0001_init.sql", "create table notes (id int);\n")], CodeGeneration.CommitMessage(0, "Base", ["supabase/migrations/0001_init.sql"], baseStep), Ct);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.BaseCommitSha, commit.Sha), Ct));

        await AgentJobs.RunQueuedAsync(app, id);

        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == steps[0].Id, Ct));
        Assert.NotEqual(StepStatus.Completed, step.Status);
        Assert.Contains(step.Violations!, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Message.Contains("immutable", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<ExecutionStep>> SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id, PlanStep[] plan, int completedThrough = 0)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nNext.js + Supabase.");
        var planArtifact = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: PlanDocument.Render(ArtifactType.ImplementationPlan, plan));
        return await ProjectApi.WithDbAsync<IReadOnlyList<ExecutionStep>>(app, async db =>
        {
            var steps = plan.Select(p => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planArtifact.Id,
                StepNumber = p.Number,
                Title = p.Title,
                Task = p.Task,
                DeclaredFiles = p.Files,
                Dependencies = p.Dependencies,
                AgentKey = p.Agent,
                Status = p.Number <= completedThrough ? StepStatus.Completed : StepStatus.Pending,
                Attempt = p.Number <= completedThrough ? 1 : 0,
            }).ToList();
            db.ExecutionSteps.AddRange(steps);
            await db.SaveChangesAsync(Ct);
            return steps;
        });
    }

    // ---------- FR-77: review ----------

    [Fact]
    public async Task FR77_A_migration_creating_tasks_without_RLS_is_a_critical_deterministic_finding_costing_security_0_20()
    {
        var preset = Shipped("nextjs-supabase");
        var review = new CapturingResponder(new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.95m] }));
        var codeReply = System.Text.Json.JsonSerializer.Serialize(new
        {
            stepNumber = 1,
            files = new[] { new { path = "supabase/migrations/0001_tasks.sql", content = "create table public.tasks (id uuid primary key);\n" } },
            notes = "scripted",
        });
        await using var app = AgentJobs.WithResponders(factory, review, new ScriptedResponder(AgentKind.CodeGenerator, codeReply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetPresetAsync(app, id, preset);
        await SeedExecutionAsync(app, id, [new(1, "Tasks", "Tasks table.", [new DeclaredFile("supabase/migrations/0001_tasks.sql", FileAction.Create)], [], Agent: "db-engineer")]);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Contains(jobs, j => j.Type == JobTypes.Review && j.Status == JobStatus.Succeeded);
        var latest = await ProjectApi.ReadAsync<ComplianceReportDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/latest", UriKind.Relative), Ct));
        var security = latest.CategoryScores.Single(c => c.Category == ReviewCategory.Security);
        Assert.Equal((0.95m, 0.20m, 0.75m), (security.AgentScore, security.Penalty, security.Score));
        var finding = Assert.Single(latest.Violations, v => v.Source == FindingSource.Deterministic);
        Assert.Equal((ReviewCategory.Security, Severity.Critical, "supabase/migrations/0001_tasks.sql"), (finding.Category, finding.Severity, finding.File));
        Assert.Contains("[C1] (security, critical)", review.Requests[0].System, StringComparison.Ordinal);
        Assert.Equal("nextjs-supabase", (await AgentJobs.RunsAsync(app, id)).Single(r => r.Agent == AgentKind.CodeReview).PresetId);
    }

    // ---------- §8: the Fake preset path end to end ----------

    [Fact]
    public async Task S8_A_Fake_nextjs_frontend_project_goes_spec_plan_execution_review_to_release_eligible()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SetPresetAsync(factory, id, Shipped("nextjs-frontend"));
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Refinement, 1, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: ProjectRequest);

        // Specification, then planning: each Fake draft approved and the project advanced.
        foreach (var type in new[] { ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan })
        {
            await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
            Assert.All(await AgentJobs.RunQueuedAsync(factory, id), j => Assert.Equal(JobStatus.Succeeded, j.Status));
            var draft = (await AgentJobs.ArtifactsAsync(factory, id, type)).Single();
            await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        }

        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(factory, id);

        var report = await LatestReportAsync(fixture);
        Assert.DoesNotContain(report.Violations, v => v.Source == FindingSource.Deterministic);
        if (report.Decision == ReviewDecision.ReviewRequired)
        {
            // The default Fake review asks for one optimisation loop; approving its plan runs it and review cycle 2.
            var optimization = (await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.OptimizationPlan)).Single();
            await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, optimization.Id, "approve"));
            await AgentJobs.RunQueuedAsync(factory, id);
            report = await LatestReportAsync(fixture);
        }

        Assert.Equal(ReviewDecision.ReleaseEligible, report.Decision);
        Assert.DoesNotContain(report.Violations, v => v.Source == FindingSource.Deterministic);
    }

    private static async Task<ComplianceReportDto> LatestReportAsync(ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<ComplianceReportDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/latest", UriKind.Relative), Ct));

    // ---------- FR-74: projects list filter ----------

    [Fact]
    public async Task FR74_The_projects_list_preset_filter_none_lists_projects_without_a_preset()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var plain = await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("plain"), idea = "An idea for a plain project.", git = new { provider = "local" } }, Ct), HttpStatusCode.Created);
        var stacked = await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName("stacked"), idea = "An idea for a preset project.", git = new { provider = "local" }, presetId = "nextjs-frontend" }, Ct), HttpStatusCode.Created);

        var none = await ListIdsAsync(owner.Client, "none");
        var frontend = await ListIdsAsync(owner.Client, "nextjs-frontend");

        Assert.Equal([plain.Id], none);
        Assert.Equal([stacked.Id], frontend);
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, string preset)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/projects?preset={preset}&limit=100", UriKind.Relative), Ct));
        return [.. doc.RootElement.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid())];
    }

    /// <summary>Wraps a responder and records every request it answers (system prompt included).</summary>
    private sealed class CapturingResponder(IFakeAgentResponder inner) : IFakeAgentResponder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LlmRequest> _requests = new();

        public AgentKind Agent => inner.Agent;

        public IReadOnlyList<LlmRequest> Requests => [.. _requests];

        public string Respond(LlmRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _requests.Enqueue(request);
            return inner.Respond(request);
        }
    }
}
