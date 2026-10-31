using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// One step of a rollout (FR-147, FR-149): <c>step</c> (<c>migrations</c>, <c>backend</c>, <c>frontend</c>) and its
/// <c>state</c>. A deployment step names its <c>deploymentId</c>, <c>provider</c> (the host), <c>status</c>, <c>url</c>,
/// <c>inspectorUrl</c>, <c>buildLogTail</c> (masked) and error; <c>canRedeploy</c> is true when it finished (redeploy with
/// <c>POST …/deployments/{deploymentId}/redeploy</c>). The migrations step names its <c>migrationRunId</c> (details at
/// <c>GET …/migrations/{runId}</c>). <c>note</c> explains a skipped step (e.g. GitHub Pages has no previews).
/// </summary>
public sealed record RolloutStepDto(
    RolloutStepKind Step,
    RolloutStepState State,
    DeploymentProvider? Provider,
    Guid? DeploymentId,
    DeploymentStatus? Status,
    string? Url,
    string? InspectorUrl,
    string? BuildLogTail,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? MigrationRunId,
    string? Note,
    bool CanRedeploy);

/// <summary>
/// A preview or production rollout (FR-147, FR-149): <c>rolloutId</c> (the job id, = <c>deployments.rolloutId</c>), the
/// commit, its overall <c>state</c> (<c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>blocked</c> — FR-148
/// <c>PREVIEW_DATABASE_DIVERGED</c>, reset the preview database —, <c>canceled</c> when a newer preview replaced it), the
/// error when it stopped, and its <c>steps</c> in order (migrations → backend → frontend).
/// </summary>
public sealed record RolloutDto(
    Guid? RolloutId,
    DeploymentEnvironment Environment,
    RolloutState State,
    Guid ReleaseVersionId,
    Guid? ReleaseId,
    string GitRef,
    string GitSha,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<RolloutStepDto> Steps,
    DateTimeOffset RequestedAt);

/// <summary>
/// FR-147 Review page: the project's persistent preview environment: the <c>current</c> (newest) preview rollout, or
/// <c>null</c> before the first; <c>notes</c> for the page (e.g. "Preview uses the production database without this
/// version's migrations", FR-154); and <c>databaseDiverged</c> with its <c>divergedMessage</c> when the preview database has
/// migrations the version branch no longer has (FR-148: the Owner sees "Reset preview database").
/// </summary>
public sealed record PreviewEnvironmentDto(RolloutDto? Current, IReadOnlyList<string> Notes, bool DatabaseDiverged, string? DivergedMessage);

/// <summary>
/// FR-149: where a released version is live: the component, the host (<c>provider</c> and its <c>providerName</c>, e.g.
/// "Netlify") and the production <c>url</c> — for "Released 0.1.0 · live on Netlify and Render".
/// </summary>
public sealed record LiveTargetDto(DeploymentComponent Component, DeploymentProvider Provider, string ProviderName, string? Url);
