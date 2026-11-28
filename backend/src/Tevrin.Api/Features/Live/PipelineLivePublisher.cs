using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Api.Features.Live;

/// <summary>
/// FR-302: after a committed save, pushes the engine's live messages to the project's existing group: a gate event sends
/// <c>gateUpdated</c> (the gate DTO); a run-changing event sends <c>pipelineRunUpdated</c> (the project's current run);
/// a transition sends <c>stageUpdated</c> for each stage of that run. The DTOs are read in a fresh scope after commit; a
/// failure is logged and never fails the write. Nothing is read for a project nobody is viewing. Existing <c>projectEvent</c> messages are unchanged.
/// </summary>
public sealed partial class PipelineLivePublisher(
    IServiceScopeFactory scopes,
    IHubContext<ProjectsHub> hub,
    ProjectViewerRegistry viewers,
    ILogger<PipelineLivePublisher> logger)
{
    private static readonly HashSet<string> GateEvents = new(StringComparer.Ordinal)
    {
        EventTypes.GateCreated, EventTypes.GateApproved, EventTypes.GateRejected, EventTypes.GateCancelled, EventTypes.GateOverdue,
    };

    private static readonly HashSet<string> TransitionEvents = new(StringComparer.Ordinal)
    {
        EventTypes.PhaseChanged, EventTypes.VersionStarted, EventTypes.RollbackCompleted, EventTypes.ReleaseCompleted, EventTypes.ReleaseMergedExternally,
    };

    private static readonly HashSet<string> RunEvents = new(StringComparer.Ordinal)
    {
        EventTypes.RunStarted, EventTypes.RunCompleted, EventTypes.RunFailed, EventTypes.ReviewStarted, EventTypes.ReviewCompleted,
        EventTypes.ReviewFailed, EventTypes.ReleaseStarted, EventTypes.ReleaseFailed, EventTypes.ReleaseAwaitingMerge,
        EventTypes.ExecutionStarted, EventTypes.ExecutionCompleted, EventTypes.ArtifactCreated, EventTypes.ArtifactApproved,
    };

    public async Task PublishAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var batch in events.Where(e => e.ProjectId is not null).GroupBy(e => e.ProjectId!.Value))
        {
            if (!viewers.HasViewers(batch.Key))
            {
                continue;
            }

            var types = batch.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
            var gateIds = batch.Where(e => GateEvents.Contains(e.Type)).Select(e => GateIdOf(e.Payload)).OfType<Guid>().Distinct().ToList();
            var transition = types.Overlaps(TransitionEvents);
            if (gateIds.Count == 0 && !transition && !types.Overlaps(RunEvents))
            {
                continue;
            }

            try
            {
                await PushAsync(batch.Key, gateIds, transition, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPushFailed(logger, ex, batch.Key);
            }
        }
    }

    private async Task PushAsync(Guid projectId, List<Guid> gateIds, bool transition, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<PipelineRunReader>();
        var db = scope.ServiceProvider.GetRequiredService<IEngineDbContext>();
        var group = hub.Clients.Group(ProjectsHub.GroupFor(projectId));

        if (gateIds.Count > 0)
        {
            var gates = await db.ApprovalGates.AsNoTracking()
                .Where(g => gateIds.Contains(g.Id) && g.ProjectId == projectId && g.PipelineRunId != null)
                .ToListAsync(cancellationToken);
            foreach (var gate in await reader.ToGateDtosAsync(gates, cancellationToken))
            {
                await group.SendAsync(ProjectsHub.GateUpdatedMethod, gate, cancellationToken);
            }
        }

        if (await reader.CurrentRunAsync(projectId, cancellationToken) is not { } run)
        {
            return;
        }

        await group.SendAsync(ProjectsHub.PipelineRunUpdatedMethod, await reader.ToDtoAsync(run, cancellationToken), cancellationToken);
        if (transition)
        {
            foreach (var stage in await reader.StagesAsync(run, cancellationToken))
            {
                await group.SendAsync(ProjectsHub.StageUpdatedMethod, stage, cancellationToken);
            }
        }
    }

    private static Guid? GateIdOf(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject json && json["gateId"] is JsonValue value
                && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline live update for project {ProjectId} could not be pushed")]
    private static partial void LogPushFailed(ILogger logger, Exception exception, Guid projectId);
}
