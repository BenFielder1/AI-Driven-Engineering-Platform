using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>A GitLab or Bitbucket project's provider and saved connection settings (§4.19): what readiness, CI and the merge poll call.</summary>
public sealed record GitHostTarget(IGitProvider Provider, GitConnectionSettings Connection);

/// <summary>
/// FR-138–FR-140: resolves the §4.19 host provider of a project whose Git work goes through the binding Owner's
/// connection (GitLab, Bitbucket). <c>null</c> for GitHub and Local projects, which keep their v2 paths.
/// </summary>
public sealed class GitHostTargets(AideDbContext db, IGitProviderResolver providers)
{
    public async Task<GitHostTarget?> ResolveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        return settings is not null && Domain.Projects.ProjectSettingsRules.IsGitHost(settings.Provider)
            ? new GitHostTarget(providers.Resolve(settings.Provider), GitConnectionSettings.From(settings))
            : null;
    }
}
