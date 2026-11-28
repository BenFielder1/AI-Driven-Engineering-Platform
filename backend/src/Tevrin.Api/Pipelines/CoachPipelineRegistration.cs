using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Pack.Coach;

namespace Tevrin.Api.Pipelines;

/// <summary>
/// FR-338–FR-341: the Impulse coaching pack in the host. It registers the pack (its definition, schemas and prompts) and
/// binds <see cref="CoachGuardNames.VelocityLoadRules"/> to <see cref="VelocityLoadRulesGuard"/>, which reads its inputs
/// from <see cref="CoachVelocityInputsSource"/>. Its agent stages run on the generic agent stage job with the generic
/// hooks (budget, token accounting, audit and redaction, D-0.8.0-7).
/// </summary>
public static class CoachPipelineRegistration
{
    public static IServiceCollection AddCoachPipeline(this IServiceCollection services)
    {
        services.AddPipelinePack<CoachPack>();
        services.AddScoped<IVelocityInputsSource, CoachVelocityInputsSource>();
        services.AddPipelineGuard<VelocityLoadRulesGuard>(CoachGuardNames.VelocityLoadRules);
        return services;
    }
}

/// <summary>
/// FR-340, FR-352: the Velocity guard's inputs. The <c>athlete_profile</c> is the version the run pinned at its start
/// (never a newer one); the <c>session_log</c> versions are the run's own, each version once (oldest first).
/// </summary>
public sealed class CoachVelocityInputsSource(IEngineDbContext db) : IVelocityInputsSource
{
    public async Task<VelocityInputs?> ReadAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == context.RunId && r.ProjectId == context.ProjectId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var pinned = await ProjectArtifacts.PinnedAsync(db, run, [CoachArtifacts.AthleteProfile], cancellationToken);
        if (pinned.FirstOrDefault(a => a.Type == CoachArtifacts.AthleteProfile) is not { } profile)
        {
            return null;
        }

        var logs = await db.Artifacts.AsNoTracking()
            .Where(AgentStageScope.InSubject(run))
            .Where(a => a.Type == CoachArtifacts.SessionLog && (a.Status == ArtifactStatus.Locked || a.Status == ArtifactStatus.Superseded))
            .OrderBy(a => a.Version)
            .Select(a => new { a.Id, a.Content })
            .ToListAsync(cancellationToken);
        return new VelocityInputs(profile.Content, [.. logs.DistinctBy(l => l.Id).Select(l => l.Content)]);
    }
}
