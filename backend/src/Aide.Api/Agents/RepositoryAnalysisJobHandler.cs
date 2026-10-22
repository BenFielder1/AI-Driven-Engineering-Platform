using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Versions;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Releases;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// FR-70: the Repository Analyst. Queued when a release version with existing code starts (a new version, an import's
/// first version, a scratch project on a repository that has code). It explores the repository at the version's base
/// commit with the repo tools (FR-71) and writes the <c>codebase_summary</c>, which must have the eight sections of
/// <see cref="CodebaseSummaryRules"/>: a reply missing some is re-run once with their names; still missing → the run fails
/// with <c>MISSING_SECTIONS</c> and no summary is stored (refinement then goes on without one). The summary is stored
/// <b>locked</b> (no gate) in the version, with <c>CODEBASE_SUMMARY_CREATED</c>.
/// <para>
/// Nothing runs when the version is no longer the project's version in development, has no base commit (no code), or
/// already has a summary. Redelivery is idempotent like the other agent jobs (run ids from the job id; the writer returns
/// the summary a run already stored).
/// </para>
/// </summary>
public sealed partial class RepositoryAnalysisJobHandler(
    AgentRunner runner,
    AgentDraftWriter writer,
    AgentJobInputs inputs,
    AideDbContext db,
    ILogger<RepositoryAnalysisJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        RepositoryAnalysisJobPayload payload;
        try
        {
            payload = AideJson.Deserialize<RepositoryAnalysisJobPayload>(job.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new JobFailedException($"The {job.Type} job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }

        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        var version = await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == payload.ReleaseVersionId && v.ProjectId == payload.ProjectId, cancellationToken);
        if (project is null || version is null || project.CurrentReleaseVersionId != version.Id || !ReleaseVersionRules.IsDevelopment(version.Status))
        {
            LogSkipped(logger, "the version is no longer in development");
            return;
        }

        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            LogSkipped(logger, "the version has no existing code");
            return;
        }

        var runIds = new[] { AgentRunRules.RunId(job.Id), AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey) };
        if (await db.Artifacts.AsNoTracking().AnyAsync(
                a => a.ProjectId == project.Id && a.ReleaseVersionId == version.Id && a.Type == ArtifactType.CodebaseSummary
                    && (a.AgentRunId == null || !runIds.Contains(a.AgentRunId.Value)),
                cancellationToken))
        {
            LogSkipped(logger, "the version already has a codebase summary");
            return;
        }

        var baseText = version.BaseTag is { } tag ? $"{tag} ({version.BaseCommitSha})" : $"commit {version.BaseCommitSha}";
        AgentRunRequest Request(string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.RepositoryAnalyst,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = project.Name,
                ["VERSION_REQUEST"] = version.Request,
                ["BASE"] = baseText,
                [AgentRunner.FeedbackInput] = feedback,
            },
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
        };

        var first = await runner.RunAsync(Request(null), cancellationToken);
        var content = CodebaseSummaryRules.Check(AgentOutput.UnwrapFence(first.Text));
        var produced = first;
        if (content.IsFailure)
        {
            LogRejected(logger, content.Error!.Code, content.Error.Message);
            produced = await runner.RunAsync(
                Request(CodebaseSummaryRules.CorrectiveFeedback(content.Error.Details)) with
                {
                    RunKey = AgentOutput.CorrectiveRunKey,
                    Check = text => CodebaseSummaryRules.Check(AgentOutput.UnwrapFence(text)) is { IsFailure: true } failed ? Result.Failure(failed.Error!) : Result.Success(),
                },
                cancellationToken);
            content = CodebaseSummaryRules.Check(AgentOutput.UnwrapFence(produced.Text));
            if (content.IsFailure)
            {
                throw new JobFailedException(content.Error!.Message, transient: false, content.Error.Code);
            }
        }

        var summary = await writer.AddLockedAsync(project.Id, version.Id, ArtifactType.CodebaseSummary, content.Value, produced.RunId, produced.CompletedAt, cancellationToken);
        if (summary is null)
        {
            LogSkipped(logger, "the version stopped being the project's version in development while the analyst ran");
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new JobFailedException($"The codebase summary collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }

        LogStored(logger, summary.Version, version.Version);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository analysis skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The codebase summary was rejected with {ErrorCode}; re-running once with the missing sections: {Reason}")]
    private static partial void LogRejected(ILogger logger, string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored codebase_summary v{ArtifactVersion} (locked) for version {ReleaseVersion}")]
    private static partial void LogStored(ILogger logger, int artifactVersion, string releaseVersion);
}
