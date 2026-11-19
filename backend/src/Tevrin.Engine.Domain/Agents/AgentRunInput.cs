using System.Text;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>One message of a recorded agent input: its role (<c>system</c>, <c>user</c>, <c>assistant</c>) and content.</summary>
public sealed record AgentInputMessage(string Role, string Content);

/// <summary>FR-297, D-0.7.0-16: the stored text of an agent run's input and whether it was cut at the cap.</summary>
public sealed record RecordedAgentInput(string Text, bool Truncated);

/// <summary>
/// FR-297, D-0.7.0-16: what an agent run stores as its <c>input</c>: the rendered prompt and messages as plain text (each
/// message under a <c>[role]</c> line), with credentials masked by <see cref="SecretRedaction"/> and at most
/// <see cref="MaxBytes"/> UTF-8 bytes (cut on a character boundary, then flagged as truncated). Recording never changes
/// what is sent to the provider.
/// </summary>
public static class AgentRunInput
{
    /// <summary>The cap on a stored input: 1 MB of UTF-8.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The stored input for <paramref name="messages"/> (the system prompt first), redacted with the caller's
    /// known <paramref name="secrets"/> as well as the usual key patterns, and capped.</summary>
    public static RecordedAgentInput Record(IEnumerable<AgentInputMessage> messages, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(secrets);
        var text = new StringBuilder();
        foreach (var message in messages)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append('[').Append(message.Role).Append("]\n").Append(message.Content);
        }

        // Text far past the cap is dropped before redacting, so a huge prompt can't run the patterns into their timeout.
        var precut = text.Length > PrecutChars;
        var redacted = SecretRedaction.Redact(precut ? text.ToString(0, PrecutChars) : text.ToString(), secrets);
        var capped = Cap(redacted);
        return capped with { Truncated = capped.Truncated || precut };
    }

    /// <summary>Characters kept before redacting (twice the cap; every character is at least one UTF-8 byte). Input cut here
    /// is flagged as truncated.</summary>
    private const int PrecutChars = 2 * MaxBytes;

    /// <summary><paramref name="text"/> cut to at most <see cref="MaxBytes"/> UTF-8 bytes without splitting a character
    /// (a surrogate pair stays whole).</summary>
    public static RecordedAgentInput Cap(string text, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return new RecordedAgentInput(text, false);
        }

        var bytes = 0;
        var length = 0;
        while (length < text.Length)
        {
            var width = char.IsSurrogatePair(text, length) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(length, width));
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            length += width;
        }

        return new RecordedAgentInput(text[..length], true);
    }
}
