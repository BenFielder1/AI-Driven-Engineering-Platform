using Aide.Api.Features.Git;
using Aide.Api.Features.Notifications;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Releases;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseEntity = Aide.Domain.Entities.Release;

namespace Aide.Api.Features.Release;

/// <summary>
/// FR-64, §4.14: after the tag exists, the GitHub Release for it (<c>POST /repos/{o}/{r}/releases</c>, name = the version)
/// with AIDE's notes (<see cref="ReleaseNotes"/>): the version, the spec's "Changes in this version", the plan step titles
/// with commit links, the review score and a link back to AIDE (<c>&lt;App:PublicBaseUrl&gt;/projects/&lt;id&gt;/release</c>).
/// Idempotent: a release that already exists for the tag (<c>GET /releases/tags/{tag}</c>) is reused. Local projects have
/// none. GitHub failing (5xx, unreachable) retries the job (the tag is reused then); GitHub refusing (e.g. no permission) is
/// logged and the release completes without a GitHub Release; a rate limit waits for the reset.
/// </summary>
public sealed partial class GitHubReleasePublisher(
    AideDbContext db,
    GitHubRepoAccess access,
    GitHubApi api,
    IOptions<AppOptions> app,
    ILogger<GitHubReleasePublisher> logger)
{
    /// <summary>The GitHub Release URL, or <c>null</c> (Local, or GitHub refused).</summary>
    public async Task<string?> PublishAsync(ReleaseEntity release, string tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var target = await access.ResolveAsync(release.ProjectId, cancellationToken);
        if (target.IsFailure)
        {
            LogNotPublished(logger, release.ProjectId, tag, target.Error!.Message);
            return null;
        }

        if (target.Value is not { } github)
        {
            return null;
        }

        try
        {
            if (await api.FindReleaseUrlAsync(github.Token, github.Owner, github.Repo, tag, cancellationToken) is { } existing)
            {
                LogReused(logger, release.ProjectId, tag);
                return existing;
            }

            var input = await NotesInputAsync(release, tag, github.RepoWebUrl, cancellationToken);
            var url = await api.CreateReleaseAsync(
                github.Token,
                github.Owner,
                github.Repo,
                tag,
                ReleaseNotes.Name(input.Version),
                ReleaseNotes.Build(input),
                SemVer.TryParse(input.Version, out var version) && version.IsPrerelease,
                cancellationToken);
            LogPublished(logger, release.ProjectId, tag);
            return url;
        }
        catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Upstream)
        {
            throw new JobFailedException($"Creating the GitHub Release for {tag} failed: {ex.Message}", transient: true, ErrorCodes.UpstreamError);
        }
        catch (GitHubApiException ex)
        {
            LogNotPublished(logger, release.ProjectId, tag, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// FR-140: AIDE's release notes for a provider that creates its release together with the tag (GitLab: the Release's
    /// description); <c>null</c> for GitHub (published here after tagging) and Local.
    /// </summary>
    public async Task<string?> HostNotesAsync(ReleaseEntity release, Domain.Entities.GitProvider provider, string? repoWebUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (provider != Domain.Entities.GitProvider.Gitlab)
        {
            return null;
        }

        return ReleaseNotes.Build(await NotesInputAsync(release, release.Tag, repoWebUrl, cancellationToken));
    }

    /// <summary>The notes' inputs from the version: its locked spec, its completed steps (all plans, in order) and latest review.</summary>
    internal async Task<ReleaseNotesInput> NotesInputAsync(ReleaseEntity release, string tag, string? repoWebUrl, CancellationToken cancellationToken)
    {
        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == release.ProjectId).Select(p => p.Name).FirstAsync(cancellationToken);
        var version = await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == release.ReleaseVersionId).Select(v => v.Version).FirstOrDefaultAsync(cancellationToken)
            ?? tag;
        var spec = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == release.ProjectId && a.ReleaseVersionId == release.ReleaseVersionId
                && a.Type == ArtifactType.TechnicalSpecification && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => a.Content)
            .FirstOrDefaultAsync(cancellationToken);
        var plans = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == release.ProjectId && a.ReleaseVersionId == release.ReleaseVersionId
                && (a.Type == ArtifactType.ImplementationPlan || a.Type == ArtifactType.OptimizationPlan))
            .Select(a => new { a.Id, a.CreatedAt })
            .ToListAsync(cancellationToken);
        var planOrder = plans.OrderBy(p => p.CreatedAt).Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ProjectId == release.ProjectId && s.ReleaseVersionId == release.ReleaseVersionId && s.Status == StepStatus.Completed)
            .Select(s => new { s.PlanArtifactId, s.StepNumber, s.Title, s.CommitSha })
            .ToListAsync(cancellationToken);
        var ordered = steps
            .OrderBy(s => planOrder.TryGetValue(s.PlanArtifactId, out var order) ? order : int.MaxValue)
            .ThenBy(s => s.StepNumber)
            .Select((s, i) => new ReleaseNoteStep(i + 1, s.Title, s.CommitSha))
            .ToList();
        var review = await db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == release.ProjectId && r.ReleaseVersionId == release.ReleaseVersionId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Score, r.ReviewCycle })
            .FirstOrDefaultAsync(cancellationToken);
        return new ReleaseNotesInput(
            projectName,
            version,
            tag,
            spec is null ? null : SpecSections.Extract(spec, SpecSections.ChangesInThisVersion),
            ordered,
            repoWebUrl,
            review?.Score,
            review?.ReviewCycle,
            app.Value.Absolute($"/projects/{release.ProjectId:D}/release"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the GitHub Release {Tag} of project {ProjectId}")]
    private static partial void LogPublished(ILogger logger, Guid projectId, string tag);

    [LoggerMessage(Level = LogLevel.Information, Message = "The GitHub Release {Tag} of project {ProjectId} already exists; reusing it")]
    private static partial void LogReused(ILogger logger, Guid projectId, string tag);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No GitHub Release was created for {Tag} of project {ProjectId}: {Reason}")]
    private static partial void LogNotPublished(ILogger logger, Guid projectId, string tag, string reason);
}
