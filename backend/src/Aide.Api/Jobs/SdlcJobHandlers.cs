using Aide.Api.Agents;
using Aide.Api.Features.BuildChecks;
using Aide.Api.Features.Databases;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Git;
using Aide.Api.Features.Notifications;
using Aide.Api.Features.Release;
using Aide.Api.Features.Review;
using Aide.Api.Features.Trackers;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Jobs;

/// <summary>
/// FR-272: the SDLC's job handlers, one for every <see cref="SdlcJobTypes"/> value (the 0.6.1 job type strings), in one
/// place so the set the SDLC enqueues and the set it registers can't drift apart. Only registered types can be enqueued
/// (<see cref="JobEnqueuer"/>) and claimed. <c>execution</c> is one per project (§4.9).
/// </summary>
public static class SdlcJobHandlers
{
    public static IServiceCollection AddSdlcJobHandlers(this IServiceCollection services)
    {
        // Agents (FR-27–FR-30, FR-70, FR-180, FR-256) and execution (FR-31–FR-37).
        services.AddJobHandler<IdeaRefinementJobHandler>(SdlcJobTypes.IdeaRefinement);
        services.AddJobHandler<SpecGenerationJobHandler>(SdlcJobTypes.SpecGeneration);
        services.AddJobHandler<PlanGenerationJobHandler>(SdlcJobTypes.PlanGeneration);
        services.AddJobHandler<ExecutionJobHandler>(SdlcJobTypes.Execution, onePerProject: true);
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
        return services;
    }
}
