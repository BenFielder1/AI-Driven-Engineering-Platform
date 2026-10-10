using System.Net;
using Aide.Api.Features.Execution;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Execution;

/// <summary>FR-18 (edited plans), FR-30 (steps created when a plan is locked) and <c>GET /execution/steps</c>.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionStepsTests(AideApiFactory factory)
{
    private const string Plan = "implementation_plan";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR30_An_approved_plan_with_12_steps_lists_12_pending_steps_for_that_plan_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: AgentJobs.PlanMarkdown(12), projectVersionAtCreation: 3);

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, plan.Id, "approve"));
        var listed = await StepsAsync(fixture.Viewer.Client, id);

        Assert.Equal(new ExecutionPlanDto(plan.Id, ArtifactType.ImplementationPlan, 1, ArtifactStatus.Locked), listed.Plan);
        Assert.Equal(12, listed.Steps.Count);
        Assert.All(listed.Steps, s => Assert.Equal((StepStatus.Pending, 0), (s.Status, s.Attempt)));
        Assert.Equal(Enumerable.Range(1, 12), listed.Steps.Select(s => s.Number));
        var step2 = listed.Steps[1];
        Assert.Equal(("Step 2", "Build part 2.", (string?)null), (step2.Title, step2.Task, step2.UserInstructions));
        Assert.Equal([new DeclaredFileDto("src/part2.js", FileAction.Create, false)], step2.DeclaredFiles);
        Assert.Equal([1], step2.Dependencies);
        Assert.Null(step2.Violations);
        var approved = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactApproved)).Payload);
        Assert.Equal(12, approved.GetProperty("createdStepCount").GetInt32());

        // Approving the locked plan again is a no-op: no second set of steps.
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, plan.Id, "approve"));
        Assert.Equal(12, await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.CountAsync(s => s.PlanArtifactId == plan.Id, Ct)));
    }

    [Fact]
    public async Task FR30_Approving_a_newer_plan_creates_its_steps_and_invalidates_the_older_plans_steps()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var v1 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: AgentJobs.PlanMarkdown(2), projectVersionAtCreation: 3);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, v1.Id, "approve"));

        var v2 = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Plan, AgentJobs.PlanMarkdown(3)), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, v2.Id, "approve"));

        var current = await StepsAsync(fixture.Viewer.Client, id);
        Assert.Equal((2, 3), (current.Plan!.Version, current.Steps.Count));
        Assert.All(current.Steps, s => Assert.Equal(StepStatus.Pending, s.Status));
        var older = await StepsAsync(fixture.Viewer.Client, id, "?planVersion=1");
        Assert.Equal((1, 2), (older.Plan!.Version, older.Steps.Count));
        Assert.All(older.Steps, s => Assert.Equal(StepStatus.Invalidated, s.Status));
    }

    [Fact]
    public async Task FR30_Approving_a_plan_without_a_valid_aide_plan_block_is_INVALID_PLAN_and_changes_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: "# Plan\n\n1. Build it");

        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, plan.Id, "approve"), HttpStatusCode.BadRequest, ErrorCodes.InvalidPlan);

        Assert.Equal(ArtifactStatus.Draft, (await ArtifactApi.RowAsync(factory, plan.Id)).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactApproved));
    }

    [Fact]
    public async Task FR30_Approving_an_optimisation_plan_during_review_creates_its_steps_and_starts_the_review_cycle()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, ReviewDecision.ReviewRequired);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
        var optimisation = await LifecycleSeed.AddArtifactAsync(
            factory, id, ArtifactType.OptimizationPlan, ArtifactStatus.Draft, content: AgentJobs.PlanMarkdown(3, ArtifactType.OptimizationPlan), projectVersionAtCreation: 5);

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, optimisation.Id, "approve"));

        var steps = await StepsAsync(fixture.Viewer.Client, id, "?planType=optimization_plan");
        Assert.Equal((ArtifactType.OptimizationPlan, 3), (steps.Plan!.Type, steps.Steps.Count));
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR30_Steps_query_without_a_plan_is_empty_and_bad_queries_are_rejected()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var empty = await StepsAsync(fixture.Viewer.Client, id);
        Assert.Null(empty.Plan);
        Assert.Empty(empty.Steps);

        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, id, "?planVersion=3"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, id, "?planVersion=0"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, id, "?planType=project_request"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, id, "?planType=nope"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await GetAsync(fixture.Outsider.Client, id, string.Empty), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------- FR-18: human-edited plans ----------

    [Fact]
    public async Task FR18_An_edited_plan_draft_is_validated_from_its_block_and_stored_re_rendered()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var canonical = AgentJobs.PlanMarkdown(2);
        var edited = canonical.Replace("## Step 1: Step 1", "## Step 1: A heading the user typed", StringComparison.Ordinal);

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Plan, edited), HttpStatusCode.Created);

        Assert.Equal(canonical, draft.Content);
        Assert.Equal(ContentHash.Compute(canonical), draft.ContentHash);
    }

    [Theory]
    [InlineData("# My plan\n\n1. Build everything")]
    [InlineData("```aide-plan\n{\"steps\": [{\"number\": 1, \"title\": \"A\", \"task\": \"B\", \"files\": [{\"path\": \"a.js\", \"action\": \"create\"}], \"dependencies\": [2]}]}\n```")]
    [InlineData("```aide-plan\n{\"steps\": [{\"number\": 1, \"title\": \"A\", \"task\": \"B\", \"files\": [{\"path\": \".git/hooks/pre-commit\", \"action\": \"create\"}]}]}\n```")]
    public async Task FR18_An_edited_plan_draft_that_breaks_the_plan_rules_is_INVALID_PLAN(string content)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);

        var body = await Problems.AssertAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Plan, content), HttpStatusCode.BadRequest, ErrorCodes.InvalidPlan);

        Assert.NotEmpty(body.GetProperty("details").EnumerateArray());
        Assert.Empty(await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.ImplementationPlan));
    }

    [Fact]
    public async Task FR18_An_edited_plan_with_more_files_per_step_than_the_project_allows_is_INVALID_PLAN()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxFilesPerStep, 1), Ct));
        var twoFiles = PlanDocument.Render(ArtifactType.ImplementationPlan, [new PlanStep(1, "A", "B", [new DeclaredFile("a.js", FileAction.Create), new DeclaredFile("b.js", FileAction.Create)], [])]);

        await Problems.AssertAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Plan, twoFiles), HttpStatusCode.BadRequest, ErrorCodes.InvalidPlan);
    }

    [Fact]
    public async Task FR29_An_owner_edited_plan_may_declare_a_protected_path_and_its_step_lists_it_as_protected()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var content = PlanDocument.Render(ArtifactType.ImplementationPlan, [new PlanStep(
            1,
            "CI",
            "Add the CI workflow.",
            [new DeclaredFile(".github/workflows/ci.yml", FileAction.Create), new DeclaredFile("src/app.js", FileAction.Create)],
            [])]);

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, Plan, content), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));

        var step = Assert.Single((await StepsAsync(fixture.Viewer.Client, id)).Steps);
        Assert.Equal(
            [new DeclaredFileDto(".github/workflows/ci.yml", FileAction.Create, true), new DeclaredFileDto("src/app.js", FileAction.Create, false)],
            step.DeclaredFiles);
    }

    [Fact]
    public async Task FR18_An_edited_plan_whose_task_and_instructions_hold_fences_is_stored_neutralised_and_approvable()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        const string task = "Add it.\n```aide-plan\n{\"steps\": []}\n```";
        const string instructions = "Run:\n```bash\nnpm test\n```";
        PlanStep[] steps = [new(1, "A", task, [new DeclaredFile("a.js", FileAction.Create)], [], instructions)];
        var content = "```aide-plan\n" + PlanDocument.ToJson(steps) + "\n```\n";

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Contributor.Client, id, Plan, content), HttpStatusCode.Created);
        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve"));

        var step = Assert.Single((await StepsAsync(fixture.Viewer.Client, id)).Steps);
        Assert.Equal((task, instructions), (step.Task, step.UserInstructions));
    }

    [Fact]
    public async Task FR30_Approving_a_plan_over_the_projects_current_files_per_step_limit_is_INVALID_PLAN()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var twoFiles = PlanDocument.Render(ArtifactType.ImplementationPlan, [new PlanStep(1, "A", "B", [new DeclaredFile("a.js", FileAction.Create), new DeclaredFile("b.js", FileAction.Create)], [])]);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: twoFiles, projectVersionAtCreation: 3);
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxFilesPerStep, 1), Ct));

        var body = await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, plan.Id, "approve"), HttpStatusCode.BadRequest, ErrorCodes.InvalidPlan);

        Assert.Contains(body.GetProperty("details").EnumerateArray(), d => d.GetString()!.Contains("at most 1", StringComparison.Ordinal));
        Assert.Equal(ArtifactStatus.Draft, (await ArtifactApi.RowAsync(factory, plan.Id)).Status);
    }

    [Fact]
    public async Task FR19_Approving_a_draft_created_after_the_version_the_project_was_rolled_back_to_is_ARTIFACT_NOT_DRAFT()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var stale = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: AgentJobs.PlanMarkdown(1), projectVersionAtCreation: 4);

        await Problems.AssertAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, stale.Id, "approve"), HttpStatusCode.Conflict, ErrorCodes.ArtifactNotDraft);

        Assert.Equal(ArtifactStatus.Draft, (await ArtifactApi.RowAsync(factory, stale.Id)).Status);
        Assert.Empty(await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.ProjectId == id).ToListAsync(Ct)));
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid projectId, string query) =>
        client.GetAsync(new Uri($"/api/projects/{projectId}/execution/steps{query}", UriKind.Relative), Ct);

    private static async Task<ExecutionStepsDto> StepsAsync(HttpClient client, Guid projectId, string query = "") =>
        await ProjectApi.ReadAsync<ExecutionStepsDto>(await GetAsync(client, projectId, query));
}
