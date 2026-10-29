using System.Text.Json;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Releases;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>Payload of the <c>merge_readiness</c> job: the project and the pre-release gate that queued it.</summary>
public sealed record MergeReadinessJobPayload(Guid ProjectId, Guid GateId);

/// <summary>
/// FR-84: merge readiness is checked when the pre-release gate is created (queued with the gate, in its transaction), and
/// stored for the gate and the Release page. A gate decided or cancelled meanwhile, or a project outside review/release,
/// is skipped. GitHub failing is logged, not retried (readiness can be refreshed on demand); a rate limit waits.
/// </summary>
public sealed partial class MergeReadinessJobHandler(AideDbContext db, MergeReadinessService readiness, ILogger<MergeReadinessJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        MergeReadinessJobPayload payload;
        try
        {
            payload = AideJson.Deserialize<MergeReadinessJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            throw new JobFailedException($"The merge readiness job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }

        var pending = await db.ApprovalGates.AsNoTracking()
            .AnyAsync(g => g.Id == payload.GateId && g.ProjectId == payload.ProjectId && g.Status == GateStatus.Pending, cancellationToken);
        var phase = await db.Projects.AsNoTracking().Where(p => p.Id == payload.ProjectId).Select(p => (Domain.Lifecycle.Phase?)p.CurrentPhase).FirstOrDefaultAsync(cancellationToken);
        if (!pending || phase is not { } current || MergeReadinessRules.CanCheck(current).IsFailure)
        {
            LogSkipped(logger, payload.ProjectId);
            return;
        }

        var checkedNow = await readiness.CheckAsync(payload.ProjectId, cancellationToken);
        if (checkedNow.IsFailure)
        {
            LogFailed(logger, payload.ProjectId, checkedNow.Error!.Code);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Merge readiness job of project {ProjectId} did nothing: the gate isn't pending or the project left review")]
    private static partial void LogSkipped(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merge readiness of project {ProjectId} couldn't be checked ({ErrorCode}); it can be refreshed on demand")]
    private static partial void LogFailed(ILogger logger, Guid projectId, string errorCode);
}

public static class MergeReadinessServiceCollectionExtensions
{
    /// <summary>FR-84: GitHub access per project, merge readiness and its gate-time job.</summary>
    public static IServiceCollection AddMergeReadiness(this IServiceCollection services)
    {
        services.AddScoped<GitHubRepoAccess>();
        services.AddScoped<GitHostTargets>();
        services.AddScoped<MergeReadinessService>();
        services.AddSingleton<MergeReadinessThrottle>();
        services.AddJobHandler<MergeReadinessJobHandler>(JobTypes.MergeReadiness);
        return services;
    }
}
