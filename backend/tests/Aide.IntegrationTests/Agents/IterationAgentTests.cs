using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Review;
using Aide.Api.Features.Versions;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Domain.Specs;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Agents;

/// <summary>
/// FR-72, FR-75, §4.13 and FR-76 (covers/agent) with the Fake provider and Local Git: iteration inputs of refinement, spec
/// validation (first version and iteration) with its corrective re-run, plan coverage warnings and step fields, and review
/// regressions.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class IterationAgentTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Todo API\n\n## Summary\nA todo list API.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A valid first-version spec (the Fake's), optionally with a requirement line that has no id.</summary>
    private static string FirstSpec(bool withUnnumbered = false)
    {
        var spec = new FakeSpecGeneratorResponder().Respond(new LlmRequest
        {
            Model = "fake",
            System = "s",
            Messages = [new LlmMessage(LlmRole.User, $"<project_request>\n{ProjectRequest}\n</project_request>\n\n<spec_mode>\nfirst_version\n</spec_mode>")],
        });
        return withUnnumbered
            ? spec.Replace("## 3. Technical Architecture", "- Export todos as CSV.\n\n## 3. Technical Architecture", StringComparison.Ordinal)
            : spec;
    }

    // ---------- FR-72 ----------

    [Fact]
    public async Task FR72_Given_an_iteration_request_when_refinement_runs_then_its_inputs_include_the_codebase_summary_and_the_previous_spec_and_request()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        var baselineRequest = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        var baselineSpec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, content: FirstSpec());
        var head = await ReleaseOnWorkingCopyAsync(id);

        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/versions", new { version = "1.2.0", request = "Add CSV export" }, Ct), HttpStatusCode.Created);
        Assert.Equal(head, started.ReleaseVersion.BaseCommitSha);
        await AgentJobs.RunQueuedAsync(factory, id);
        var summary = Assert.Single(await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.CodebaseSummary));
        Assert.Equal(started.ReleaseVersion.Id, summary.ReleaseVersionId);

        await fixture.Contributor.Client.PostAsJsonAsync($"/api/projects/{id}/refinement/run", new { }, Ct);
        await AgentJobs.RunQueuedAsync(factory, id);

        var run = Assert.Single(await AgentJobs.RunsAsync(factory, id), r => r.Agent == AgentKind.IdeaRefinement);
        Assert.Equal(
            [
                new InputArtifactRef(summary.Id, ArtifactType.CodebaseSummary, summary.Version),
                new InputArtifactRef(baselineRequest.Id, ArtifactType.ProjectRequest, 1),
                new InputArtifactRef(baselineSpec.Id, ArtifactType.TechnicalSpecification, 1),
            ],
            run.InputArtifacts);
        Assert.NotEqual("[]", run.ToolCalls); // FR-71: the repo tools at the base commit
        var draft = (await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.ProjectRequest)).Last();
        Assert.Equal((started.ReleaseVersion.Id, ArtifactStatus.Draft), (draft.ReleaseVersionId, draft.Status));
    }

    // ---------- FR-75, §4.13 spec validation ----------

    [Fact]
    public async Task FR75_A_spec_with_a_requirement_without_an_id_is_re_run_once_with_MISSING_REQUIREMENT_IDS_feedback()
    {
        var responder = new ScriptedResponder(AgentKind.SpecGenerator, FirstSpec(withUnnumbered: true), FirstSpec());
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, responder.Requests.Count);
        Assert.Contains("MISSING_REQUIREMENT_IDS", responder.Prompts[1], StringComparison.Ordinal);
        Assert.Contains("Export todos as CSV.", responder.Prompts[1], StringComparison.Ordinal);
        Assert.Contains("<spec_mode>\nfirst_version\n</spec_mode>", responder.Prompts[0], StringComparison.Ordinal);
        var spec = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification));
        Assert.Equal((Guid?)AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey), spec.AgentRunId);
    }

    [Fact]
    public async Task FR75_A_spec_still_missing_an_id_after_the_re_run_fails_with_MISSING_REQUIREMENT_IDS_and_no_draft()
    {
        var responder = new ScriptedResponder(AgentKind.SpecGenerator, FirstSpec(withUnnumbered: true));
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith("MISSING_REQUIREMENT_IDS: ", job.LastError, StringComparison.Ordinal);
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], (await AgentJobs.RunsAsync(app, id)).Select(r => r.Status));
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification));
    }

    [Fact]
    public async Task S4_13_A_new_version_spec_is_validated_against_the_baseline_and_an_untagged_one_gets_the_corrective_re_run()
    {
        // First the untagged first-version spec (INVALID… for an iteration), then the Fake's cumulative tagged spec.
        var fake = new FakeSpecGeneratorResponder();
        var responder = new SequenceResponder(AgentKind.SpecGenerator, _ => FirstSpec(), fake.Respond);
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        var baseline = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: FirstSpec());
        await ReleaseOnWorkingCopyAsync(id, app);
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/versions", new { version = "1.2.0", request = "Add CSV export" }, Ct), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, id);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, version: 2, content: ProjectRequest + "\n\nCSV export.");
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id), j => j.Type == JobTypes.SpecGeneration);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("<spec_mode>\nnew_version\n</spec_mode>", responder.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("MISSING_SECTIONS", responder.Prompts[1], StringComparison.Ordinal); // "Changes in this version"
        Assert.Contains("FR-1 has no tag", responder.Prompts[1], StringComparison.Ordinal); // and every tag problem, in one re-run
        Assert.Contains("Changes in this version", responder.Prompts[1], StringComparison.Ordinal);
        var run = Assert.Single(await AgentJobs.RunsAsync(app, id), r => r.Agent == AgentKind.SpecGenerator && r.Id == job.Id);
        Assert.Contains(new InputArtifactRef(baseline.Id, ArtifactType.TechnicalSpecification, 1), run.InputArtifacts);
        Assert.Contains(run.InputArtifacts, a => a.Type == ArtifactType.CodebaseSummary);
        var spec = (await AgentJobs.ArtifactsAsync(app, id, ArtifactType.TechnicalSpecification)).Last();
        Assert.True(SpecValidator.Validate(spec.Content, SpecValidationContext.NewVersion(RequirementParser.Ids(baseline.Content))).IsValid);
        Assert.Equal(["FR-5"], RequirementParser.Parse(spec.Content).NewOrChangedIds);
    }

    // ---------- §4.13 plan scope, FR-76 ----------

    [Fact]
    public async Task S4_13_A_plan_leaving_a_new_requirement_uncovered_shows_UNCOVERED_REQUIREMENTS_and_its_steps_keep_agent_and_covers()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var spec = "## 2. Functional Requirements\n- **FR-1** [unchanged] List. Given todos, when listed, then shown.\n- **FR-2** [new] Export. Given todos, when exported, then a CSV is returned.\n- **FR-3** [changed] Filter. Given todos, when filtered, then matching ones are shown.";
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, content: spec);
        var plan = PlanDocument.Render(ArtifactType.ImplementationPlan,
        [
            new PlanStep(1, "Export", "Add CSV export (FR-2).", [new DeclaredFile("src/export.js", FileAction.Create)], [], Covers: ["FR-2"]),
        ]);
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft, content: plan, projectVersionAtCreation: 3);

        var dto = await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/artifacts/implementation_plan/latest", UriKind.Relative), Ct));

        var warning = Assert.Single(dto.Warnings);
        Assert.Equal(ErrorCodes.UncoveredRequirements, warning.Code);
        Assert.Equal(["FR-3"], warning.Ids);
        var spec2 = await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/artifacts/technical_specification/latest", UriKind.Relative), Ct));
        Assert.Empty(spec2.Warnings);

        // Approving the plan creates its steps with the agent (generalist) and the requirements they cover.
        await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/artifacts/{draft.Id}/approve", new { }, Ct));
        var step = Assert.Single(await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id).ToListAsync(Ct)));
        Assert.Equal(PlanAgents.Generalist, step.AgentKey);
        Assert.Equal(["FR-2"], step.Covers);
    }

    // ---------- §4.13 review regressions ----------

    [Fact]
    public async Task S4_13_A_broken_unchanged_requirement_is_a_regression_in_the_report_and_scored_over_new_and_changed_only()
    {
        var script = FakeCodeReviewScript.Default with
        {
            ScoresByCycle = [0.95m],
            OptimizationStepsByCycle = [0],
            Regressions = [("FR-1", "Listing todos returns 500 since step 2."), ("FR-9", "Unknown requirement.")],
        };
        await using var app = AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(script));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification,
            content: "## 2. Functional Requirements\n- **FR-1** [unchanged] List todos. Given todos, when listed, then shown.\n- **FR-2** [new] Export. Given todos, when exported, then a CSV is returned.\n\n## API Contracts\nNo HTTP API.");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: AgentJobs.PlanMarkdown(1));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.Add(new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = 1,
                Title = "Step 1",
                Task = "Build part 1.",
                DeclaredFiles = [new DeclaredFile("src/part1.js", FileAction.Create)],
                Dependencies = [],
            });
            await db.SaveChangesAsync(Ct);
        });
        await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct);
        await AgentJobs.RunQueuedAsync(app, id);

        var report = Assert.Single(await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().Where(r => r.ProjectId == id).ToListAsync(Ct)));
        var dto = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/reports/{report.ReviewCycle}", UriKind.Relative), Ct));
        var regression = Assert.Single(dto.Regressions);
        Assert.Equal(("FR-1", ReviewCategory.FeatureCoverage, "Listing todos returns 500 since step 2."), (regression.RequirementId, regression.Category, regression.Description));
        Assert.Contains(report.Violations, v => v.Source == FindingSource.Agent && v.Severity == Severity.Major && v.Message.StartsWith("Regression of FR-1", StringComparison.Ordinal));
        Assert.Contains(report.DriftAreas, d => d.Area == IterationReviewScope.RegressionAreaPrefix + "FR-1" && d.Severity == Severity.Major);
        Assert.NotEqual(DriftLevel.None, report.DriftLevel);
        var reviewRun = Assert.Single(await AgentJobs.RunsAsync(app, id), r => r.Agent == AgentKind.CodeReview);
        Assert.NotEqual("[]", reviewRun.ToolCalls); // FR-71: the review reads the project branch with the repo tools
    }

    /// <summary>
    /// Prepares the Local working copy, then marks the project's only version released on its head commit (as a merged
    /// release); returns that commit.
    /// </summary>
    private async Task<string> ReleaseOnWorkingCopyAsync(Guid projectId, WebApplicationFactory<Program>? app = null)
    {
        app ??= factory;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var prepared = await scope.ServiceProvider.GetRequiredService<WorkingCopyPreparer>().PrepareAsync(projectId, Ct);
            Assert.True(prepared.IsSuccess, prepared.Error?.Message);
        }

        var head = await TestGit.RunAsync(Path.Combine(factory.WorkspacesRoot, projectId.ToString("D")), Ct, "rev-parse", "HEAD");
        await LifecycleSeed.AddTransitionAsync(app, projectId, Phase.Review, Phase.Release, 6, DateTimeOffset.UtcNow.AddSeconds(1));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == projectId, Ct);
            version.Status = ReleaseVersionStatus.Released;
            version.Tag = "v" + version.Version;
            version.MergeCommitSha = head;
            version.ReleasedAt = DateTimeOffset.UtcNow;
            var project = await db.Projects.SingleAsync(p => p.Id == projectId, Ct);
            project.CurrentPhase = Phase.Release;
            project.Status = ProjectStatus.Released;
            project.Checkpoint = 6;
            project.ReleasedVersion = version.Version;
            project.DevelopmentVersion = null;
            await db.SaveChangesAsync(Ct);
        });
        return head;
    }
}

/// <summary>A Fake responder that answers request n with the n-th function (the last repeats) and records each prompt.</summary>
public sealed class SequenceResponder(AgentKind agent, params Func<LlmRequest, string>[] replies) : IFakeAgentResponder
{
    private readonly List<string> _prompts = [];

    public AgentKind Agent => agent;

    public IReadOnlyList<string> Prompts
    {
        get
        {
            lock (_prompts)
            {
                return [.. _prompts];
            }
        }
    }

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int index;
        lock (_prompts)
        {
            _prompts.Add(request.Messages[0].Content);
            index = _prompts.Count;
        }

        return replies[Math.Min(index, replies.Length) - 1](request);
    }
}
