using Aide.Api.Features.Stack;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Analysis;

/// <summary>
/// What profile rules need from a version's base commit (FR-180, FR-183): its files (<c>null</c> when the commit can't be
/// read, so existence isn't checked), the detector's result there (§4.18, when asked for), and the repository's VCS option (R5).
/// </summary>
public sealed record StackProfileBase(IReadOnlySet<string>? Files, StackDetection? Detection, string? RepoVcs);

/// <summary>Reads <see cref="StackProfileBase"/> from the project's working copy (fetching a hosted commit first if needed).</summary>
public sealed partial class StackProfileBaseReader(
    AideDbContext db,
    IWorkingCopies workingCopies,
    Agents.AgentRepoContext repoContext,
    ProjectStackService stacks,
    ILogger<StackProfileBaseReader> logger)
{
    /// <summary>The base of <paramref name="version"/>; with <paramref name="detect"/>, the detector runs over its manifests.</summary>
    public async Task<StackProfileBase> ReadAsync(ReleaseVersion version, bool detect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        var repoVcs = await stacks.RepoVcsAsync(version.ProjectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return new StackProfileBase(null, null, repoVcs);
        }

        try
        {
            var paths = await workingCopies.ListPathsAtCommitAsync(version.ProjectId, version.BaseCommitSha, VersionStackLoader.MaxBaseFiles, cancellationToken);
            if (paths is null && await repoContext.FetchAsync(version.ProjectId, cancellationToken))
            {
                paths = await workingCopies.ListPathsAtCommitAsync(version.ProjectId, version.BaseCommitSha, VersionStackLoader.MaxBaseFiles, cancellationToken);
            }

            if (paths is null || paths.Count >= VersionStackLoader.MaxBaseFiles)
            {
                return new StackProfileBase(null, null, repoVcs);
            }

            var files = paths.Select(PathRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!detect)
            {
                return new StackProfileBase(files, null, repoVcs);
            }

            var (byPath, _) = RepoFiles.ByNormalisedPath(paths.Select(p => (p, p)), StackDetectionService.MaxFiles);
            var wanted = StackDetector.ManifestsToRead(byPath.Keys).Where(byPath.ContainsKey).ToList();
            var read = await workingCopies.ReadTextAtCommitAsync(version.ProjectId, version.BaseCommitSha, [.. wanted.Select(w => byPath[w])], StackDetector.MaxManifestLength, cancellationToken);
            var manifests = wanted.Where(w => read.ContainsKey(byPath[w])).ToDictionary(w => w, w => read[byPath[w]], StringComparer.Ordinal);
            var detection = StackDetector.Detect(byPath.Keys, manifests, await VcsHostAsync(version.ProjectId, cancellationToken));
            return new StackProfileBase(files, detection, repoVcs);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogBaseUnreadable(logger, version.Id, ex.GetType().Name);
            return new StackProfileBase(null, null, repoVcs);
        }
    }

    /// <summary>The detector's VCS host for the project's repository (Local: GitHub, R5's default).</summary>
    private async Task<string> VcsHostAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var provider = await db.ProjectGitSettings.AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .Select(g => (GitProvider?)g.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        return provider switch
        {
            GitProvider.Gitlab => StackDetectionService.GitLabVcsHost,
            GitProvider.Bitbucket => StackDetectionService.BitbucketVcsHost,
            _ => StackDetectionService.VcsHost,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The base commit of release version {ReleaseVersionId} could not be read for the stack profile ({ErrorType}); paths aren't checked against it")]
    private static partial void LogBaseUnreadable(ILogger logger, Guid releaseVersionId, string errorType);
}
