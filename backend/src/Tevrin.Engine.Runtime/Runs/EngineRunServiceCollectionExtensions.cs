using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>FR-331–FR-335: DI registration of the run driver and run control.</summary>
public static class EngineRunServiceCollectionExtensions
{
    /// <summary>
    /// The run driver (<see cref="EngineRunDriver"/>, the handler of <c>stage.run</c>), <see cref="PipelineRunStarter"/> and
    /// <see cref="EngineRunControl"/> and (FR-352) <see cref="ProjectArtifacts"/> (scoped), and the triggers (schedules with the <c>trigger.fire</c> handler, webhooks). Needs the job queue, agent stages and stage handlers.
    /// </summary>
    public static IServiceCollection AddEngineRunDriver(this IServiceCollection services)
    {
        services.TryAddScoped<EngineRunDriver>();
        services.TryAddScoped<PipelineRunStarter>();
        services.TryAddScoped<EngineRunControl>();
        services.TryAddScoped<ProjectArtifacts>();
        services.TryAddScoped<AgentFeedbackEntries>();
        services.AddJobHandler<StageRunJobHandler>(EngineJobTypes.StageRun);

        // FR-359: a stage.run job that ends without succeeding closes the destructive calls it saved.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobEndedHook, ToolApprovalJobEndedHook>());

        // FR-336, FR-337: schedule and webhook triggers (the host registers Triggers.IProjectApprovalModes).
        services.TryAddScoped<Triggers.PipelineSchedules>();
        services.TryAddScoped<Triggers.PipelineWebhooks>();
        services.AddJobHandler<Triggers.TriggerFireJobHandler>(EngineJobTypes.TriggerFire);
        return services;
    }
}
