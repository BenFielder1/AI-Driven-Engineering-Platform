using System.Globalization;
using Aide.Domain.Common;

namespace Aide.Domain.Trackers;

/// <summary>§4.26 <b>Limits:</b> what an import may hold, and how an item is trimmed before it is stored.</summary>
public static class SourceItemLimits
{
    /// <summary>Items across a version's active imports.</summary>
    public const int MaxItems = 200;

    /// <summary>The cap on the rendered size, however large the model's context window.</summary>
    public const int MaxRenderedBytesCap = 800_000;

    /// <summary>The rendered size allowed for a model the catalog doesn't list.</summary>
    public const int UnlistedModelBytes = 100_000;

    public const int MaxDescriptionCharacters = 20_000;

    public const int MaxComments = 20;

    public const int MaxCommentCharacters = 2_000;

    /// <summary>
    /// §4.26: <c>min(floor(contextTokens × 0.25) × 4, 800,000)</c> bytes; 100,000 for an unlisted model
    /// (<paramref name="contextTokens"/> <c>null</c> or not positive).
    /// </summary>
    public static int MaxRenderedBytes(int? contextTokens)
    {
        if (contextTokens is not > 0)
        {
            return UnlistedModelBytes;
        }

        return (int)Math.Min((contextTokens.Value / 4L) * 4L, MaxRenderedBytesCap);
    }

    /// <summary><c>SOURCE_ITEMS_TOO_LARGE</c> when <paramref name="count"/> is over <see cref="MaxItems"/>.</summary>
    public static Result CheckItemCount(int count) =>
        count > MaxItems
            ? Result.Failure(TooLarge(
                count,
                MaxItems,
                $"These items are too many to import ({count.ToString(CultureInfo.InvariantCulture)} of {MaxItems.ToString(CultureInfo.InvariantCulture)}). Remove some items."))
            : Result.Success();

    /// <summary><c>SOURCE_ITEMS_TOO_LARGE</c>, with the size and limit, when the rendered snapshot is over <paramref name="limit"/> bytes.</summary>
    public static Result CheckRenderedSize(int bytes, int limit) =>
        bytes > limit
            ? Result.Failure(TooLarge(
                bytes,
                limit,
                $"These items are too large for the AI model ({bytes.ToString(CultureInfo.InvariantCulture)} of {limit.ToString(CultureInfo.InvariantCulture)} bytes). Remove some items or choose a larger model."))
            : Result.Success();

    /// <summary>
    /// §4.26: an item as stored: a long description cut with <c>(truncated; see {url})</c>; the newest 20 comments, each up to
    /// 2,000 characters, with a count of the older ones dropped; assignee and comments only when opted in.
    /// </summary>
    public static SourceItem Normalize(SourceItem item, TrackerImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        item = StripNul(item);
        var description = item.DescriptionMarkdown.Length > MaxDescriptionCharacters
            ? $"{item.DescriptionMarkdown[..MaxDescriptionCharacters]}\n\n(truncated; see {item.Url})"
            : item.DescriptionMarkdown;

        IReadOnlyList<SourceItemComment>? comments = null;
        var dropped = 0;
        if (options.IncludeComments && item.Comments is { } all)
        {
            var newest = all.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.BodyMarkdown, StringComparer.Ordinal).ToList();
            dropped = Math.Max(0, newest.Count - MaxComments) + item.CommentsDropped;
            comments =
            [
                .. newest.Take(MaxComments).Select(c => c.BodyMarkdown.Length > MaxCommentCharacters
                    ? c with { BodyMarkdown = c.BodyMarkdown[..MaxCommentCharacters] }
                    : c),
            ];
        }

        return item with
        {
            DescriptionMarkdown = description,
            Assignee = options.IncludeAssignees ? item.Assignee : null,
            Comments = comments,
            CommentsDropped = dropped,
        };
    }

    /// <summary>Removes NUL characters, which PostgreSQL refuses in <c>text</c> and <c>jsonb</c>.</summary>
    public static string StripNul(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static string? StripOptional(string? text) => text is null ? null : StripNul(text);

    private static SourceItem StripNul(SourceItem item) => item with
    {
        ExternalId = StripNul(item.ExternalId),
        Key = StripNul(item.Key),
        Url = StripNul(item.Url),
        NativeType = StripNul(item.NativeType),
        Title = StripNul(item.Title),
        DescriptionMarkdown = StripNul(item.DescriptionMarkdown),
        Status = StripNul(item.Status),
        Priority = StripOptional(item.Priority),
        Labels = [.. item.Labels.Select(StripNul)],
        Assignee = StripOptional(item.Assignee),
        Estimate = StripOptional(item.Estimate),
        ParentKey = StripOptional(item.ParentKey),
        ChildKeys = [.. item.ChildKeys.Select(StripNul)],
        Links = [.. item.Links.Select(l => new SourceItemLink(StripNul(l.Relation), StripNul(l.Key)))],
        AcceptanceCriteria = StripOptional(item.AcceptanceCriteria),
        Comments = item.Comments is null ? null : [.. item.Comments.Select(c => c with { Author = StripOptional(c.Author), BodyMarkdown = StripNul(c.BodyMarkdown) })],
        AttachmentNames = [.. item.AttachmentNames.Select(StripNul)],
    };

    /// <summary>Fills each item's children from the parents the other items name, keeping those the tracker already listed. Pure.</summary>
    public static IReadOnlyList<SourceItem> LinkChildren(IReadOnlyList<SourceItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var byParent = items.Where(i => i.ParentKey is not null).ToLookup(i => i.ParentKey!, i => i.Key, StringComparer.Ordinal);
        return
        [
            .. items.Select(i => i with
            {
                ChildKeys = [.. i.ChildKeys.Concat(byParent[i.Key]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            }),
        ];
    }

    private static DomainError TooLarge(long size, long limit, string message) =>
        new(ErrorCodes.SourceItemsTooLarge, message)
        {
            Details =
            [
                $"size: {size.ToString(CultureInfo.InvariantCulture)}",
                $"limit: {limit.ToString(CultureInfo.InvariantCulture)}",
            ],
        };
}
