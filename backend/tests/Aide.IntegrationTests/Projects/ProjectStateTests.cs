using System.Net;
using Aide.Api.Features.Projects;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Projects;

/// <summary>§6 <c>GET /api/projects/:id/state</c>: deterministic state computed from the database.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectStateTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S6_State_of_a_new_project_is_refinement_with_nothing_produced()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);

        var state = await GetStateAsync(owner.Client, project.Id);

        Assert.Equal((project.Id, Phase.Refinement, ProjectStatus.Active, 1, false), (state.ProjectId, state.Phase, state.Status, state.Checkpoint, state.ExecutionPaused));
        Assert.Equal(ArtifactRules.All, state.Artifacts.Select(a => a.Type));
        Assert.All(state.Artifacts, a => Assert.Equal((null, null, null), (a.LatestVersion, a.LatestStatus, a.CurrentLockedVersion)));
        Assert.Equal(new ExecutionCountsDto(0, 0, 0, 0, 0, 0), state.Execution);
        Assert.Equal(new ReviewStateDto(0, 3, null, null, null, ReviewRunStatus.Idle), state.Review);
        Assert.Empty(state.PendingGates);
    }

    [Fact]
    public async Task S6_State_summarises_artifacts_steps_review_and_pending_gates()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await SeedWorkflowRowsAsync(factory, fixture.Project.Id);

        var state = await GetStateAsync(fixture.Viewer.Client, fixture.Project.Id);

        Assert.Equal(4, state.Checkpoint);
        Assert.Equal(
            [
                new ArtifactStateDto(ArtifactType.ProjectRequest, 2, ArtifactStatus.Draft, 1),
                new ArtifactStateDto(ArtifactType.TechnicalSpecification, 1, ArtifactStatus.Locked, 1),
                new ArtifactStateDto(ArtifactType.ImplementationPlan, 2, ArtifactStatus.Locked, 2),
                new ArtifactStateDto(ArtifactType.OptimizationPlan, null, null, null),
            ],
            state.Artifacts);

        // Steps of the current (locked v2) plan only; v1's invalidated steps don't count.
        Assert.Equal(new ExecutionCountsDto(4, 1, 0, 2, 0, 1), state.Execution);

        // The v5 report was undone by a rollback to v4, so the v3 report is the latest that counts.
        Assert.Equal(new ReviewStateDto(1, 4, 0.820m, ReviewDecision.ReviewRequired, DriftLevel.Low, ReviewRunStatus.Idle), state.Review);

        Assert.Equal([GateType.SpecApproval, GateType.PreRelease], state.PendingGates.Select(g => g.Type));
        Assert.True(state.PendingGates[0].Overdue);
        Assert.False(state.PendingGates[1].Overdue);
    }

    [Fact]
    public async Task S6_State_is_404_for_non_members()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    private static async Task<ProjectStateDto> GetStateAsync(HttpClient client, Guid projectId) =>
        await ProjectApi.ReadAsync<ProjectStateDto>(await client.GetAsync(new Uri($"/api/projects/{projectId}/state", UriKind.Relative), Ct));

    /// <summary>
    /// Workflow rows for a project at version 4, review cycle 1 (+1 extra): project_request v1 locked + v2 draft,
    /// spec v1 locked, plan v1 invalidated (3 invalidated steps) and v2 locked (2 completed, 1 pending, 1 blocked),
    /// an agent run, compliance reports at v3 (0.82) and v5 (stale), a pending spec gate created 80 h ago, a pending
    /// pre-release gate and a decided escalation gate. Includes the NO ACTION references between project rows.
    /// </summary>
    internal static Task SeedWorkflowRowsAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Checkpoint, 4).SetProperty(p => p.ReviewCycle, 1).SetProperty(p => p.ExtraReviewCycles, 1),
                Ct);

            Artifact NewArtifact(ArtifactType type, int version, ArtifactStatus status, TimeSpan age, Guid? runId = null) => new()
            {
                ProjectId = projectId,
                Type = type,
                Version = version,
                Content = $"# {type} v{version}",
                ContentHash = new string('a', 64),
                Status = status,
                CheckpointAtCreation = 1,
                AgentRunId = runId,
                CreatedAt = now - age,
            };

            ExecutionStep NewStep(Artifact plan, int number, StepStatus status) => new()
            {
                ProjectId = projectId,
                PlanArtifactId = plan.Id,
                StepNumber = number,
                Title = $"Step {number}",
                Task = "Do it",
                DeclaredFiles = [new DeclaredFile($"src/file{number}.cs", FileAction.Create)],
                Status = status,
            };

            var planV1 = NewArtifact(ArtifactType.ImplementationPlan, 1, ArtifactStatus.Invalidated, TimeSpan.FromHours(3));
            var planV2 = NewArtifact(ArtifactType.ImplementationPlan, 2, ArtifactStatus.Locked, TimeSpan.FromHours(2));
            var steps = new[]
            {
                NewStep(planV1, 1, StepStatus.Invalidated),
                NewStep(planV1, 2, StepStatus.Invalidated),
                NewStep(planV1, 3, StepStatus.Invalidated),
                NewStep(planV2, 1, StepStatus.Completed),
                NewStep(planV2, 2, StepStatus.Completed),
                NewStep(planV2, 3, StepStatus.Pending),
                NewStep(planV2, 4, StepStatus.Blocked),
            };
            var run = new AgentRun
            {
                ProjectId = projectId,
                Agent = AgentKind.CodeGenerator,
                Phase = Phase.Execution,
                StepId = steps[3].Id,
                Provider = LlmProvider.Fake,
                Model = "fake-1",
                PromptVersion = "code-generator@1",
                Status = AgentRunStatus.Succeeded,
                CreatedAt = now,
            };
            var requestV1 = NewArtifact(ArtifactType.ProjectRequest, 1, ArtifactStatus.Locked, TimeSpan.FromHours(5));
            var requestV2 = NewArtifact(ArtifactType.ProjectRequest, 2, ArtifactStatus.Draft, TimeSpan.FromHours(1), run.Id);
            var specV1 = NewArtifact(ArtifactType.TechnicalSpecification, 1, ArtifactStatus.Locked, TimeSpan.FromHours(4));

            db.Artifacts.AddRange(planV1, planV2, requestV1, requestV2, specV1);
            db.ExecutionSteps.AddRange(steps);
            db.AgentRuns.Add(run);
            db.ComplianceReports.AddRange(
                new ComplianceReport
                {
                    ProjectId = projectId,
                    ReviewCycle = 1,
                    Checkpoint = 3,
                    Score = 0.820m,
                    DriftLevel = DriftLevel.Low,
                    Decision = ReviewDecision.ReviewRequired,
                    AgentRunId = run.Id,
                    CreatedAt = now - TimeSpan.FromMinutes(30),
                },
                new ComplianceReport
                {
                    ProjectId = projectId,
                    ReviewCycle = 2,
                    Checkpoint = 5,
                    Score = 0.990m,
                    DriftLevel = DriftLevel.None,
                    Decision = ReviewDecision.ReleaseEligible,
                    AgentRunId = run.Id,
                    CreatedAt = now - TimeSpan.FromMinutes(10),
                });
            db.ApprovalGates.AddRange(
                new ApprovalGate { ProjectId = projectId, Type = GateType.SpecApproval, ArtifactId = specV1.Id, CreatedAt = now - TimeSpan.FromHours(80) },
                new ApprovalGate { ProjectId = projectId, Type = GateType.PreRelease, CreatedAt = now - TimeSpan.FromMinutes(5) },
                new ApprovalGate
                {
                    ProjectId = projectId,
                    Type = GateType.Escalation,
                    ArtifactId = planV2.Id,
                    Status = GateStatus.Approved,
                    Decision = EscalationDecision.Continue,
                    CreatedAt = now - TimeSpan.FromHours(1),
                });
            await db.SaveChangesAsync(Ct);
        });
}
