using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// Seeds lifecycle state directly through the DbContext (the artifact, execution and review endpoints arrive in later
/// phases), and reads back what the lifecycle wrote.
/// </summary>
public static class LifecycleSeed
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> AdvanceAsync(HttpClient client, Guid projectId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync($"/api/projects/{projectId}/phase/advance", new { reason }, Ct);
    }

    public static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid projectId, Guid gateId, string action, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync($"/api/projects/{projectId}/gates/{gateId}/{action}", body ?? new { }, Ct);
    }

    /// <summary>Puts the project at a phase/version/mode (status follows phase unless given).</summary>
    public static Task SetProjectAsync(
        WebApplicationFactory<Program> factory, Guid projectId, Phase phase, int version, ApprovalMode mode, ProjectStatus? status = null, int reviewCycle = 0) =>
        ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(
            s => s.SetProperty(p => p.CurrentPhase, phase)
                .SetProperty(p => p.ProjectVersion, version)
                .SetProperty(p => p.ApprovalMode, mode)
                .SetProperty(p => p.Status, status ?? PhaseOrder.StatusFor(phase))
                .SetProperty(p => p.ReviewCycle, reviewCycle),
            Ct));

    public static Task<Artifact> AddArtifactAsync(
        WebApplicationFactory<Program> factory,
        Guid projectId,
        ArtifactType type,
        ArtifactStatus status = ArtifactStatus.Locked,
        int version = 1,
        int projectVersionAtCreation = 1,
        DateTimeOffset? createdAt = null) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var content = $"# {type} v{version}";
            var artifact = new Artifact
            {
                ProjectId = projectId,
                Type = type,
                Version = version,
                Content = content,
                ContentHash = ContentHash.Compute(content),
                Status = status,
                ProjectVersionAtCreation = projectVersionAtCreation,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            };
            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync(Ct);
            return artifact;
        });

    public static Task<IReadOnlyList<ExecutionStep>> AddStepsAsync(
        WebApplicationFactory<Program> factory, Guid projectId, Guid planId, int count, StepStatus status = StepStatus.Pending) =>
        ProjectApi.WithDbAsync<IReadOnlyList<ExecutionStep>>(factory, async db =>
        {
            var steps = Enumerable.Range(1, count).Select(n => new ExecutionStep
            {
                ProjectId = projectId,
                PlanArtifactId = planId,
                StepNumber = n,
                Title = $"Step {n}",
                Task = "Do it",
                DeclaredFiles = [new DeclaredFile($"src/file{n}.cs", FileAction.Create)],
                Status = status,
            }).ToList();
            db.ExecutionSteps.AddRange(steps);
            await db.SaveChangesAsync(Ct);
            return steps;
        });

    public static Task PrepareWorkingCopyAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(g => g.ProjectId == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.BranchName, $"aide/test-{projectId:N}"), Ct));

    public static Task AddTransitionAsync(WebApplicationFactory<Program> factory, Guid projectId, Phase from, Phase to, int version, DateTimeOffset createdAt) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            db.StateTransitions.Add(new StateTransition
            {
                ProjectId = projectId,
                FromPhase = from,
                ToPhase = to,
                Actor = "seed",
                Reason = "seed",
                ProjectVersion = version,
                CreatedAt = createdAt,
            });
            await db.SaveChangesAsync(Ct);
        });

    /// <summary>A review agent run and a compliance report for <paramref name="projectVersion"/>.</summary>
    public static Task<ComplianceReport> AddReportAsync(
        WebApplicationFactory<Program> factory,
        Guid projectId,
        int projectVersion,
        ReviewDecision decision,
        EscalationReason? reason = null,
        DateTimeOffset? createdAt = null) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var at = createdAt ?? DateTimeOffset.UtcNow;
            var run = new AgentRun
            {
                ProjectId = projectId,
                Agent = AgentKind.CodeReview,
                Phase = Phase.Review,
                Provider = LlmProvider.Fake,
                Model = "fake-1",
                PromptVersion = "code-review@1",
                Status = AgentRunStatus.Succeeded,
                CreatedAt = at,
            };
            var report = new ComplianceReport
            {
                ProjectId = projectId,
                ReviewCycle = 1,
                ProjectVersion = projectVersion,
                Score = decision == ReviewDecision.ReleaseEligible ? 0.950m : 0.700m,
                DriftLevel = DriftLevel.Low,
                Decision = decision,
                EscalationReason = reason,
                AgentRunId = run.Id,
                CreatedAt = at,
            };
            db.AgentRuns.Add(run);
            db.ComplianceReports.Add(report);
            await db.SaveChangesAsync(Ct);
            return report;
        });

    public static Task<ApprovalGate> AddGateAsync(
        WebApplicationFactory<Program> factory, Guid projectId, GateType type, Guid? artifactId = null, DateTimeOffset? createdAt = null) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var gate = new ApprovalGate { ProjectId = projectId, Type = type, ArtifactId = artifactId, CreatedAt = createdAt ?? DateTimeOffset.UtcNow };
            db.ApprovalGates.Add(gate);
            await db.SaveChangesAsync(Ct);
            return gate;
        });

    public static Task<Project> ProjectAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId, Ct));

    public static Task<List<Job>> JobsAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == projectId).OrderBy(j => j.CreatedAt).ToListAsync(Ct));

    public static Task<List<AuditEvent>> EventsAsync(WebApplicationFactory<Program> factory, Guid projectId, string type) =>
        ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == type).OrderBy(e => e.CreatedAt).ToListAsync(Ct));

    public static Task<List<StateTransition>> TransitionsAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == projectId).OrderBy(t => t.CreatedAt).ToListAsync(Ct));

    public static Task<List<ApprovalGate>> GatesAsync(WebApplicationFactory<Program> factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.ApprovalGates.AsNoTracking()
            .Where(g => g.ProjectId == projectId).OrderBy(g => g.CreatedAt).ToListAsync(Ct));

    public static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
