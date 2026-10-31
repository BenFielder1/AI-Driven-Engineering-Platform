using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Jobs;

/// <summary>
/// Job types of the PostgreSQL work queue (§4.9, D1). Values are stored in <c>jobs.type</c>, so they never change once
/// used. A job type is only claimed by a worker once a handler is registered for it; until then its jobs stay queued.
/// </summary>
public static class JobTypes
{
    /// <summary>FR-27: one idea refinement round.</summary>
    public const string IdeaRefinement = "idea_refinement";

    /// <summary>FR-28: generate the technical specification (also re-run after a rejected spec_approval gate, D16).</summary>
    public const string SpecGeneration = "spec_generation";

    /// <summary>FR-29: generate the implementation plan (also re-run after a rejected plan_approval gate).</summary>
    public const string PlanGeneration = "plan_generation";

    /// <summary>FR-31–FR-34: run the current plan's steps. At most one queued or running per project (§4.9).</summary>
    public const string Execution = Job.ExecutionType;

    /// <summary>
    /// FR-36: when every step of the current plan is completed, push the project branch, open (or reuse) the one project PR
    /// (GitHub) or record the branch comparison (Local), then advance execution → review.
    /// </summary>
    public const string PullRequest = "pull_request";

    /// <summary>FR-38: one review cycle.</summary>
    public const string Review = "review";

    /// <summary>FR-42: merge and tag the release.</summary>
    public const string Release = "release";

    /// <summary>
    /// FR-65, §4.14: polls the version's PR of an <c>awaiting_merge</c> release attempt (every 2 minutes for an hour, then
    /// every 10 minutes, up to 14 days) by deferring itself between polls; completes the release when it is merged.
    /// </summary>
    public const string MergePoll = "merge_poll";

    /// <summary>FR-84: checks merge readiness of the version's PR when the pre-release gate is created (stored for the gate).</summary>
    public const string MergeReadiness = "merge_readiness";

    /// <summary>
    /// FR-70: the Repository Analyst summarises the codebase at a version's base commit into a locked
    /// <c>codebase_summary</c>. Queued when a version with existing code starts: a new version (FR-60), an import's first
    /// version (FR-73) and a scratch project on a repository that already has code (FR-86).
    /// </summary>
    public const string RepositoryAnalysis = "repository_analysis";

    /// <summary>
    /// FR-90, FR-91, §4.15, NFR-REL2: one deployment row: creates it at the provider (a production deployment first adopts the
    /// one Vercel made for the merge commit) and polls it — every 10 seconds for 2 minutes, then every 30, up to 30 minutes —
    /// by deferring itself between polls, so polling survives restarts.
    /// </summary>
    public const string Deployment = "deployment";

    /// <summary>
    /// FR-144: sets up one deployment target: creates or links the platform resource (with branch control and the preview
    /// resource where the platform has one), then recomputes and syncs the derived env vars. Progress is on the target.
    /// </summary>
    public const string DeploymentSetup = "deployment_setup";

    /// <summary>
    /// FR-152, §4.21: provisions or links one managed database (or follows a Supabase restore): the platform call, then a
    /// poll every 10 seconds (deferring itself) until available, within 10 minutes; then the derived env vars are
    /// recomputed and synced. Progress is on the database.
    /// </summary>
    public const string DatabaseProvisioning = "database_provisioning";

    /// <summary>
    /// FR-147: deploys the PR head to the project's persistent preview environment in dependency order — Supabase BaaS
    /// preview migrations, then the backend (its pre-deploy migrates its preview database), then the frontend — each step
    /// waiting for <c>ready</c> (the job defers itself). Its deployment rows carry <c>rollout_id</c> = this job's id.
    /// </summary>
    public const string PreviewRollout = "preview_rollout";

    /// <summary>
    /// FR-149: after the release merge, deploys every target in order (Supabase BaaS migrations, backend, frontend),
    /// adopting the platform's own deployment of the merge commit within 60 s or creating one; idempotent per
    /// <c>(release_id, component)</c>.
    /// </summary>
    public const string ProductionRollout = "production_rollout";

    /// <summary>FR-148: resets the preview database (schema reset; Supabase BaaS re-applies the base commit's migrations).</summary>
    public const string PreviewDatabaseReset = "preview_database_reset";

    /// <summary>
    /// FR-47: fan out one notification-worthy event (or a gate reminder, FR-15): in-app notifications for its recipients,
    /// one delivery row per subscribed project channel, then a first delivery attempt. Queued in the event's transaction.
    /// </summary>
    public const string Notification = "notification";

    /// <summary>The job that runs <paramref name="agent"/>.</summary>
    public static string ForAgent(AgentKind agent) => agent switch
    {
        AgentKind.IdeaRefinement => IdeaRefinement,
        AgentKind.SpecGenerator => SpecGeneration,
        AgentKind.PlanGenerator => PlanGeneration,
        AgentKind.CodeGenerator => Execution,
        AgentKind.CodeReview => Review,
        AgentKind.RepositoryAnalyst => RepositoryAnalysis,
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, null),
    };

    /// <summary>
    /// The follow-up job written in the same transaction as a forward transition into <paramref name="entered"/>:
    /// spec generation on entering specification (FR-28), plan generation on entering planning (FR-29), a review
    /// cycle on entering review (FR-38) and the release job on entering release (FR-42). Refinement is started by
    /// the user (FR-27) and execution by <c>POST /execution/start</c> (FR-35, FR-37), so they have none.
    /// </summary>
    public static string? OnEnter(Phase entered) => entered switch
    {
        Phase.Specification => SpecGeneration,
        Phase.Planning => PlanGeneration,
        Phase.Review => Review,
        Phase.Release => Release,
        _ => null,
    };
}
