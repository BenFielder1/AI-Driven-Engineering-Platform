using System.Globalization;
using System.Text;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>One stored feedback entry as the selection rule sees it (FR-351).</summary>
public sealed record FeedbackEntry(Guid Id, string Source, string Text, DateTimeOffset CreatedAt, bool Deleted);

/// <summary>The entries chosen for a prompt and their rendered text (FR-351, §4.75).</summary>
public sealed record FeedbackSelection(IReadOnlyList<Guid> Ids, string Rendered);

/// <summary>
/// FR-351, §4.75: which feedback entries an agent stage's prompt gets in <c>{{AGENT_FEEDBACK}}</c>. Entries are taken
/// newest first, up to <c>limit</c> (default 10, maximum 50), capped at 4,000 characters in total (oldest dropped first);
/// each is rendered as <c>- [&lt;yyyy-MM-dd&gt; &lt;source&gt;] &lt;text&gt;</c>. Deleted entries are never injected.
/// </summary>
public static class FeedbackInjectionRules
{
    public const string Placeholder = "AGENT_FEEDBACK";
    public const int DefaultLimit = 10;
    public const int MinLimit = 1;
    public const int MaxLimit = 50;
    public const int MaxTotalCharacters = 4_000;
    public const int MaxTextLength = 2_000;
    public const string SourceHuman = "human";
    public const string SourceStage = "stage";

    /// <summary>
    /// FR-351: whether <paramref name="definition"/> declares agent <paramref name="agentKey"/>: some stage (or parallel
    /// branch stage) runs it or binds it under <c>agents:</c>. Feedback is kept only for declared agent keys.
    /// </summary>
    public static bool DeclaresAgent(PipelineDefinition definition, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stages.SelectMany(s => s.WithBranchStages()).Any(s =>
            string.Equals(s.Agent?.Key, agentKey, StringComparison.Ordinal)
            || s.Agents.Any(b => string.Equals(b.Key, agentKey, StringComparison.Ordinal)));
    }

    /// <summary>One entry's line: <c>- [yyyy-MM-dd source] text</c>.</summary>
    public static string Format(FeedbackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var date = entry.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"- [{date} {entry.Source}] {entry.Text}";
    }

    /// <summary>
    /// The entries to inject: not deleted, newest first (ties by id, newest first), at most <paramref name="limit"/>
    /// (clamped to 1–50), then the oldest dropped until the lines (joined by newlines) fit in 4,000 characters.
    /// </summary>
    public static FeedbackSelection Select(IEnumerable<FeedbackEntry> entries, int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var take = Math.Clamp(limit, MinLimit, MaxLimit);
        var chosen = entries
            .Where(e => !e.Deleted)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .Select(e => (e.Id, Line: Format(e)))
            .ToList();

        // Oldest dropped first: keep the newest entries whose lines (plus separators) fit the cap.
        var total = 0;
        var kept = 0;
        foreach (var (_, line) in chosen)
        {
            var cost = line.Length + (kept == 0 ? 0 : 1);
            if (total + cost > MaxTotalCharacters)
            {
                break;
            }

            total += cost;
            kept++;
        }

        var used = chosen.Take(kept).ToList();
        var builder = new StringBuilder(total);
        foreach (var (_, line) in used)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return new FeedbackSelection([.. used.Select(u => u.Id)], builder.ToString());
    }

    /// <summary>
    /// FR-351: the feedback texts a <c>writes_feedback</c> stage's output holds in the named field:
    /// a string, or an object's <c>text</c> string; blank items are skipped and each text is cut to 2,000 characters.
    /// </summary>
    public static IReadOnlyList<string> Texts(IEnumerable<string?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Select(t => t.Length > MaxTextLength ? t[..MaxTextLength] : t)];
    }
}
