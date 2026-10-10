namespace Aide.Domain.Agents;

/// <summary>
/// Tolerant reading of agent replies (FR-27–FR-29): models sometimes wrap a document or a JSON object in a Markdown code
/// fence or add a sentence around it, although the prompts ask them not to.
/// </summary>
public static class AgentOutput
{
    /// <summary>The run key of the one corrective re-run a job makes after invalid output (FR-28, FR-29).</summary>
    public const string CorrectiveRunKey = "corrective";

    /// <summary>
    /// The text without a fence that wraps all of it (<c>```markdown … ```</c>); otherwise the trimmed text. A fence that
    /// only wraps part of the text is kept.
    /// </summary>
    public static string UnwrapFence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.ReplaceLineEndings("\n").Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal) || trimmed.Length < 6)
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewLine < 0)
        {
            return trimmed;
        }

        var inner = trimmed[(firstNewLine + 1)..^3];

        // Only one fence: the opening line's fence must not be closed before the end.
        return inner.Split('\n').Any(line => line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            ? trimmed
            : inner.Trim();
    }

    /// <summary>
    /// The JSON object in a reply: the text itself, the content of a wrapping fence, or the span from the first
    /// <c>{</c> to the last <c>}</c>. <c>null</c> when there is no object at all.
    /// </summary>
    public static string? ExtractJsonObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var unwrapped = UnwrapFence(text);
        var start = unwrapped.IndexOf('{', StringComparison.Ordinal);
        var end = unwrapped.LastIndexOf('}');
        return start < 0 || end <= start ? null : unwrapped[start..(end + 1)];
    }
}
