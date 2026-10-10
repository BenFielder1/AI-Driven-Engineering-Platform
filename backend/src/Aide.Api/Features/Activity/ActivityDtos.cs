using System.Text.Json.Nodes;
using Aide.Domain.Common;

namespace Aide.Api.Features.Activity;

/// <summary>Who did something: a user (<c>id</c> and display name) or the system actor (<c>isSystem</c>, no <c>id</c>).</summary>
public sealed record ActorDto(Guid? Id, string Name, bool IsSystem);

/// <summary>
/// One entry of a project's activity feed (FR-50). <c>type</c> is the stored event type (e.g. <c>PHASE_CHANGED</c>),
/// <c>category</c> its feed category (<c>null</c> for internal events), <c>summary</c> a one-line description and
/// <c>payload</c> the raw event payload.
/// </summary>
public sealed record ActivityItemDto(
    Guid Id,
    string Type,
    EventCategory? Category,
    DateTimeOffset OccurredAt,
    ActorDto Actor,
    string Summary,
    JsonObject Payload);

/// <summary>A page of the activity feed, newest first. Pass <c>nextBefore</c> as <c>before</c> to load more; <c>null</c> when there is no more.</summary>
public sealed record ActivityPageDto(IReadOnlyList<ActivityItemDto> Items, string? NextBefore);

/// <summary>Request limits of the activity feed (FR-50).</summary>
public static class ActivityLimits
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    public const int CursorMaxLength = 200;
}
