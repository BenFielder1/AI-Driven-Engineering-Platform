using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.Trackers;

/// <summary>An active import as the renderer sees it: its id, when it was made (the newest wins a duplicate) and its items.</summary>
public sealed record SourceImportSnapshot(Guid ImportId, TrackerProvider Provider, DateTimeOffset CreatedAt, IReadOnlyList<SourceItem> Items);

/// <summary>The rendered snapshot: the Markdown, its size in UTF-8 bytes and the items kept after removing duplicates.</summary>
public sealed record RenderedSourceItems(string Markdown, int ByteCount, IReadOnlyList<SourceItem> Items);

/// <summary>
/// §4.26 <b>Rendering:</b> the Markdown of the version's active imports. Pure: the same input gives the same bytes, and so the
/// same hash. Items are ordered by import (oldest first), then hierarchy, then key. A duplicate <c>(provider, key)</c> across
/// active imports: the newest import wins. Any <c>source_items</c> tag in the content is escaped, so tracker text can't close
/// the prompt's data block. Fetch time isn't rendered: re-importing unchanged items gives the same hash.
/// </summary>
public static partial class SourceItemsRenderer
{
    /// <summary>The content of a snapshot with no items left (after the last import is removed).</summary>
    public const string Empty = "No source items are imported.";

    public static RenderedSourceItems Render(IEnumerable<SourceImportSnapshot> imports)
    {
        ArgumentNullException.ThrowIfNull(imports);
        var ordered = imports.OrderBy(i => i.CreatedAt).ThenBy(i => i.ImportId).ToList();

        // Newest first decides duplicates; items then render under the import that kept them.
        var seen = new HashSet<(TrackerProvider, string)>();
        var kept = new Dictionary<Guid, List<SourceItem>>();
        foreach (var import in Enumerable.Reverse(ordered))
        {
            kept[import.ImportId] = [.. import.Items.Where(i => seen.Add((i.Provider, i.Key)))];
        }

        var text = new StringBuilder();
        var all = new List<SourceItem>();
        foreach (var import in ordered)
        {
            var items = kept[import.ImportId];
            if (items.Count == 0)
            {
                continue;
            }

            text.Append("## Imported from ").Append(TrackerRules.DisplayName(import.Provider)).Append("\n\n");
            foreach (var item in InHierarchyOrder(items))
            {
                all.Add(item);
                Append(text, item);
            }
        }

        var markdown = Escape(all.Count == 0 ? Empty : text.ToString().TrimEnd('\n') + "\n");
        return new RenderedSourceItems(markdown, Encoding.UTF8.GetByteCount(markdown), all);
    }

    /// <summary>§4.26: escapes every opening or closing <c>source_items</c> tag, whatever its case or spacing.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SourceItemsTag().Replace(text, "&lt;$1source_items");
    }

    /// <summary>Depth first: each root (no parent among these items), by key, followed by its descendants, by key.</summary>
    private static List<SourceItem> InHierarchyOrder(List<SourceItem> items)
    {
        var keys = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var children = items
            .Where(i => i.ParentKey is not null && keys.Contains(i.ParentKey) && i.ParentKey != i.Key)
            .ToLookup(i => i.ParentKey!, StringComparer.Ordinal);
        var result = new List<SourceItem>(items.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(SourceItem item)
        {
            if (!visited.Add(item.Key))
            {
                return;
            }

            result.Add(item);
            foreach (var child in children[item.Key].OrderBy(c => c.Key, KeyOrder.Instance))
            {
                Visit(child);
            }
        }

        foreach (var root in items.Where(i => i.ParentKey is null || !keys.Contains(i.ParentKey) || i.ParentKey == i.Key).OrderBy(i => i.Key, KeyOrder.Instance))
        {
            Visit(root);
        }

        // A parent cycle has no root: its items still render, in key order.
        foreach (var rest in items.OrderBy(i => i.Key, KeyOrder.Instance))
        {
            Visit(rest);
        }

        return result;
    }

    private static void Append(StringBuilder text, SourceItem item)
    {
        text.Append("### ").Append(item.Key).Append(": ").Append(OneLine(item.Title)).Append('\n');
        Field(text, "Type", $"{item.Type.ToWire()} ({OneLine(item.NativeType)})");
        Field(text, "Status", $"{OneLine(item.Status)} ({item.StatusCategory.ToWire()})");
        Field(text, "Priority", item.Priority);
        Field(text, "Labels", item.Labels.Count == 0 ? null : string.Join(", ", item.Labels.Select(OneLine)));
        Field(text, "Assignee", item.Assignee);
        Field(text, "Estimate", item.Estimate);
        Field(text, "Parent", item.ParentKey);
        Field(text, "Children", item.ChildKeys.Count == 0 ? null : string.Join(", ", item.ChildKeys));
        Field(text, "Links", item.Links.Count == 0 ? null : string.Join(", ", item.Links.Select(l => $"{OneLine(l.Relation)} {l.Key}")));
        Field(text, "Attachments (names only)", item.AttachmentNames.Count == 0 ? null : string.Join(", ", item.AttachmentNames.Select(OneLine)));
        Field(text, "Updated", item.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        Field(text, "URL", item.Url);
        text.Append('\n');
        Block(text, "Description", item.DescriptionMarkdown);
        Block(text, "Acceptance criteria", item.AcceptanceCriteria);
        if (item.Comments is { Count: > 0 } comments)
        {
            text.Append("Comments");
            if (item.CommentsDropped > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" ({item.CommentsDropped} older dropped)");
            }

            text.Append(":\n\n");
            foreach (var comment in comments)
            {
                var when = comment.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                text.Append("- ").Append(OneLine(comment.Author ?? "unknown")).Append(", ").Append(when).Append(": ")
                    .Append(Lines(comment.BodyMarkdown).Replace("\n", "\n  ", StringComparison.Ordinal)).Append('\n');
            }

            text.Append('\n');
        }
    }

    private static void Field(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append("- ").Append(name).Append(": ").Append(value).Append('\n');
        }
    }

    private static void Block(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append(name).Append(":\n\n").Append(Lines(value)).Append("\n\n");
        }
    }

    private static string Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static string OneLine(string text) => WhiteSpaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"<(\s*/?\s*)source_items", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceItemsTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRun();

    /// <summary>Keys sort by their text, then the number that ends them (<c>PROJ-2</c> before <c>PROJ-10</c>).</summary>
    private sealed partial class KeyOrder : IComparer<string>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = Split(x ?? string.Empty);
            var right = Split(y ?? string.Empty);
            var text = string.CompareOrdinal(left.Prefix, right.Prefix);
            if (text != 0)
            {
                return text;
            }

            var number = left.Number.CompareTo(right.Number);
            return number != 0 ? number : string.CompareOrdinal(x, y);
        }

        private static (string Prefix, long Number) Split(string key)
        {
            var match = TrailingNumber().Match(key);
            return match.Success && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? (match.Groups[1].Value, number)
                : (key, -1);
        }

        [GeneratedRegex(@"^(.*?)(\d{1,18})$", RegexOptions.CultureInvariant)]
        private static partial Regex TrailingNumber();
    }
}
