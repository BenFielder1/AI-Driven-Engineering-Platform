using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>A prepared working copy: the provider, its connection settings, the project branch and the decrypted token.</summary>
public sealed record PreparedWorkingCopy(IGitProvider Provider, GitConnectionSettings Connection, string Branch, string? Token);

/// <summary>
/// The outcome of <see cref="WorkingCopyPreparer.TryPrepareAsync"/>: the prepared working copy, or the error and whether a
/// retry could help (a network error) or not (no Git settings, an unreadable token, a bad repository URL, bad credentials,
/// a missing repository or default branch).
/// </summary>
public sealed record WorkingCopyPreparation(Result<PreparedWorkingCopy> Result, bool Transient);

/// <summary>
/// FR-35: prepares a project's working copy and project branch (<c>aide/&lt;slug&gt;-&lt;short-id&gt;</c>) through its Git
/// provider, before the project enters execution (§4.1 "Git working copy prepared") and before every step. Idempotent: an
/// existing repository and branch are reused, and the branch name, once recorded in the Git settings, never changes (the
/// branch is not rewritten by a rollback, §4.8). Git work happens outside any database transaction; only the branch name
/// is written, the first time.
/// </summary>
public sealed partial class WorkingCopyPreparer(
    AideDbContext db,
    IGitProviderResolver providers,
    ISecretProtector secrets,
    ILogger<WorkingCopyPreparer> logger)
{
    public async Task<Result<PreparedWorkingCopy>> PrepareAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await TryPrepareAsync(projectId, cancellationToken)).Result;

    /// <summary>As <see cref="PrepareAsync"/>, carrying the provider's verdict on whether a failure is transient.</summary>
    public async Task<WorkingCopyPreparation> TryPrepareAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.Slug })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return Failure("The project was not found.", transient: false, ErrorCodes.NotFound);
        }

        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null)
        {
            return Failure("The project has no Git settings. Save them in the project settings first.", transient: false);
        }

        string? token = null;
        if (settings.TokenEncrypted is { } encrypted && !secrets.TryUnprotect(encrypted, out token))
        {
            return Failure("The saved Git token can't be decrypted. An Owner needs to save it again.", transient: false);
        }

        var branch = settings.BranchName ?? ExecutionRules.BranchName(project.Slug, project.Id);
        var connection = new GitConnectionSettings(project.Id, settings.RepoUrl, settings.DefaultBranch);
        var provider = providers.Resolve(settings.Provider);
        var outcome = await provider.PrepareWorkingCopyAsync(connection, branch, token, cancellationToken);
        if (!outcome.Ok)
        {
            LogPrepareFailed(logger, projectId, settings.Provider);
            return Failure($"The Git working copy could not be prepared: {outcome.Reason}", outcome.Transient);
        }

        if (settings.BranchName is null)
        {
            await db.ProjectGitSettings
                .Where(s => s.ProjectId == projectId && s.BranchName == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.BranchName, branch), cancellationToken);
            branch = await db.ProjectGitSettings.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.BranchName).FirstAsync(cancellationToken) ?? branch;
            LogPrepared(logger, projectId, branch);
        }

        return new WorkingCopyPreparation(Result.Success(new PreparedWorkingCopy(provider, connection, branch, token)), Transient: false);
    }

    private static WorkingCopyPreparation Failure(string message, bool transient, string code = ErrorCodes.PhasePreconditionFailed) =>
        new(Result.Failure<PreparedWorkingCopy>(new DomainError(code, message) { Details = [message] }), transient);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prepared the working copy of project {ProjectId} on branch {Branch}")]
    private static partial void LogPrepared(ILogger logger, Guid projectId, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing the working copy of project {ProjectId} ({Provider}) failed")]
    private static partial void LogPrepareFailed(ILogger logger, Guid projectId, GitProvider provider);
}
