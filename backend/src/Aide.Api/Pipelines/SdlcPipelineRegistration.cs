using Aide.Domain.Lifecycle;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Pipelines;

/// <summary>
/// FR-278, FR-290: the pipeline registry with the SDLC pack, its guards (composed from <c>Aide.Domain</c> rules,
/// D-0.7.0-23), its run settings and its start selector. In 0.7.0 M57 the registry is only loaded, validated and
/// health-checked (<see cref="PipelineDefinitionsHealthCheck"/>); in M58 parity mode every lifecycle decision also runs
/// through the engine (<see cref="ParityStateMachine"/>, FR-291), while the SDLC state machine still decides.
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

        services.AddScoped<IRunSettingsProvider, SdlcRunSettingsProvider>();
        services.AddScoped<IRunStartSelector, SdlcRunStartSelector>();
        services.AddSingleton<IInvalidationHook>(SdlcInvalidationHook.Instance);

        // FR-291 (M58 parity mode): every lifecycle decision runs through both machines; Throw is on in the test hosts only.
        services.AddOptions<ParityOptions>().BindConfiguration(ParityOptions.SectionName);
        services.AddSingleton<ParityStateMachine>();
        return services;
    }
}
