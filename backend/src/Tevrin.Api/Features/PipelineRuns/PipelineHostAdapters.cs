using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Auth;
using Tevrin.Api.Errors;
using Tevrin.Api.Features.Gates;
using Tevrin.Domain.Access;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Features.PipelineRuns;

/// <summary>
/// FR-299–FR-303, §2: the engine endpoints use the project's permissions through <see cref="IProjectAccess"/> (view:
/// 404 for projects the caller can't see). Visible projects are members' projects, or every project for Admins.
/// </summary>
internal sealed class ProjectRunAccessPolicy(IProjectAccess access, TevrinDbContext db, ICurrentUser currentUser, SystemRoleLookup roles) : IRunAccessPolicy
{
    public async Task<DomainError?> CheckViewAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(projectId, ProjectAction.View, cancellationToken);
        return grant.IsFailure ? grant.Error : null;
    }

    public async Task<IQueryable<Guid>> VisibleProjectIdsAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return db.Projects.Where(_ => false).Select(p => p.Id);
        }

        return await roles.GetAsync(userId, cancellationToken) == SystemRole.Admin
            ? db.Projects.Select(p => p.Id)
            : db.ProjectMembers.Where(m => m.UserId == userId).Select(m => m.ProjectId);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ProjectNamesAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        var ids = projectIds.ToList();
        return await db.Projects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }
}

/// <summary>FR-301: engine gate decisions call <see cref="GateService"/> with the project gate endpoints' rule
/// (<see cref="ProjectAction.DecideGate"/>), so rules, errors, follow-ups and audit are identical.</summary>
internal sealed class ProjectGateDecisionService(
    IProjectAccess access, ICurrentUser currentUser, GateService gates, TevrinDbContext db, SystemRoleLookup roles) : IGateDecisionService
{
    public async Task<DomainError?> CheckDecideAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(projectId, ProjectAction.DecideGate, cancellationToken);
        return grant.IsFailure ? grant.Error : null;
    }

    public async Task<IReadOnlySet<Guid>> DecidableProjectsAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        if (currentUser.UserId is not { } userId || projectIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var systemRole = await roles.GetAsync(userId, cancellationToken) ?? SystemRole.User;
        var ids = projectIds.ToList();
        var memberships = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId && ids.Contains(m.ProjectId))
            .Select(m => new { m.ProjectId, m.Role })
            .ToDictionaryAsync(m => m.ProjectId, m => (ProjectRole?)m.Role, cancellationToken);

        // The same matrix as IProjectAccess.CheckAsync(projectId, ProjectAction.DecideGate) for each listed project.
        return ids
            .Where(id => ProjectPermissions.Check(systemRole, memberships.GetValueOrDefault(id), ProjectAction.DecideGate) == AccessDecision.Allowed)
            .ToHashSet();
    }

    public Task<Result<ApprovalGate>> ApproveAsync(Guid projectId, Guid gateId, PipelineGateDecision decision, CancellationToken cancellationToken) =>
        gates.ApproveAsync(projectId, gateId, ToRequest(decision), currentUser.RequiredUserId, currentUser.Actor, cancellationToken);

    public Task<Result<ApprovalGate>> RejectAsync(Guid projectId, Guid gateId, PipelineGateDecision decision, CancellationToken cancellationToken) =>
        gates.RejectAsync(projectId, gateId, ToRequest(decision), currentUser.RequiredUserId, currentUser.Actor, cancellationToken);

    private static DecideGateRequest ToRequest(PipelineGateDecision decision) =>
        new() { Comment = decision.Comment, Decision = decision.Decision, TargetCheckpoint = decision.TargetCheckpoint };
}

/// <summary>FR-300: the run's <c>state_transitions</c> (stage ids are the SDLC phases' wire values).</summary>
internal sealed class StateTransitionSource(TevrinDbContext db) : IPipelineTransitionSource
{
    public async Task<IReadOnlyList<PipelineTransitionRow>> ListAsync(Guid pipelineRunId, TimelineWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var query = db.StateTransitions.AsNoTracking().Where(t => t.PipelineRunId == pipelineRunId);
        if (window.At is { } at)
        {
            query = query.Where(t => t.CreatedAt == at);
        }

        if (window.After is { } after)
        {
            query = query.Where(t => t.CreatedAt > after);
        }

        query = query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id);
        if (window.Take is { } take)
        {
            query = query.Take(take);
        }

        var rows = await query.ToListAsync(cancellationToken);
        return [.. rows.Select(t => new PipelineTransitionRow(
            t.Id, t.CreatedAt, t.FromPhase?.ToWire(), t.ToPhase.ToWire(), t.Kind, t.Checkpoint, string.IsNullOrEmpty(t.Reason) ? null : t.Reason))];
    }
}

/// <summary>§4.57: notification jobs (FR-47) report what already happened, so they never make a run running (as the
/// rollback's active-job check leaves them out).</summary>
internal sealed class SdlcRunActivityPolicy : IRunActivityPolicy
{
    public IReadOnlyCollection<string> IgnoredJobTypes { get; } = [SdlcJobTypes.Notification];
}

/// <summary>The engine endpoints' problems are the host's (<see cref="ApiProblems"/>, CLAUDE.md rule 4).</summary>
internal sealed class EngineProblems : IEngineProblems
{
    public ProblemHttpResult FromError(DomainError problem) => ApiProblems.FromError(problem);
}

/// <summary>Registration of the engine's generic API in the SDLC host (FR-299–FR-303).</summary>
public static class PipelineApiHosting
{
    public static IServiceCollection AddPipelineApiHost(this IServiceCollection services)
    {
        services.AddScoped<IRunAccessPolicy, ProjectRunAccessPolicy>();
        services.AddScoped<IGateDecisionService, ProjectGateDecisionService>();
        services.AddScoped<IPipelineTransitionSource, StateTransitionSource>();
        services.AddSingleton<IEngineProblems, EngineProblems>();
        services.AddSingleton<IRunActivityPolicy, SdlcRunActivityPolicy>();
        return Tevrin.Engine.Api.EngineApiServiceCollectionExtensions.AddTevrinEngineApi(services);
    }
}
