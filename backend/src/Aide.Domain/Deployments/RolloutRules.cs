using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>A step of a preview or production rollout (FR-147, FR-149), in dependency order. Wire values: <c>migrations</c>, <c>backend</c>, <c>frontend</c>.</summary>
public enum RolloutStepKind
{
    /// <summary>Supabase BaaS migrations applied by AIDE's runner (§4.21).</summary>
    Migrations = 0,

    Backend = 1,

    Frontend = 2,
}

/// <summary>The state of a rollout step. Wire values: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>skipped</c>.</summary>
public enum RolloutStepState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>A rollout's overall state. Wire values: <c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>blocked</c>, <c>canceled</c>.</summary>
public enum RolloutState
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,

    /// <summary>FR-148: the preview database diverged; the rollout waits for a reset (<c>PREVIEW_DATABASE_DIVERGED</c>).</summary>
    Blocked = 3,

    /// <summary>A newer preview rollout replaced it.</summary>
    Canceled = 4,
}

/// <summary>A step in a rollout plan: what it deploys, and whether (and why) it is skipped.</summary>
public sealed record RolloutStepPlan(RolloutStepKind Kind, bool Skipped, string? Note);

/// <summary>A deployment target as the rollout plan sees it: component, platform, and whether it can deploy (resource and connection).</summary>
public sealed record RolloutTarget(DeploymentComponent Component, DeploymentProvider Platform, bool Configured);

/// <summary>What the rollout does next: start a step, wait for the running one, or stop (failed or completed).</summary>
public enum RolloutDecisionKind
{
    Start = 0,
    Wait = 1,
    Failed = 2,
    Completed = 3,
}

public sealed record RolloutDecision(RolloutDecisionKind Kind, RolloutStepKind? Step);

/// <summary>
/// FR-147, FR-149, §4.21: rollout planning and sequencing, pure.
/// <list type="bullet">
/// <item>Order: Supabase BaaS migrations → backend → frontend. Each step waits for the previous one to succeed; a failure
/// stops the later steps (a backend whose pre-deploy migration failed never lets the frontend start).</item>
/// <item>A component without a configured target isn't in the plan. In preview, a host without previews (GitHub Pages) is
/// skipped with a note, and review doesn't wait for it.</item>
/// </list>
/// </summary>
public static class RolloutRules
{
    /// <summary>How often a rollout looks at its running step.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>FR-147: how long review waits for the whole preview rollout (the v2 ceiling).</summary>
    public static readonly TimeSpan ReviewWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>A rollout that hasn't finished after this long fails (each deployment has its own 30-minute ceiling).</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(95);

    /// <summary>The steps of a rollout of <paramref name="environment"/>, in order.</summary>
    public static IReadOnlyList<RolloutStepPlan> Plan(DeploymentEnvironment environment, bool baasMigrations, IReadOnlyList<RolloutTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        List<RolloutStepPlan> steps = [];
        if (baasMigrations)
        {
            steps.Add(new RolloutStepPlan(RolloutStepKind.Migrations, false, null));
        }

        foreach (var (component, kind) in new[] { (DeploymentComponent.Backend, RolloutStepKind.Backend), (DeploymentComponent.Frontend, RolloutStepKind.Frontend) })
        {
            if (targets.FirstOrDefault(t => t.Component == component && t.Configured) is not { } target)
            {
                continue;
            }

            var noPreviews = environment == DeploymentEnvironment.Preview && !DeploymentPlatforms.Capabilities(target.Platform).Previews;
            steps.Add(new RolloutStepPlan(kind, noPreviews, noPreviews ? $"{DeploymentPlatforms.DisplayName(target.Platform)} has no previews; the {component.ToWire()} preview is skipped." : null));
        }

        return steps;
    }

    /// <summary>What to do next given each step's state (a step without a state is pending).</summary>
    public static RolloutDecision Next(IReadOnlyList<RolloutStepPlan> plan, IReadOnlyDictionary<RolloutStepKind, RolloutStepState> states)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(states);
        foreach (var step in plan)
        {
            if (step.Skipped)
            {
                continue;
            }

            switch (states.GetValueOrDefault(step.Kind, RolloutStepState.Pending))
            {
                case RolloutStepState.Succeeded or RolloutStepState.Skipped:
                    continue;
                case RolloutStepState.Failed:
                    return new RolloutDecision(RolloutDecisionKind.Failed, step.Kind);
                case RolloutStepState.Running:
                    return new RolloutDecision(RolloutDecisionKind.Wait, step.Kind);
                default:
                    return new RolloutDecision(RolloutDecisionKind.Start, step.Kind);
            }
        }

        return new RolloutDecision(RolloutDecisionKind.Completed, null);
    }

    /// <summary>A deployment's state as a step: queued/building running, ready succeeded, error/canceled failed.</summary>
    public static RolloutStepState StateOf(DeploymentStatus status) => status switch
    {
        DeploymentStatus.Ready => RolloutStepState.Succeeded,
        DeploymentStatus.Error or DeploymentStatus.Canceled => RolloutStepState.Failed,
        _ => RolloutStepState.Running,
    };

    public static RolloutStepState StateOf(MigrationRunStatus status) => status switch
    {
        MigrationRunStatus.Succeeded => RolloutStepState.Succeeded,
        MigrationRunStatus.Failed => RolloutStepState.Failed,
        _ => RolloutStepState.Running,
    };

    public static DeploymentComponent ComponentOf(RolloutStepKind kind) =>
        kind == RolloutStepKind.Backend ? DeploymentComponent.Backend : DeploymentComponent.Frontend;

    /// <summary>
    /// FR-154: whether a failed deployment failed in its pre-deploy (the platform-run ORM migration): Render's
    /// <c>pre_deploy_failed</c>, the Fake's <c>.aide-fake/fail-migration</c>, or any message naming the pre-deploy step.
    /// </summary>
    public static bool IsPreDeployFailure(string? errorMessage) =>
        errorMessage is { Length: > 0 } message
        && (message.Contains(RenderRules.PreDeployFailed, StringComparison.OrdinalIgnoreCase)
            || message.Contains("pre-deploy", StringComparison.OrdinalIgnoreCase)
            || message.Contains(FakeDeploymentRules.FailMigrationMarker, StringComparison.Ordinal));
}
