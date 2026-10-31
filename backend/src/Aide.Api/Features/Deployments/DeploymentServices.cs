using Aide.Api.Jobs;
using Aide.Domain.Jobs;

namespace Aide.Api.Features.Deployments;

public static class DeploymentServiceCollectionExtensions
{
    /// <summary>FR-87–FR-91, §4.15: deployment settings, env sync, scheduling and the <c>deployment</c> poll job.</summary>
    public static IServiceCollection AddDeployments(this IServiceCollection services)
    {
        // §9: the Fake provider only where allowed (fails startup elsewhere; warns at every boot when opted in).
        services.AddHostedService<FakeDeploymentProviderGuard>();
        services.AddScoped<DeploymentAccess>();
        services.AddScoped<DeploymentEnvSync>();
        services.AddScoped<DeploymentSettingsService>();
        services.AddScoped<DeploymentInventoryService>();
        services.AddScoped<DeploymentTargetService>();
        services.AddScoped<IJobEndedHook, DeploymentSetupJobEndedHook>();
        services.AddJobHandler<DeploymentSetupJobHandler>(JobTypes.DeploymentSetup);
        services.AddScoped<DeploymentScheduler>();
        services.AddScoped<RolloutScheduler>();
        services.AddScoped<RolloutReader>();

        // FR-152, FR-153: managed databases, their provisioning job and the migration runs.
        services.AddScoped<Databases.ManagedDatabaseSecrets>();
        services.AddScoped<Databases.DatabaseSeparation>();
        services.AddScoped<Databases.ManagedDatabaseService>();
        services.AddScoped<Databases.MigrationRunReader>();
        services.AddScoped<Databases.MigrationRunService>();
        services.AddScoped<IJobEndedHook, Databases.DatabaseProvisioningJobEndedHook>();
        services.AddJobHandler<Databases.DatabaseProvisioningJobHandler>(JobTypes.DatabaseProvisioning);
        services.AddScoped<Databases.DatabaseExpirySweepService>();
        services.AddJobHandler<Databases.DatabaseExpirySweepJobHandler>(JobTypes.DatabaseExpirySweep);
        services.AddScoped<IJobEndedHook, DeploymentJobEndedHook>();
        services.AddJobHandler<DeploymentJobHandler>(JobTypes.Deployment);

        // FR-147, FR-148, FR-149: preview and production rollouts and the preview database reset.
        services.AddJobHandler<RolloutJobHandler>(JobTypes.PreviewRollout);
        services.AddJobHandler<RolloutJobHandler>(JobTypes.ProductionRollout);
        services.AddScoped<Databases.PreviewDatabaseResetService>();
        services.AddJobHandler<Databases.PreviewDatabaseResetJobHandler>(JobTypes.PreviewDatabaseReset);
        return services;
    }
}
