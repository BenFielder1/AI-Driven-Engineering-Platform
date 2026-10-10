using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>
/// The project PR once recorded (FR-36): provider, base (default) and head (project) branch, and for GitHub its number and
/// web URL (both <c>null</c> for Local, whose "PR" is the recorded branch comparison). <c>openedAt</c> is when AIDE last
/// opened or reused it.
/// </summary>
public sealed record PullRequestSummaryDto(GitProvider Provider, string BaseBranch, string HeadBranch, int? Number, string? Url, DateTimeOffset OpenedAt);

/// <summary>How a file differs between the default branch and the project branch (lowercase on the wire, like every enum).</summary>
public enum PullRequestFileStatus
{
    [JsonStringEnumMemberName("added")]
    Added = 0,

    [JsonStringEnumMemberName("modified")]
    Modified = 1,

    [JsonStringEnumMemberName("deleted")]
    Deleted = 2,

    [JsonStringEnumMemberName("renamed")]
    Renamed = 3,

    [JsonStringEnumMemberName("copied")]
    Copied = 4,

    [JsonStringEnumMemberName("changed")]
    Changed = 5,
}

/// <summary>A commit on the project branch that isn't on the default branch; <c>url</c> links it on GitHub (<c>null</c> for Local).</summary>
public sealed record PullRequestCommitDto(string Sha, string Subject, string? Url);

/// <summary>A file changed on the project branch since it left the default branch.</summary>
public sealed record PullRequestFileDto(string Path, PullRequestFileStatus Status);

/// <summary>
/// The project PR with its branch comparison (FR-35, FR-36), read live from the working copy: the commits (newest first, at
/// most 250) and changed files (at most 1000; <c>truncated</c> when either list was cut). <c>comparisonAvailable</c> is
/// <c>false</c> when the working copy or a branch is missing (then both lists are empty).
/// </summary>
public sealed record PullRequestDto(
    GitProvider Provider,
    string BaseBranch,
    string HeadBranch,
    int? Number,
    string? Url,
    DateTimeOffset OpenedAt,
    bool ComparisonAvailable,
    string? BaseSha,
    string? HeadSha,
    IReadOnlyList<PullRequestCommitDto> Commits,
    IReadOnlyList<PullRequestFileDto> Files,
    bool Truncated);

/// <summary>Reads the recorded project PR (FR-36) and compares its branches in the working copy.</summary>
public sealed partial class PullRequestReader(
    AideDbContext db,
    IGitProviderResolver providers,
    IWorkingCopies workingCopies,
    ILogger<PullRequestReader> logger)
{
    public const int MaxCommits = 250;
    public const int MaxFiles = 1000;

    /// <summary>The recorded PR, or <c>null</c> before one was opened (or when the project has no Git settings).</summary>
    public static async Task<PullRequestSummaryDto?> SummaryAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var opened = await PullRequestQueries.LatestOpenedAsync(db, projectId, cancellationToken);
        if (opened is null)
        {
            return null;
        }

        var git = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(g => g.ProjectId == projectId, cancellationToken);
        if (git is null)
        {
            return null;
        }

        var (baseBranch, headBranch) = Branches(opened.Payload);
        var head = headBranch ?? git.BranchName;
        return head is null
            ? null
            : new PullRequestSummaryDto(git.Provider, baseBranch ?? git.DefaultBranch, head, git.PullRequestNumber, git.PullRequestUrl, opened.CreatedAt);
    }

    public async Task<PullRequestDto?> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (await SummaryAsync(db, projectId, cancellationToken) is not { } summary)
        {
            return null;
        }

        var git = await db.ProjectGitSettings.AsNoTracking().FirstAsync(g => g.ProjectId == projectId, cancellationToken);
        var connection = new GitConnectionSettings(projectId, git.RepoUrl, summary.BaseBranch);
        BranchComparison? comparison = null;
        try
        {
            var baseRef = providers.Resolve(summary.Provider).ComparisonBaseRef(connection);
            comparison = await workingCopies.CompareAsync(projectId, baseRef, summary.HeadBranch, MaxCommits, MaxFiles, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or NotSupportedException)
        {
            LogCompareFailed(logger, projectId, ex.GetType().Name);
        }

        return new PullRequestDto(
            summary.Provider,
            summary.BaseBranch,
            summary.HeadBranch,
            summary.Number,
            summary.Url,
            summary.OpenedAt,
            comparison is not null,
            comparison?.BaseSha,
            comparison?.HeadSha,
            [.. (comparison?.Commits ?? []).Select(c => new PullRequestCommitDto(c.Sha, c.Subject, ExecutionQueries.CommitUrl(git.Provider, git.RepoUrl, c.Sha)))],
            [.. (comparison?.Files ?? []).Select(f => new PullRequestFileDto(f.Path, Status(f.Status)))],
            comparison?.Truncated ?? false);
    }

    private static PullRequestFileStatus Status(string status) => status switch
    {
        "added" => PullRequestFileStatus.Added,
        "modified" => PullRequestFileStatus.Modified,
        "deleted" => PullRequestFileStatus.Deleted,
        "renamed" => PullRequestFileStatus.Renamed,
        "copied" => PullRequestFileStatus.Copied,
        _ => PullRequestFileStatus.Changed,
    };

    private static (string? Base, string? Head) Branches(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            string? Get(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("baseBranch"), Get("headBranch"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comparing the branches of project {ProjectId} failed ({ErrorType})")]
    private static partial void LogCompareFailed(ILogger logger, Guid projectId, string errorType);
}

/// <summary>FR-35, FR-36: the project's Git branch and pull request (§6 additions under <c>/api/projects/:id/git</c>).</summary>
public static class GitEndpoints
{
    public static IEndpointRouteBuilder MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/git")
            .WithTags("Git")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/pull-request", GetPullRequest)
            .WithName("GetPullRequest")
            .WithSummary("The project pull request (members, FR-36): provider, base and head branch, GitHub number and URL (null for Local), and the branch comparison read from the working copy — commits (newest first, at most 250) and changed files (at most 1000). For a Local project the comparison is the \"PR\". 404 NOT_FOUND until AIDE has opened it (when every step of the plan is completed).")
            .Produces<PullRequestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseDescriptions((StatusCodes.Status404NotFound, "NOT_FOUND: the project, or no pull request has been opened yet."));

        group.MapPost("/pull-request", RetryPullRequest)
            .WithName("RetryPullRequest")
            .WithSummary("Open the project pull request again (Contributor or Owner, FR-36), e.g. after the pull request job failed or its retries ran out: queues the pull request job. Allowed in execution once every step of the current plan is completed, or in review, while no pull request is recorded for the current plan. In review the job only records the PR; in execution it then advances to review. Returns 202; the outcome arrives as PULL_REQUEST_OPENED or PULL_REQUEST_FAILED on the live channel.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN: Viewers can't open the pull request."),
                (StatusCodes.Status404NotFound, "NOT_FOUND: the project."),
                (StatusCodes.Status409Conflict, "PHASE_PRECONDITION_FAILED (not in execution or review, steps not all completed, or the PR for the current plan is already recorded) or CONFLICT (a pull request job is already queued or running, or a concurrent change)."));

        return app;
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> RetryPullRequest(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        PullRequestJobs pullRequests,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ControlExecution, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return ApiProblems.NotFound("The project was not found.");
        }

        var now = time.GetUtcNow();
        var requested = await pullRequests.RequestAsync(project, PullRequestJobPayload.Retried, now, cancellationToken);
        if (requested.IsFailure)
        {
            return ApiProblems.FromError(requested.Error!);
        }

        // xmin: two concurrent requests (or a rollback in between) can't both queue a job.
        project.UpdatedAt = now;
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        return saved.IsSuccess
            ? TypedResults.Accepted($"/api/projects/{id}/git/pull-request")
            : ApiProblems.FromError(saved.Error!);
    }

    private static async Task<Results<Ok<PullRequestDto>, ProblemHttpResult>> GetPullRequest(
        Guid id,
        IProjectAccess access,
        PullRequestReader reader,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var pullRequest = await reader.GetAsync(id, cancellationToken);
        return pullRequest is null
            ? ApiProblems.NotFound("No pull request has been opened for this project yet.")
            : TypedResults.Ok(pullRequest);
    }
}
