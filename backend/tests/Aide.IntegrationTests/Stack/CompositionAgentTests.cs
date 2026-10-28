using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Review;
using Aide.Api.Features.Versions;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Stack;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Stack;

/// <summary>
/// FR-120–FR-128 end to end with the Fake provider and Local Git, on a composed project (fe-react-vite + be-aspnet +
/// data-efcore-postgres): run records (catalog version, packs), the composed spec and plan, specialist execution, the
/// composed review checks, INVALID_PLAN from a composed rule, the Guardian's immutable migrations, the
/// UNDECLARED_GENERATED_FILE warning and the Repository Analyst's detected mismatch.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CompositionAgentTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Task Board\n\n## Summary\nA task board.";

    private static readonly string[] Fr1 = ["FR-1"];

    private static readonly int[] DependsOnStep1 = [1];

    private static readonly string[] SetupModelling = ["Setup", "Modelling"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly StackSelection AspnetSpa = new()
    {
        Frontend = new StackComponentPick("fe-react-vite"),
        Backend = new StackComponentPick("be-aspnet"),
        Data = new StackDataPick("data-efcore-postgres"),
        Vcs = new StackVcsPick("vcs-github"),
    };

    private static StackComposition Compose(WebApplicationFactory<Program> app, StackSelection selection)
    {
        var validation = StackCompatibility.Validate(selection, app.Services.GetRequiredService<IStackCatalog>().Current);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Violations.Select(v => v.Reason)));
        return validation.Composition!;
    }

    private static async Task<StackComposition> SetCompositionAsync(WebApplicationFactory<Program> app, Guid projectId, StackSelection? selection = null)
    {
        var composition = Compose(app, selection ?? AspnetSpa);
        var json = StackCompositionJson.Serialize(composition);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == projectId).ExecuteUpdateAsync(
            s => s.SetProperty(v => v.StackComposition, json).SetProperty(v => v.CatalogVersion, composition.CatalogVersion).SetProperty(v => v.CatalogHash, composition.CatalogHash),
            Ct));
        return composition;
    }

    private static string Spec(params string[] extra) =>
        string.Join("\n\n", SpecSections.Required
            .Select((name, i) => name == "Functional Requirements"
                ? $"## {i + 1}. {name}\n- **FR-1** Add a task. Given a title, when it is posted, then it is stored."
                : $"## {i + 1}. {name}\nDetails about {name}.")
            .Concat(extra.Select(t => $"## {t}\nDetails about {t}.")));

    private static List<JsonElement> Packs(AgentRun run) =>
        [.. JsonDocument.Parse(run.Packs!).RootElement.EnumerateArray().Select(e => e.Clone())];

    private static async Task<ComplianceReportDto> LatestReportAsync(ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<ComplianceReportDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/latest", UriKind.Relative), Ct));

    // ---------- the composed path end to end ----------

    [Fact]
    public async Task FR120_FR121_FR123_FR124_FR126_A_Fake_composed_project_goes_spec_plan_execution_review_with_specialists_and_run_records()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var composition = await SetCompositionAsync(factory, id);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Refinement, 1, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: ProjectRequest);

        foreach (var type in new[] { ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan })
        {
            await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
            Assert.All(await AgentJobs.RunQueuedAsync(factory, id), j => Assert.Equal(JobStatus.Succeeded, j.Status));
            var draft = (await AgentJobs.ArtifactsAsync(factory, id, type)).Single();
            await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));
        }

        // FR-123: the spec has the composition's sections; FR-121: spec and plan runs record the catalog, with no packs.
        var spec = (await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.TechnicalSpecification)).Single();
        Assert.Contains("## API Contract & Error Codes\n", spec.Content, StringComparison.Ordinal);
        Assert.Contains("## Data Model & Migrations\n", spec.Content, StringComparison.Ordinal);
        var early = await AgentJobs.RunsAsync(factory, id);
        Assert.All(early, r => Assert.Equal((composition.CatalogVersion, "[]"), (r.CatalogVersion, r.Packs)));

        // FR-124: the plan uses the specialists, scaffolder first, and its steps carry them.
        var plan = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/implementation_plan/latest"));
        Assert.DoesNotContain(plan.Warnings, w => w.Code is ErrorCodes.PlanOrder or ErrorCodes.UndeclaredGeneratedFile);
        var agents = await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.AsNoTracking().Where(s => s.PlanArtifactId == plan.Id).OrderBy(s => s.StepNumber).Select(s => s.AgentKey).ToListAsync(Ct));
        Assert.Equal(StackAgents.Scaffolder, agents[0]);
        Assert.Contains(StackAgents.DataEngineer, agents);
        Assert.Contains(StackAgents.BackendEngineer, agents);
        Assert.DoesNotContain(PlanAgents.Generalist, agents);

        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(factory, id);

        // FR-121, FR-125: each step ran as its specialist; the backend-engineer's run records the be-aspnet skill.
        var runs = await AgentJobs.RunsAsync(factory, id);
        var backend = runs.Single(r => r.Agent == AgentKind.CodeGenerator && r.AgentKey == StackAgents.BackendEngineer);
        Assert.Equal(composition.CatalogVersion, backend.CatalogVersion);
        var loaded = Packs(backend);
        Assert.Contains(loaded, p => p.GetProperty("id").GetString() == "be-aspnet" && p.GetProperty("version").GetString()!.StartsWith("1.0.0", StringComparison.Ordinal));
        Assert.Contains(loaded, p => p.GetProperty("id").GetString() == StackCoreSkills.ApiContract);
        Assert.Contains(loaded, p => p.GetProperty("id").GetString() == "data-efcore-postgres"
            && p.GetProperty("sections").EnumerateArray().Select(s => s.GetString()).SequenceEqual(SetupModelling));
        var started = await LifecycleSeed.EventsAsync(factory, id, EventTypes.RunStarted);
        Assert.Contains(started, e => LifecycleSeed.Payload(e.Payload).GetProperty("stack").GetString() == $"stack@{composition.CatalogVersion}");

        // FR-126: the Fake files pass every composed deterministic check, and the review reaches release-eligible.
        var report = await LatestReportAsync(fixture);
        Assert.DoesNotContain(report.Violations, v => v.Source == FindingSource.Deterministic);
        if (report.Decision == ReviewDecision.ReviewRequired)
        {
            var optimization = (await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.OptimizationPlan)).Single();
            await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, optimization.Id, "approve"));
            await AgentJobs.RunQueuedAsync(factory, id);
            report = await LatestReportAsync(fixture);
        }

        Assert.Equal(ReviewDecision.ReleaseEligible, report.Decision);
    }

    // ---------- FR-124 ----------

    private static async Task PlanningReadyAsync(WebApplicationFactory<Program> app, Guid id)
    {
        await SetCompositionAsync(app, id);
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Specification, 2, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: Spec("API Contract & Error Codes", "Data Model & Migrations"), projectVersionAtCreation: 2);
    }

    [Fact]
    public async Task FR124_A_backend_engineer_step_creating_a_migration_is_re_run_then_fails_with_INVALID_PLAN_naming_the_rule()
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { number = 1, title = "Skeleton", task = "Scaffold.", files = new[] { new { path = "backend/App.slnx", action = "create" } }, dependencies = Array.Empty<int>(), agent = "scaffolder", covers = Fr1 },
                new { number = 2, title = "Schema", task = "Migrate.", files = new[] { new { path = "backend/src/App.Infrastructure/Migrations/20260101000000_Init.cs", action = "create" } }, dependencies = DependsOnStep1, agent = "backend-engineer", covers = Fr1 },
            },
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.PlanGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        Assert.Equal(JobStatus.Failed, Assert.Single(await AgentJobs.RunQueuedAsync(app, id)).Status);

        var runs = await AgentJobs.RunsAsync(app, id);
        Assert.Equal(2, runs.Count);
        Assert.StartsWith("INVALID_PLAN: ", runs[^1].Error, StringComparison.Ordinal);
        Assert.Contains("Migrations belong to data-engineer", runs[^1].Error, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));
    }

    [Fact]
    public async Task FR127_A_backend_step_adding_endpoints_without_the_contract_file_is_an_UNDECLARED_GENERATED_FILE_warning()
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { number = 1, title = "Skeleton", task = "Scaffold.", files = new[] { new { path = "backend/App.slnx", action = "create" } }, dependencies = Array.Empty<int>(), agent = "scaffolder", covers = Fr1 },
                new { number = 2, title = "API", task = "Endpoints.", files = new[] { new { path = "backend/src/App.Api/Features/Tasks/TasksEndpoints.cs", action = "create" } }, dependencies = DependsOnStep1, agent = "backend-engineer", covers = Fr1 },
            },
        });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.PlanGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await PlanningReadyAsync(app, id);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        Assert.Equal(JobStatus.Succeeded, Assert.Single(await AgentJobs.RunQueuedAsync(app, id)).Status);

        var plan = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/implementation_plan/latest"));
        var warning = Assert.Single(plan.Warnings, w => w.Code == ErrorCodes.UndeclaredGeneratedFile);
        Assert.Contains("backend/openapi/api.json", warning.Message, StringComparison.Ordinal);
    }

    // ---------- FR-125 / FR-126: execution and review ----------

    private static async Task<IReadOnlyList<ExecutionStep>> SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id, PlanStep[] plan)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nReact + ASP.NET + EF Core.");
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
                Status = StepStatus.Pending,
            }).ToList();
            db.ExecutionSteps.AddRange(steps);
            await db.SaveChangesAsync(Ct);
            return steps;
        });
    }

    [Fact]
    public async Task FR125_A_data_engineer_step_modifying_a_migration_on_the_base_commit_is_rejected_by_the_Guardian()
    {
        const string migration = "backend/src/App.Infrastructure/Migrations/20260101000000_Init.cs";
        var reply = JsonSerializer.Serialize(new { stepNumber = 1, files = new[] { new { path = migration, content = "// edited\n" } }, notes = "scripted" });
        await using var app = AgentJobs.WithResponders(factory, new ScriptedResponder(AgentKind.CodeGenerator, reply));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetCompositionAsync(app, id);
        var steps = await SeedExecutionAsync(app, id, [new(1, "Init", "Edit the init migration.", [new DeclaredFile(migration, FileAction.Modify)], [], Agent: StackAgents.DataEngineer)]);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        var baseStep = Guid.NewGuid();
        var commit = await app.Services.GetRequiredService<IWorkingCopies>().CommitStepAsync(
            id, branch, baseStep, [new ProducedFile(migration, "// initial\n")], CodeGeneration.CommitMessage(0, "Base", [migration], baseStep), Ct);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.BaseCommitSha, commit.Sha), Ct));

        await AgentJobs.RunQueuedAsync(app, id);

        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == steps[0].Id, Ct));
        Assert.NotEqual(StepStatus.Completed, step.Status);
        Assert.Contains(step.Violations!, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Message.Contains("immutable", StringComparison.Ordinal));
        var run = (await AgentJobs.RunsAsync(app, id)).First(r => r.StepId == steps[0].Id);
        Assert.Equal(StackAgents.DataEngineer, run.AgentKey);
        Assert.Contains(Packs(run), p => p.GetProperty("id").GetString() == "data-efcore-postgres");
    }

    [Fact]
    public async Task FR126_EnsureCreated_in_backend_code_is_a_major_architecture_finding_costing_0_10()
    {
        var review = new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.95m] });
        var code = JsonSerializer.Serialize(new
        {
            stepNumber = 1,
            files = new[] { new { path = "backend/src/App.Api/Program.cs", content = "// /api/health/live /api/health/ready\ndb.Database.EnsureCreated();\n" } },
            notes = "scripted",
        });
        await using var app = AgentJobs.WithResponders(factory, review, new ScriptedResponder(AgentKind.CodeGenerator, code));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetCompositionAsync(app, id);
        await SeedExecutionAsync(app, id, [new(1, "Startup", "Program.", [new DeclaredFile("backend/src/App.Api/Program.cs", FileAction.Create)], [], Agent: StackAgents.BackendEngineer)]);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Contains(jobs, j => j.Type == JobTypes.Review && j.Status == JobStatus.Succeeded);
        var latest = await LatestReportAsync(fixture);
        var finding = Assert.Single(latest.Violations, v => v.Source == FindingSource.Deterministic);
        Assert.Equal((ReviewCategory.Architecture, Severity.Major, "backend/src/App.Api/Program.cs"), (finding.Category, finding.Severity, finding.File));
        var architecture = latest.CategoryScores.Single(c => c.Category == ReviewCategory.Architecture);
        Assert.Equal((0.95m, 0.10m, 0.85m), (architecture.AgentScore, architecture.Penalty, architecture.Score));
        var reviewRun = (await AgentJobs.RunsAsync(app, id)).Single(r => r.Agent == AgentKind.CodeReview);
        Assert.Equal("[]", reviewRun.Packs);
    }

    // ---------- FR-128 ----------

    [Fact]
    public async Task FR128_An_import_detected_as_fe_vue_with_fe_react_vite_chosen_flags_the_mismatch_and_the_spec_needs_Stack_Migration()
    {
        var analyst = new CapturingResponder(new FakeRepositoryAnalystResponder());
        var specs = new CapturingResponder(new FakeSpecGeneratorResponder());
        await using var app = AgentJobs.WithResponders(factory, analyst, specs);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SetCompositionAsync(app, id);

        // The base commit holds a Vue + Vite frontend.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var prepared = await scope.ServiceProvider.GetRequiredService<WorkingCopyPreparer>().PrepareAsync(id, Ct);
            Assert.True(prepared.IsSuccess, prepared.Error?.Message);
            var baseStep = Guid.NewGuid();
            const string manifest = "frontend/package.json";
            var commit = await scope.ServiceProvider.GetRequiredService<IWorkingCopies>().CommitStepAsync(
                id, prepared.Value.Branch, baseStep,
                [new ProducedFile(manifest, "{\"dependencies\":{\"vue\":\"3.5.0\"},\"devDependencies\":{\"vite\":\"7.0.0\"}}\n"), new ProducedFile("frontend/index.html", "<div id=\"app\"></div>\n")],
                CodeGeneration.CommitMessage(0, "Base", [manifest], baseStep), Ct);
            await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.BaseCommitSha, commit.Sha), Ct));
            var versionId = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.Id).SingleAsync(Ct));
            await ProjectApi.WithDbAsync(app, async db =>
            {
                await new JobEnqueuer(db, TimeProvider.System).EnqueueAsync(JobTypes.RepositoryAnalysis, new RepositoryAnalysisJobPayload(id, versionId), id, Ct);
                await db.SaveChangesAsync(Ct);
            });
        }

        Assert.All(await AgentJobs.RunQueuedAsync(app, id), j => Assert.Equal(JobStatus.Succeeded, j.Status));

        var summary = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.CodebaseSummary));
        var stackSection = SpecSections.Extract(summary.Content, "Stack & Dependencies")!;
        Assert.Contains("fe-vue", stackSection, StringComparison.Ordinal);
        Assert.Contains("mismatch", stackSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fe-react-vite", stackSection, StringComparison.Ordinal);
        Assert.Contains("`vue`", FakePromptTag(analyst.Requests[0], "stack_detection"), StringComparison.Ordinal);

        // The spec job requires "Stack Migration" (the Fake spec adds it).
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Refinement, 1, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        Assert.All(await AgentJobs.RunQueuedAsync(app, id), j => Assert.Equal(JobStatus.Succeeded, j.Status));
        Assert.Contains("#### Stack Migration", specs.Requests[^1].System, StringComparison.Ordinal);
        var spec = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification));
        Assert.Contains("## Stack Migration\n", spec.Content, StringComparison.Ordinal);
    }

    private static string FakePromptTag(LlmRequest request, string name)
    {
        var text = string.Join("\n", request.Messages.Select(m => m.Content));
        var start = text.IndexOf($"<{name}>", StringComparison.Ordinal);
        var end = text.IndexOf($"</{name}>", StringComparison.Ordinal);
        return start < 0 || end < start ? string.Empty : text[(start + name.Length + 2)..end];
    }

    /// <summary>Wraps a responder and records every request it answers (system prompt included).</summary>
    internal sealed class CapturingResponder(IFakeAgentResponder inner) : IFakeAgentResponder
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
