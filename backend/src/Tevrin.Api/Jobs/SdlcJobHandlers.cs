using Tevrin.Api.Agents;
using Tevrin.Api.Features.BuildChecks;
using Tevrin.Api.Features.Databases;
using Tevrin.Api.Features.Deployments;
using Tevrin.Api.Features.Git;
using Tevrin.Api.Features.Notifications;
using Tevrin.Api.Features.Release;
using Tevrin.Api.Features.Review;
using Tevrin.Api.Features.Trackers;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Stages;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Jobs;

/// <summary>
/// FR-272: the SDLC's job handlers, one for every <see cref="SdlcJobTypes"/> value (the 0.6.1 job type strings), in one
/// place so the set the SDLC enqueues and the set it registers can't drift apart. Only registered types can be enqueued
/// (<see cref="JobEnqueuer"/>) and claimed. <c>execution</c> is one per project (§4.9) because <c>sdlc.pipeline.yaml</c>
/// declares it exclusive per project (FR-286).
/// </summary>
public static class SdlcJobHandlers
{
    public static IServiceCollection AddSdlcJobHandlers(this IServiceCollection services)
    {
        // Agents (FR-27–FR-30, FR-70, FR-180, FR-256) and execution (FR-31–FR-37).
        services.AddJobHandler<IdeaRefinementJobHandler>(SdlcJobTypes.IdeaRefinement);
        services.AddJobHandler<SpecGenerationJobHandler>(SdlcJobTypes.SpecGeneration);
        services.AddJobHandler<PlanGenerationJobHandler>(SdlcJobTypes.PlanGeneration);
        // One per project from the SDLC definition's concurrency block (FR-286), not a code flag.
        services.AddJobHandler<ExecutionJobHandler>(SdlcJobTypes.Execution);
        services.AddJobHandler<RepositoryAnalysisJobHandler>(SdlcJobTypes.RepositoryAnalysis);
        services.AddJobHandler<StackAnalysisJobHandler>(SdlcJobTypes.StackAnalysis);

        // Pull request, build check, review and release (FR-36, FR-38, FR-42, FR-65, FR-84, FR-254–FR-256).
        services.AddJobHandler<PullRequestJobHandler>(SdlcJobTypes.PullRequest);
        services.AddJobHandler<BuildCheckJobHandler>(SdlcJobTypes.BuildCheck);
        services.AddJobHandler<BuildFixJobHandler>(SdlcJobTypes.BuildFix);
        services.AddJobHandler<ReviewJobHandler>(SdlcJobTypes.Review);
        services.AddJobHandler<MergeReadinessJobHandler>(SdlcJobTypes.MergeReadiness);
        services.AddJobHandler<MergePollJobHandler>(SdlcJobTypes.MergePoll);
        services.AddJobHandler<ReleaseJobHandler>(SdlcJobTypes.Release);

        // Trackers (FR-170) and notifications (FR-47).
        services.AddJobHandler<TrackerImportJobHandler>(SdlcJobTypes.TrackerImport);
        services.AddJobHandler<NotificationJobHandler>(SdlcJobTypes.Notification);

        // Deployments and managed databases (FR-90, FR-91, FR-144, FR-147–FR-149, FR-152, FR-155).
        services.AddJobHandler<DeploymentSetupJobHandler>(SdlcJobTypes.DeploymentSetup);
        services.AddJobHandler<DeploymentJobHandler>(SdlcJobTypes.Deployment);
        services.AddJobHandler<RolloutJobHandler>(SdlcJobTypes.PreviewRollout);
        services.AddJobHandler<RolloutJobHandler>(SdlcJobTypes.ProductionRollout);
        services.AddJobHandler<DatabaseProvisioningJobHandler>(SdlcJobTypes.DatabaseProvisioning);
        services.AddJobHandler<DatabaseExpirySweepJobHandler>(SdlcJobTypes.DatabaseExpirySweep);
        services.AddJobHandler<PreviewDatabaseResetJobHandler>(SdlcJobTypes.PreviewDatabaseReset);

        // FR-319, D-0.7.0-43: the stage work the execution and build check jobs run through the engine.
        services.AddStageHandlers();
        services.AddScoped<ISdlcRunPins, SdlcRunPins>();
        services.AddScoped<SdlcStageHandlerJob>();
        services.AddPipelineStageHandler<SdlcExecutionStageHandler>(SdlcStageHandlerNames.Execution);
        services.AddPipelineStageHandler<SdlcBuildCheckStageHandler>(SdlcStageHandlerNames.BuildCheck);
        return services;
    }
}
