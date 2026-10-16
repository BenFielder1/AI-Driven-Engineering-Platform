using System.Collections.Concurrent;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// Runs a project's queued agent jobs through their registered handlers, like the worker would (claim, handle, record the
/// outcome), but only for that project — the shared test database holds other tests' queued jobs.
/// </summary>
public static class AgentJobs
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs every queued job of the project (including jobs those jobs queue), oldest first; returns the job rows.</summary>
    public static async Task<List<Job>> RunQueuedAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(app);
        // Notification jobs (FR-47 outbox) are left queued: notification tests run them explicitly.
        var types = app.Services.GetServices<JobHandlerRegistration>().Select(r => r.JobType).Where(t => t != JobTypes.Notification).ToArray();
        // A job that deferred itself (FR-70: waiting for the codebase summary) stays queued and isn't run again here.
        var deferred = new HashSet<Guid>();
        for (var round = 0; round < 25; round++)
        {
            var queued = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == projectId && j.Status == JobStatus.Queued && types.Contains(j.Type))
                .OrderBy(j => j.CreatedAt)
                .ToListAsync(Ct));
            queued.RemoveAll(j => deferred.Contains(j.Id));
            if (queued.Count == 0)
            {
                break;
            }

            foreach (var job in queued)
            {
                if (await RunCoreAsync(app, job))
                {
                    deferred.Add(job.Id);
                }
            }
        }

        return await LifecycleSeed.JobsAsync(app, projectId);
    }

    /// <summary>Delivers <paramref name="job"/> to its handler (again, for a redelivery) and records the outcome.</summary>
    public static Task RunAsync(WebApplicationFactory<Program> app, Job job) => RunCoreAsync(app, job);

    /// <summary>
    /// As <see cref="RunAsync"/>; <c>true</c> when the handler deferred the job (<see cref="JobDeferredException"/>), which
    /// is then queued again at its run-after time without counting the attempt, like the worker does.
    /// </summary>
    public static async Task<bool> RunCoreAsync(WebApplicationFactory<Program> app, Job job)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(job);
        var attempt = job.Attempts + 1;
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running).SetProperty(j => j.Attempts, attempt), Ct));

        var status = JobStatus.Succeeded;
        string? error = null;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var registration = scope.ServiceProvider.GetServices<JobHandlerRegistration>().Single(r => r.JobType == job.Type);
            try
            {
                await registration.Factory(scope.ServiceProvider)
                    .HandleAsync(new JobExecution(job.Id, job.Type, job.Payload, job.ProjectId, attempt), Ct);
            }
            catch (JobDeferredException ex)
            {
                await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.Attempts, attempt - 1).SetProperty(j => j.RunAfter, ex.RunAfter).SetProperty(j => j.LastError, ex.Message), Ct));
                return true;
            }
            catch (JobFailedException ex)
            {
                status = ex.Transient ? JobStatus.Dead : JobStatus.Failed;
                error = ex.ErrorCode is null ? ex.Message : $"{ex.ErrorCode}: {ex.Message}";
            }
        }

        if (status != JobStatus.Succeeded)
        {
            await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status).SetProperty(j => j.LastError, error).SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow), Ct));
            return false;
        }

        // Like the worker: mark it succeeded and run the succeeded hooks (e.g. the next execution job) in one transaction.
        await using var hookScope = app.Services.CreateAsyncScope();
        var hookDb = hookScope.ServiceProvider.GetRequiredService<AideDbContext>();
        await using var transaction = await hookDb.Database.BeginTransactionAsync(Ct);
        await hookDb.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Succeeded).SetProperty(j => j.LastError, (string?)null).SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow), Ct);
        foreach (var hook in hookScope.ServiceProvider.GetServices<IJobSucceededHook>())
        {
            await hook.OnJobSucceededAsync(hookDb, new JobExecution(job.Id, job.Type, job.Payload, job.ProjectId, attempt), Ct);
        }

        await hookDb.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return false;
    }

    /// <summary>The factory with <paramref name="responders"/> replacing the built-in Fake replies of their agents.</summary>
    public static WebApplicationFactory<Program> WithResponders(WebApplicationFactory<Program> factory, params IFakeAgentResponder[] responders) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            foreach (var responder in responders)
            {
                services.AddSingleton(responder);
            }
        }));

    public static Task<List<AgentRun>> RunsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.CreatedAt).ToListAsync(Ct));

    public static Task<List<Artifact>> ArtifactsAsync(WebApplicationFactory<Program> app, Guid projectId, ArtifactType type) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().Where(a => a.ProjectId == projectId && a.Type == type).OrderBy(a => a.Version).ToListAsync(Ct));

    /// <summary>A valid plan document (FR-29 storage format) with <paramref name="count"/> chained steps.</summary>
    public static string PlanMarkdown(int count, ArtifactType type = ArtifactType.ImplementationPlan) =>
        PlanDocument.Render(type, [.. Enumerable.Range(1, count).Select(n => new PlanStep(
            n,
            $"Step {n}",
            $"Build part {n}.",
            [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
            n == 1 ? [] : [n - 1]))]);

    /// <summary>The plan generator's JSON reply for <paramref name="dependencies"/> (step n depends on dependencies[n-1]).</summary>
    public static string PlanReply(params int[][] dependencies) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            steps = dependencies.Select((deps, i) => new
            {
                number = i + 1,
                title = $"Step {i + 1}",
                task = $"Build part {i + 1}.",
                files = new[] { new { path = $"src/part{i + 1}.js", action = "create" } },
                dependencies = deps,
            }),
        });
}

/// <summary>A Fake responder that answers with the given replies in turn (the last one repeats) and records each request.</summary>
public sealed class ScriptedResponder(AgentKind agent, params string[] replies) : IFakeAgentResponder
{
    private readonly ConcurrentQueue<LlmRequest> _requests = new();

    public AgentKind Agent => agent;

    public IReadOnlyList<LlmRequest> Requests => [.. _requests];

    /// <summary>The user message of each request.</summary>
    public IReadOnlyList<string> Prompts => [.. _requests.Select(r => r.Messages[0].Content)];

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Enqueue(request);
        return replies[Math.Min(_requests.Count, replies.Length) - 1];
    }
}

/// <summary>Wraps a responder and counts the provider calls it answers.</summary>
public sealed class CountingResponder(IFakeAgentResponder inner) : IFakeAgentResponder
{
    private int _calls;

    public AgentKind Agent => inner.Agent;

    public int Calls => _calls;

    public string Respond(LlmRequest request)
    {
        Interlocked.Increment(ref _calls);
        return inner.Respond(request);
    }
}
