using System.Text.Json.Nodes;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace Aide.Api.Features.Live;

/// <summary>
/// Which connection views which project as which user (FR-46). Used to drop a removed member's connections from the
/// project's group as soon as the removal commits, so they stop receiving its events without reconnecting.
/// Single replica: the registry and SignalR groups are in-process (PROGRESS.md Decisions).
/// </summary>
public sealed class ProjectViewerRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (Guid UserId, HashSet<Guid> Projects)> _connections = new(StringComparer.Ordinal);

    public void Add(string connectionId, Guid userId, Guid projectId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(connectionId, out var entry))
            {
                entry = (userId, []);
                _connections[connectionId] = entry;
            }

            entry.Projects.Add(projectId);
        }
    }

    public void Remove(string connectionId, Guid projectId)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(connectionId, out var entry))
            {
                entry.Projects.Remove(projectId);
            }
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _connections.Remove(connectionId);
        }
    }

    /// <summary>Removes and returns the connections of <paramref name="userId"/> viewing <paramref name="projectId"/>.</summary>
    public IReadOnlyList<string> RemoveViewer(Guid userId, Guid projectId)
    {
        lock (_lock)
        {
            var removed = _connections
                .Where(c => c.Value.UserId == userId && c.Value.Projects.Remove(projectId))
                .Select(c => c.Key)
                .ToList();
            return removed;
        }
    }
}

/// <summary>
/// FR-46: pushes committed project events to the project's SignalR group as <c>projectEvent</c> messages. Called by
/// <see cref="ProjectEventPublishingInterceptor"/> after commit, for every writer (endpoints and background jobs). Only
/// categorised events are pushed (internal markers aren't). A failed push is logged and never fails the write.
/// </summary>
public sealed partial class SignalRProjectEventPublisher(
    IHubContext<ProjectsHub> hub,
    ProjectViewerRegistry viewers,
    ILogger<SignalRProjectEventPublisher> logger) : IProjectEventPublisher
{
    public async Task PublishAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var entry in events)
        {
            if (entry.ProjectId is not { } projectId || EventTypes.CategoryOf(entry.Type) is not { } category)
            {
                continue;
            }

            try
            {
                var payload = JsonNode.Parse(entry.Payload) as JsonObject ?? [];
                var message = new ProjectEventMessage(entry.Id, projectId, entry.Type, category, payload, entry.CreatedAt);
                // Sent by name: the typed-client proxy would use the C# name "ProjectEvent", and only the JS client matches
                // method names case-insensitively.
                await hub.Clients.Group(ProjectsHub.GroupFor(projectId)).SendAsync(ProjectsHub.ProjectEventMethod, message, cancellationToken);

                if (entry.Type == EventTypes.MemberRemoved && RemovedUser(payload) is { } userId)
                {
                    foreach (var connectionId in viewers.RemoveViewer(userId, projectId))
                    {
                        await hub.Groups.RemoveFromGroupAsync(connectionId, ProjectsHub.GroupFor(projectId), cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex, projectId, entry.Type);
            }
        }
    }

    private static Guid? RemovedUser(JsonObject payload) =>
        payload["userId"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live update for project {ProjectId} ({EventType}) could not be pushed")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid projectId, string eventType);
}
