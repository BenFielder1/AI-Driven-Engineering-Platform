using Aide.Domain.Lifecycle;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Pipelines;

/// <summary>
/// FR-278, FR-290: the pipeline registry with the SDLC pack, its guards (composed from <c>Aide.Domain</c> rules,
/// D-0.7.0-23), its run settings, its start selector and invalidation hook, and the lifecycle service through which every
/// lifecycle decision runs on the engine (<see cref="SdlcLifecycle"/>, FR-292). The registry is validated and health-checked
/// (<see cref="PipelineDefinitionsHealthCheck"/>).
/// </summary>
public static class SdlcPipelineRegistration
{
    public static IServiceCollection AddSdlcPipeline(this IServiceCollection services)
    {
        services.AddPipelineRegistry();
        services.AddPipelinePack<SdlcPack>();

        services.AddPipelineGuard<StartOnlyGuard>(SdlcGuardNames.StartOnly);
        services.AddPipelineGuard<LeavingAnalysisGuard>(SdlcGuardNames.LeavingAnalysis);
        services.AddPipelineGuard<PlanHasStepsGuard>(SdlcGuardNames.PlanHasSteps);
        services.AddPipelineGuard<WorkingCopyPreparedGuard>(SdlcGuardNames.WorkingCopyPrepared);
        services.AddPipelineGuard<PlanStepsCompletedGuard>(SdlcGuardNames.PlanStepsCompleted);
        services.AddPipelineGuard<ReleaseEligibleGuard>(SdlcGuardNames.ReleaseEligible);
        services.AddPipelineGuard<ReviewCycleReadyGuard>(SdlcGuardNames.ReviewCycleReady);
        services.AddPipelineGuard<Agents.SdlcPlanValidGuard>(SdlcGuardNames.PlanValid);

        services.AddScoped<IRunSettingsProvider, SdlcRunSettingsProvider>();
        services.AddScoped<IRunStartSelector, SdlcRunStartSelector>();
        services.AddSingleton<IInvalidationHook>(SdlcInvalidationHook.Instance);

        // FR-292: every lifecycle decision runs on the engine with the SDLC definition.
        services.AddLogging();
        // FR-277: moves resolve the run's pinned definition; stored definitions are parsed once per hash (§8).
        services.TryAddSingleton<PipelineDefinitionCache>();
        services.AddSingleton<SdlcLifecycle>();
        return services;
    }
}
