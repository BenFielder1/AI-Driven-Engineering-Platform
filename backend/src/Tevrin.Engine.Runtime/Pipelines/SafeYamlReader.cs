using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>A node of a parsed YAML document (FR-275). Mapping entries keep file order and duplicates.</summary>
internal abstract class YamlNode
{
    /// <summary>1-based line of the node, for messages.</summary>
    public int Line { get; init; }

    /// <summary>The node's size with every alias expanded (characters plus one per node), saturating past the limit.</summary>
    public long ExpandedSize { get; set; }
}

internal sealed class YamlScalar(string value, ScalarStyle style) : YamlNode
{
    public string Value { get; } = value;

    public ScalarStyle Style { get; } = style;

    /// <summary>A plain <c>~</c>, <c>null</c> or empty scalar (YAML core schema null).</summary>
    public bool IsNull => Style is ScalarStyle.Plain or ScalarStyle.Any && Value is "" or "~" or "null" or "Null" or "NULL";
}

internal sealed class YamlSequence : YamlNode
{
    public List<YamlNode> Items { get; } = [];
}

internal sealed class YamlMapping : YamlNode
{
    public List<KeyValuePair<YamlScalar, YamlNode>> Entries { get; } = [];
}

/// <summary>Why a document was refused before mapping (syntax, size, tags, aliases); reported as <c>DEF_PARSE</c> at <c>$</c>.</summary>
internal sealed class YamlRefusedException(string message) : Exception(message);

/// <summary>
/// FR-275: reads one YAML document into <see cref="YamlNode"/>s using only YamlDotNet's event parser: nothing is ever
/// deserialised into a CLR type. Refuses explicit tags (<c>!!python/object</c>, <c>!custom</c>, even <c>!!str</c>),
/// documents over <see cref="MaxBytes"/>, alias expansion over <see cref="MaxExpandedSize"/> (alias bombs), aliases to
/// unknown or still-open anchors, complex keys, more than one document and nesting deeper than <see cref="MaxDepth"/>.
/// Expanded sizes are computed bottom-up as nodes close, so an alias bomb is refused in linear time.
/// </summary>
internal static class SafeYamlReader
{
    public const int MaxBytes = 1024 * 1024;
    public const long MaxExpandedSize = 1024 * 1024;
    public const int MaxDepth = 64;

    public static YamlNode Read(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new YamlRefusedException($"The definition is larger than {MaxBytes / 1024} KB.");
        }

        var parser = new Parser(new StringReader(text));
        var anchors = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        var stack = new Stack<(YamlNode Node, string? Anchor, YamlScalar? PendingKey)>();
        YamlNode? root = null;
        var documents = 0;

        try
        {
            while (parser.MoveNext())
            {
                var current = parser.Current!;
                switch (current)
                {
                    case DocumentStart:
                        if (++documents > 1)
                        {
                            throw new YamlRefusedException("The file has more than one YAML document.");
                        }

                        break;
                    case Scalar scalar:
                        RefuseTag(scalar);
                        var node = new YamlScalar(scalar.Value, scalar.Style) { Line = (int)scalar.Start.Line, ExpandedSize = scalar.Value.Length + 1 };
                        RegisterAnchor(anchors, scalar.Anchor, node);
                        Attach(stack, node, ref root);
                        break;
                    case AnchorAlias alias:
                        if (!anchors.TryGetValue(alias.Value.Value, out var target))
                        {
                            throw new YamlRefusedException($"Line {alias.Start.Line}: alias '*{alias.Value.Value}' names no complete anchor.");
                        }

                        Attach(stack, target, ref root);
                        break;
                    case SequenceStart sequenceStart:
                        RefuseTag(sequenceStart);
                        Push(stack, new YamlSequence { Line = (int)sequenceStart.Start.Line, ExpandedSize = 1 }, sequenceStart.Anchor);
                        break;
                    case MappingStart mappingStart:
                        RefuseTag(mappingStart);
                        Push(stack, new YamlMapping { Line = (int)mappingStart.Start.Line, ExpandedSize = 1 }, mappingStart.Anchor);
                        break;
                    case SequenceEnd or MappingEnd:
                        var (closed, anchor, pendingKey) = stack.Pop();
                        if (pendingKey is not null)
                        {
                            throw new YamlRefusedException($"Line {closed.Line}: a mapping key has no value.");
                        }

                        if (anchor is not null)
                        {
                            anchors[anchor] = closed;
                        }

                        Attach(stack, closed, ref root);
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            throw new YamlRefusedException($"Line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}");
        }

        return root ?? throw new YamlRefusedException("The file is empty.");
    }

    private static void RefuseTag(NodeEvent node)
    {
        if (!node.Tag.IsEmpty)
        {
            throw new YamlRefusedException($"Line {node.Start.Line}: YAML tags ('{node.Tag.Value}') are not allowed.");
        }
    }

    private static void RegisterAnchor(Dictionary<string, YamlNode> anchors, AnchorName anchor, YamlNode node)
    {
        if (!anchor.IsEmpty)
        {
            anchors[anchor.Value] = node;
        }
    }

    private static void Push(Stack<(YamlNode Node, string? Anchor, YamlScalar? PendingKey)> stack, YamlNode node, AnchorName anchor)
    {
        if (stack.Count >= MaxDepth)
        {
            throw new YamlRefusedException($"Line {node.Line}: the document is nested more than {MaxDepth} levels deep.");
        }

        stack.Push((node, anchor.IsEmpty ? null : anchor.Value, null));
    }

    private static void Attach(Stack<(YamlNode Node, string? Anchor, YamlScalar? PendingKey)> stack, YamlNode node, ref YamlNode? root)
    {
        if (stack.Count == 0)
        {
            root = node;
            return;
        }

        var (parent, anchor, pendingKey) = stack.Pop();
        parent.ExpandedSize = Math.Min(parent.ExpandedSize + node.ExpandedSize, MaxExpandedSize + 1);
        if (parent.ExpandedSize > MaxExpandedSize)
        {
            throw new YamlRefusedException($"Line {node.Line}: aliases expand the document beyond {MaxExpandedSize / 1024} KB.");
        }

        switch (parent)
        {
            case YamlSequence sequence:
                sequence.Items.Add(node);
                stack.Push((parent, anchor, null));
                break;
            case YamlMapping when pendingKey is null:
                if (node is not YamlScalar key)
                {
                    throw new YamlRefusedException($"Line {node.Line}: mapping keys must be plain values.");
                }

                stack.Push((parent, anchor, key));
                break;
            case YamlMapping mapping:
                mapping.Entries.Add(new KeyValuePair<YamlScalar, YamlNode>(pendingKey, node));
                stack.Push((parent, anchor, null));
                break;
        }
    }
}
