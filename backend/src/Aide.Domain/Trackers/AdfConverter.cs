using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aide.Domain.Trackers;

/// <summary>
/// §4.26: Atlassian Document Format → Markdown, pure. Underline, colour and panels are dropped, keeping their text. Media is
/// dropped (attachments are never imported, FR-169). An unknown node keeps its children's text. Anything that isn't ADF is
/// the empty string; a plain string passes through.
/// </summary>
public static class AdfConverter
{
    public static string ToMarkdown(JsonElement document)
    {
        if (document.ValueKind == JsonValueKind.String)
        {
            return document.GetString() ?? string.Empty;
        }

        if (document.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        Block(document, text, string.Empty);
        return text.ToString().Trim();
    }

    private static void Block(JsonElement node, StringBuilder text, string indent)
    {
        switch (Type(node))
        {
            case "doc":
            case "panel":
            case "layoutSection":
            case "layoutColumn":
            case "bodiedExtension":
            case "extensionFrame":
                Blocks(node, text, indent);
                break;
            case "paragraph":
                Line(text, indent, Inline(node));
                text.Append('\n');
                break;
            case "heading":
                var level = Math.Clamp(Attr(node, "level")?.ParseInt() ?? 1, 1, 6);
                Line(text, indent, $"{new string('#', level)} {Inline(node)}");
                text.Append('\n');
                break;
            case "bulletList":
                List(node, text, indent, _ => "- ");
                break;
            case "orderedList":
                var start = Attr(node, "order")?.ParseInt() ?? 1;
                List(node, text, indent, i => string.Create(CultureInfo.InvariantCulture, $"{start + i}. "));
                break;
            case "taskList":
                List(node, text, indent, _ => "- ");
                break;
            case "decisionList":
                List(node, text, indent, _ => "- ");
                break;
            case "taskItem":
                var done = Attr(node, "state") == "DONE";
                Line(text, indent, $"[{(done ? "x" : " ")}] {Inline(node)}");
                break;
            case "decisionItem":
                Line(text, indent, Inline(node));
                break;
            case "codeBlock":
                Line(text, indent, $"```{Attr(node, "language") ?? string.Empty}");
                foreach (var line in Inline(node).Split('\n'))
                {
                    Line(text, indent, line);
                }

                Line(text, indent, "```");
                text.Append('\n');
                break;
            case "blockquote":
                var quoted = new StringBuilder();
                Blocks(node, quoted, string.Empty);
                foreach (var line in quoted.ToString().TrimEnd().Split('\n'))
                {
                    Line(text, indent, line.Length == 0 ? ">" : $"> {line}");
                }

                text.Append('\n');
                break;
            case "rule":
                Line(text, indent, "---");
                text.Append('\n');
                break;
            case "expand":
            case "nestedExpand":
                if (Attr(node, "title") is { Length: > 0 } title)
                {
                    Line(text, indent, $"**{title}**");
                    text.Append('\n');
                }

                Blocks(node, text, indent);
                break;
            case "table":
                Table(node, text, indent);
                break;
            case "mediaSingle":
            case "mediaGroup":
            case "media":
            case "mediaInline":
                break;
            default:
                if (Children(node).Count > 0)
                {
                    if (Children(node).All(c => IsInline(Type(c))))
                    {
                        Line(text, indent, Inline(node));
                        text.Append('\n');
                    }
                    else
                    {
                        Blocks(node, text, indent);
                    }
                }
                else if (IsInline(Type(node)))
                {
                    Line(text, indent, InlineNode(node));
                    text.Append('\n');
                }

                break;
        }
    }

    private static void Blocks(JsonElement node, StringBuilder text, string indent)
    {
        foreach (var child in Children(node))
        {
            Block(child, text, indent);
        }
    }

    private static void List(JsonElement node, StringBuilder text, string indent, Func<int, string> marker)
    {
        var index = 0;
        foreach (var item in Children(node))
        {
            var prefix = marker(index++);
            var inner = new StringBuilder();
            if (Type(item) == "listItem")
            {
                foreach (var child in Children(item))
                {
                    Block(child, inner, string.Empty);
                }
            }
            else
            {
                Block(item, inner, string.Empty);
            }

            var pad = new string(' ', prefix.Length);
            var lines = inner.ToString().TrimEnd().Split('\n').Where(l => l.Length > 0).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                Line(text, indent, (i == 0 ? prefix : pad) + lines[i]);
            }
        }

        text.Append('\n');
    }

    private static void Table(JsonElement node, StringBuilder text, string indent)
    {
        var rows = Children(node).Where(r => Type(r) == "tableRow").Select(r => Children(r).Select(CellText).ToList()).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(r => r.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Concat(Enumerable.Repeat(string.Empty, columns - rows[i].Count));
            Line(text, indent, $"| {string.Join(" | ", cells)} |");
            if (i == 0)
            {
                Line(text, indent, $"|{string.Concat(Enumerable.Repeat(" --- |", columns))}");
            }
        }

        text.Append('\n');
    }

    private static string CellText(JsonElement cell)
    {
        var inner = new StringBuilder();
        Blocks(cell, inner, string.Empty);
        return inner.ToString().Trim().Replace("\n\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string Inline(JsonElement node) => string.Concat(Children(node).Select(InlineNode));

    private static string InlineNode(JsonElement node)
    {
        switch (Type(node))
        {
            case "text":
                return Marked(Text(node), node);
            case "hardBreak":
                return "\n";
            case "mention":
                return Attr(node, "text") ?? string.Empty;
            case "emoji":
                return Attr(node, "text") ?? Attr(node, "shortName") ?? string.Empty;
            case "status":
                return Attr(node, "text") ?? string.Empty;
            case "date":
                return long.TryParse(Attr(node, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : string.Empty;
            case "inlineCard":
            case "blockCard":
                return Attr(node, "url") ?? string.Empty;
            case "mediaInline":
                return string.Empty;
            default:
                return Inline(node);
        }
    }

    /// <summary>Strong, emphasis, code, strike and link marks; underline, colour and anything else are dropped, keeping the text.</summary>
    private static string Marked(string text, JsonElement node)
    {
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array)
        {
            return text;
        }

        foreach (var mark in marks.EnumerateArray())
        {
            text = Type(mark) switch
            {
                "strong" => $"**{text}**",
                "em" => $"*{text}*",
                "code" => $"`{text}`",
                "strike" => $"~~{text}~~",
                "link" when Attr(mark, "href") is { Length: > 0 } href => $"[{text}]({href})",
                _ => text,
            };
        }

        return text;
    }

    private static void Line(StringBuilder text, string indent, string line) => text.Append(indent).Append(line).Append('\n');

    private static bool IsInline(string type) =>
        type is "text" or "hardBreak" or "mention" or "emoji" or "status" or "date" or "inlineCard" or "mediaInline";

    private static string Type(JsonElement node) => Property(node, "type") ?? string.Empty;

    private static string Text(JsonElement node) => Property(node, "text") ?? string.Empty;

    private static List<JsonElement> Children(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? [.. content.EnumerateArray()]
            : [];

    private static string? Attr(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("attrs", out var attrs) ? Property(attrs, name) : null;

    private static string? Property(JsonElement node, string name)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? ParseInt(this string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
