using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Pipelines;

/// <summary>
/// FR-14, FR-278, §4.51: the SDLC's run settings. <see cref="SdlcPack.ReviewCycles"/> is the project's
/// <c>maxReviewCycles + extraReviewCycles</c>, never below its current review cycle + 1 (<see cref="SdlcReviewLoop"/>,
/// D-0.7.0-30), so the engine's loop cap never refuses a cycle 0.6.1 allows.
/// </summary>
public sealed class SdlcRunSettingsProvider(TevrinDbContext db) : IRunSettingsProvider
{
    public string PipelineId => SdlcPack.PipelineId;

    public async Task<int?> GetSettingAsync(Guid projectId, string name, CancellationToken cancellationToken)
    {
        if (name != SdlcPack.ReviewCycles)
        {
            return null;
        }

        var cycles = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.MaxReviewCycles, p.ExtraReviewCycles, p.ReviewCycle })
            .SingleOrDefaultAsync(cancellationToken);
        return cycles is null ? null : SdlcReviewLoop.ReviewCycles(cycles.MaxReviewCycles, cycles.ExtraReviewCycles, cycles.ReviewCycle);
    }
}

/// <summary>
/// FR-186, §4.27, §4.51: a new SDLC run starts in <c>analysis</c> when the version infers its stack
/// (<see cref="LifecycleSnapshot.InfersStack"/> in the <see cref="SdlcLifecycleFacts"/>), else in <c>refinement</c>.
/// A context without the fact is a programming error, as for the guards. <c>STACK_INFER_NO_CODE</c>, <c>NOT_RELEASED</c>
/// and <c>VERSION_IN_PROGRESS</c> stay in the SDLC service.
/// </summary>
public sealed class SdlcRunStartSelector : IRunStartSelector
{
    public string PipelineId => SdlcPack.PipelineId;

    public Task<string> SelectStartAsync(RunStartContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var facts = context.Facts.Get<SdlcLifecycleFacts>()
            ?? throw new InvalidOperationException($"The SDLC start selector needs {nameof(SdlcLifecycleFacts)} in the run start context's facts.");
        var stage = SdlcGuards.StartStage(facts.Snapshot.InfersStack);
        return context.StartStages.Contains(stage, StringComparer.Ordinal)
            ? Task.FromResult(stage)
            : throw new InvalidOperationException($"Start stage '{stage}' is not a start stage of pipeline '{context.PipelineId}'.");
    }
}
