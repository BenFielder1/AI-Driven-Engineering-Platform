using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Features.Projects;
using Tevrin.Domain.Access;
using Tevrin.Domain.Artifacts;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Gates;
using Tevrin.Domain.Jobs;
using Tevrin.Domain.Lifecycle;
using Tevrin.IntegrationTests.Artifacts;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Characterisation;

/// <summary>A user of a characterisation scenario: snapshots show them as <c>user:&lt;role&gt;</c>.</summary>
internal sealed record SnapshotUser(Guid Id, string Role, string Email);

/// <summary>
/// FR-260: drives one project through the SDLC like a user and the worker would — the public HTTP API for every decision,
/// <see cref="AgentJobs"/> for the queued jobs (Fake LLM, Local Git unless a scenario says otherwise) — and records its
/// characterisation snapshot. The project always has the same name and idea, because the Fake agents write them into
/// the artifacts whose content hashes the snapshot keeps.
/// </summary>
internal sealed class SdlcFlow : IDisposable
{
    public const string ProjectName = "Todo API";
    public const string Idea = "Build a todo API for small teams.";

    private readonly List<KeyValuePair<string, string>> _literals = [];

    private SdlcFlow(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        App = app;
        Fixture = fixture;
    }

    public WebApplicationFactory<Program> App { get; }

    public ProjectFixture Fixture { get; }

    public Guid Id => Fixture.Project.Id;

    public string ProjectPath => Fixture.Path;

    public IEnumerable<KeyValuePair<string, string>> Literals => _literals;

    public IEnumerable<SnapshotUser> Users =>
    [
        new(Fixture.Owner.User!.Id, "owner", Fixture.Owner.User.Email),
        new(Fixture.Contributor.User!.Id, "contributor", Fixture.Contributor.User.Email),
        new(Fixture.Viewer.User!.Id, "viewer", Fixture.Viewer.User.Email),
        new(Fixture.Outsider.User!.Id, "outsider", Fixture.Outsider.User.Email),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A from-scratch Local project in <paramref name="mode"/> (wire value) with the default settings.</summary>
    public static Task<SdlcFlow> CreateAsync(TevrinApiFactory root, WebApplicationFactory<Program> app, string mode = "advisory") =>
        CreateAsync(root, app, new Dictionary<string, object?>
        {
            ["name"] = ProjectName,
            ["idea"] = Idea,
            ["approvalMode"] = mode,
            ["git"] = new { provider = "local" },
        });

    /// <summary>
    /// <c>POST /api/projects</c> with <paramref name="request"/> by a new Owner, plus a Contributor, a Viewer and an outsider
    /// (members added like <see cref="ProjectApi.FixtureAsync"/> does).
    /// </summary>
    public static async Task<SdlcFlow> CreateAsync(TevrinApiFactory root, WebApplicationFactory<Program> app, IReadOnlyDictionary<string, object?> request)
    {
        ArgumentNullException.ThrowIfNull(root);
        var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.PostAsJsonAsync("/api/projects", request, Ct), HttpStatusCode.Created);
        var contributor = await ApiSession.RegisterAsync(app);
        var viewer = await ApiSession.RegisterAsync(app);
        var outsider = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, contributor.User!.Id, ProjectRole.Contributor);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, viewer.User!.Id, ProjectRole.Viewer);
        var flow = new SdlcFlow(app, new ProjectFixture(project, owner, contributor, viewer, outsider));
        flow.AddLiteral(root.WorkspacesRoot, "<workspaces>");
        return flow;
    }

    /// <summary>A run-specific string (e.g. a temporary folder) the snapshot shows as <paramref name="replacement"/>.</summary>
    public void AddLiteral(string value, string replacement) => _literals.Add(new KeyValuePair<string, string>(value, replacement));

    public Task<string> SnapshotAsync(string scenario) => SnapshotRecorder.RecordAsync(this, scenario, Ct);

    // ---------- jobs ----------

    public Task<List<Job>> RunJobsAsync() => AgentJobs.RunQueuedAsync(App, Id);

    /// <summary>The project's only job of <paramref name="type"/> that is queued.</summary>
    public async Task<Job> QueuedJobAsync(string type)
    {
        var jobs = await ProjectApi.WithDbAsync(App, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == Id && j.Type == type && j.Status == JobStatus.Queued).ToListAsync(Ct));
        Assert.True(jobs.Count == 1, $"Expected one queued {type} job. {await DescribeAsync()}");
        return jobs[0];
    }

    /// <summary>The project's only job of <paramref name="type"/>, whatever its status.</summary>
    public async Task<Job> JobAsync(string type)
    {
        var jobs = await ProjectApi.WithDbAsync(App, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == Id && j.Type == type).ToListAsync(Ct));
        Assert.True(jobs.Count == 1, $"Expected one {type} job. {await DescribeAsync()}");
        return jobs[0];
    }

    // ---------- artifacts, gates, phases ----------

    /// <summary>FR-27: a refinement round (Contributor), run to its draft.</summary>
    public async Task RefineAsync()
    {
        await ExpectAsync(await Fixture.Contributor.Client.PostAsJsonAsync($"{ProjectPath}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        await RunJobsAsync();
    }

    public async Task<Artifact> DraftAsync(string type)
    {
        var drafts = (await AgentJobs.ArtifactsAsync(App, Id, type)).Where(a => a.Status == ArtifactStatus.Draft).ToList();
        Assert.True(drafts.Count == 1, $"Expected one {type} draft. {await DescribeAsync()}");
        return drafts[0];
    }

    /// <summary>FR-17: the Owner approves the open draft of <paramref name="type"/>.</summary>
    public async Task ApproveDraftAsync(string type) =>
        await ExpectAsync(await ArtifactApi.DecideAsync(Fixture.Owner.Client, Id, (await DraftAsync(type)).Id, "approve"), HttpStatusCode.OK);

    /// <summary>FR-17: the Owner rejects the open draft of <paramref name="type"/> with <paramref name="comment"/>.</summary>
    public async Task RejectDraftAsync(string type, string comment) =>
        await ExpectAsync(await ArtifactApi.DecideAsync(Fixture.Owner.Client, Id, (await DraftAsync(type)).Id, "reject", comment), HttpStatusCode.OK);

    /// <summary>FR-18: a Contributor saves a draft of <paramref name="type"/> (wire value).</summary>
    public async Task SaveDraftAsync(string type, string content) =>
        await ExpectAsync(await ArtifactApi.CreateDraftAsync(Fixture.Contributor.Client, Id, type, content), HttpStatusCode.Created);

    /// <summary>FR-12: a Contributor advances; <c>true</c> when a gate was created instead of the transition.</summary>
    public async Task<bool> AdvanceAsync()
    {
        var result = await ExpectAsync(await LifecycleSeed.AdvanceAsync(Fixture.Contributor.Client, Id), HttpStatusCode.OK);
        return result.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.Object;
    }

    public async Task<ApprovalGate> PendingGateAsync(string type)
    {
        var pending = (await LifecycleSeed.GatesAsync(App, Id)).Where(g => g.Status == GateStatus.Pending && g.Type == type).ToList();
        Assert.True(pending.Count == 1, $"Expected one pending {type} gate. {await DescribeAsync()}");
        return pending[0];
    }

    /// <summary>FR-13, FR-14: the Owner decides the pending gate of <paramref name="type"/>.</summary>
    public async Task DecideGateAsync(string type, string action, object? body = null) =>
        await ExpectAsync(await LifecycleSeed.DecideAsync(Fixture.Owner.Client, Id, (await PendingGateAsync(type)).Id, action, body), HttpStatusCode.OK);

    public async Task ExpectPhaseAsync(Phase phase, ProjectStatus? status = null)
    {
        var project = await LifecycleSeed.ProjectAsync(App, Id);
        Assert.True(
            project.CurrentPhase == phase && (status is null || project.Status == status),
            $"Expected {phase}{(status is null ? string.Empty : "/" + status)}. {await DescribeAsync()}");
    }

    // ---------- the SDLC, step by step ----------

    /// <summary>Refinement → Specification: a refinement round, the request approved, advance, the spec draft generated.</summary>
    public async Task ToSpecificationAsync()
    {
        await RefineAsync();
        await ApproveDraftAsync(SdlcArtifacts.ProjectRequest);
        Assert.False(await AdvanceAsync(), "Entering specification needs no gate.");
        await ExpectPhaseAsync(Phase.Specification);
        await RunJobsAsync();
    }

    /// <summary>
    /// Specification → Planning: the spec draft approved, advance, the spec_approval gate approved (strict and advisory),
    /// the plan draft generated.
    /// </summary>
    public async Task ToPlanningAsync()
    {
        await ApproveDraftAsync(SdlcArtifacts.TechnicalSpecification);
        if (await AdvanceAsync())
        {
            await DecideGateAsync(SdlcGates.SpecApproval, "approve");
        }

        await ExpectPhaseAsync(Phase.Planning);
        await RunJobsAsync();
    }

    /// <summary>Planning → Execution: the plan draft approved, advance, the plan_approval gate approved (strict).</summary>
    public async Task ToExecutionAsync()
    {
        await ApproveDraftAsync(SdlcArtifacts.ImplementationPlan);
        if (await AdvanceAsync())
        {
            await DecideGateAsync(SdlcGates.PlanApproval, "approve");
        }

        await ExpectPhaseAsync(Phase.Execution);
    }

    /// <summary>FR-35: a Contributor starts execution.</summary>
    public async Task StartExecutionAsync() =>
        await ExpectAsync(await Fixture.Contributor.Client.PostAsJsonAsync($"{ProjectPath}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

    /// <summary>Execution started and every job run: steps, pull request, build check, then review cycle 1.</summary>
    public async Task ExecuteToReviewAsync()
    {
        await StartExecutionAsync();
        await RunJobsAsync();
        await ExpectPhaseAsync(Phase.Review);
    }

    /// <summary>From creation to the first review decision.</summary>
    public async Task ToReviewAsync()
    {
        await ToSpecificationAsync();
        await ToPlanningAsync();
        await ToExecutionAsync();
        await ExecuteToReviewAsync();
    }

    /// <summary>The pending pre_release gate approved and the release job run to <c>released</c> (Local merges and tags).</summary>
    public async Task ReleaseAsync()
    {
        await DecideGateAsync(SdlcGates.PreRelease, "approve");
        await ExpectPhaseAsync(Phase.Release);
        await RunJobsAsync();
        await ExpectPhaseAsync(Phase.Release, ProjectStatus.Released);
    }

    /// <summary>FR-44: the Owner rolls back to <paramref name="checkpoint"/>.</summary>
    public async Task RollbackAsync(int checkpoint, string reason) =>
        await ExpectAsync(await Fixture.Owner.Client.PostAsJsonAsync($"{ProjectPath}/rollback", new { targetCheckpoint = checkpoint, reason }, Ct), HttpStatusCode.OK);

    /// <summary>FR-60: the Owner starts a new release version.</summary>
    public async Task StartVersionAsync(string version, string request) =>
        await ExpectAsync(await Fixture.Owner.Client.PostAsJsonAsync($"{ProjectPath}/versions", new { version, request }, Ct), HttpStatusCode.Created);

    /// <summary>FR-65: the Owner sets the merge strategy (wire value).</summary>
    public async Task SetMergeStrategyAsync(string strategy) =>
        await ExpectAsync(await ProjectApi.PatchAsync(Fixture.Owner.Client, $"{ProjectPath}/settings/git", new { mergeStrategy = strategy }), HttpStatusCode.OK);

    /// <summary>FR-20: changes the open draft's stored content behind the API's back.</summary>
    public async Task TamperDraftAsync(string type, string content) =>
        await ArtifactApi.TamperAsync(App, (await DraftAsync(type)).Id, content);

    /// <summary>A read of <paramref name="path"/> (relative to the project's artifacts) by the Viewer.</summary>
    public async Task ReadArtifactAsync(string path) =>
        await ExpectAsync(await ArtifactApi.GetAsync(Fixture.Viewer.Client, Id, path), HttpStatusCode.OK);

    // ---------- diagnostics ----------

    /// <summary>The project's position, jobs and pending gates, for assertion messages.</summary>
    public async Task<string> DescribeAsync()
    {
        var project = await LifecycleSeed.ProjectAsync(App, Id);
        var jobs = await LifecycleSeed.JobsAsync(App, Id);
        var gates = await LifecycleSeed.GatesAsync(App, Id);
        return $"Project: {project.CurrentPhase}/{project.Status} at checkpoint {project.Checkpoint}, review cycle {project.ReviewCycle}. "
            + $"Jobs: {string.Join("; ", jobs.Select(j => $"{j.Type}:{j.Status}:{j.LastError}"))}. "
            + $"Gates: {string.Join("; ", gates.Select(g => $"{g.Type}:{g.Status}"))}.";
    }

    public void Dispose() => Fixture.Dispose();

    private static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        if (text.Length == 0)
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
