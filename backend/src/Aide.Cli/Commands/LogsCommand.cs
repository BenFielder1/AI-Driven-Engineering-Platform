using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>`aide logs &lt;id&gt;` — the project activity feed (FR-50), oldest first; `--follow` polls for new events.</summary>
internal static class LogsCommand
{
    private static readonly string[] Categories = ["transition", "gate", "artifact", "run", "step", "review", "release", "rollback", "project", "integrity"];

    public static Command Build(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var type = new Option<string?>("--type", "-t") { Description = "Only this category: " + string.Join(", ", Categories) + "." };
        type.AcceptOnlyFromAmong(Categories);
        var limit = new Option<int>("--limit", "-l")
        {
            Description = "Events to fetch (1-100).",
            DefaultValueFactory = _ => 50,
            CustomParser = Parsers.IntInRange("--limit", 1, 100),
        };
        var before = new Option<string?>("--before") { Description = "Page cursor (nextBefore from a previous --json page)." };
        var follow = new Option<bool>("--follow", "-f") { Description = "Keep polling and print new events until Ctrl+C (with --json: one JSON object per line)." };
        var interval = new Option<int>("--interval")
        {
            Description = "Seconds between polls with --follow (1-3600).",
            DefaultValueFactory = _ => 5,
            CustomParser = Parsers.IntInRange("--interval", 1, 3600),
        };

        var command = new Command("logs", "Show a project's activity feed: transitions, gate decisions, artifact changes, agent runs, step results.")
        {
            id, type, limit, before, follow, interval,
        };
        command.Validators.Add(r =>
        {
            if (r.GetValue(follow) && r.GetValue(before) is not null)
            {
                r.AddError("--before cannot be used with --follow.");
            }
        });

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            string PagePath(string? cursor) => AideApiClient.WithQuery(
                $"api/projects/{context.Get(id)}/activity", ("type", context.Get(type)), ("before", cursor), ("limit", context.Get(limit)));

            var first = await context.Api().GetAsync<ActivityPageDto>(PagePath(context.Get(before)), cancellationToken).ConfigureAwait(false);
            if (!context.Get(follow))
            {
                if (context.Json)
                {
                    await context.WriteRawJsonAsync(first.Raw).ConfigureAwait(false);
                    return ExitCodes.Success;
                }

                if (first.Value.Items.Count == 0)
                {
                    await context.Out.WriteLineAsync("No activity.").ConfigureAwait(false);
                }

                await PrintAsync(context, first.Value.Items, JsonItems(first.Raw)).ConfigureAwait(false);
                if (first.Value.NextBefore is { } next)
                {
                    await context.Error.WriteLineAsync($"(older events: --before {next})").ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }

            var follower = new Follower();
            var firstItems = first.Value.Items;
            await PrintAsync(context, firstItems, JsonItems(first.Raw)).ConfigureAwait(false);
            follower.MarkPrinted(firstItems);
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(context.Get(interval)), context.Host.TimeProvider, cancellationToken).ConfigureAwait(false);
                    await PollAsync(context, follower, PagePath, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ExitCodes.Success;
            }
        });
        return command;
    }

    /// <summary>Pages a poll may walk back before giving up (100 x limit events per interval).</summary>
    internal const int MaxPagesPerPoll = 100;

    /// <summary>
    /// Fetches everything newer than what was printed: the newest page, then older pages via <c>before</c> until an
    /// already-printed event (or one older than the newest printed) is reached, so a burst larger than --limit is not lost.
    /// </summary>
    private static async Task PollAsync(CommandContext context, Follower follower, Func<string?, string> pagePath, CancellationToken cancellationToken)
    {
        var items = new List<ActivityItemDto>();
        var raw = new List<JsonNode?>();
        string? cursor = null;
        for (var pageNumber = 0; ; pageNumber++)
        {
            if (pageNumber == MaxPagesPerPoll)
            {
                await context.Error.WriteLineAsync($"warning: more than {MaxPagesPerPoll} pages of new events in one interval; older ones were skipped.").ConfigureAwait(false);
                break;
            }

            var page = await context.Api().GetAsync<ActivityPageDto>(pagePath(cursor), cancellationToken).ConfigureAwait(false);
            var pageRaw = JsonItems(page.Raw);
            var reachedPrinted = false;
            for (var i = 0; i < page.Value.Items.Count; i++)
            {
                var item = page.Value.Items[i];
                if (follower.AlreadyPrinted(item))
                {
                    reachedPrinted = true;
                    break;
                }

                items.Add(item);
                raw.Add(i < pageRaw.Count ? pageRaw[i] : null);
            }

            if (reachedPrinted || page.Value.NextBefore is null || page.Value.Items.Count == 0)
            {
                break;
            }

            cursor = page.Value.NextBefore;
        }

        await PrintAsync(context, items, raw).ConfigureAwait(false);
        follower.MarkPrinted(items);
    }

    /// <summary>What --follow has printed: ids, and the newest (occurredAt) printed, since the feed is ordered by (occurredAt, id).</summary>
    private sealed class Follower
    {
        private readonly HashSet<Guid> _printed = [];
        private DateTimeOffset? _newest;

        public bool AlreadyPrinted(ActivityItemDto item) => _printed.Contains(item.Id) || (_newest is { } newest && item.OccurredAt < newest);

        public void MarkPrinted(IEnumerable<ActivityItemDto> items)
        {
            foreach (var item in items)
            {
                _printed.Add(item.Id);
                if (_newest is null || item.OccurredAt > _newest)
                {
                    _newest = item.OccurredAt;
                }
            }
        }
    }

    /// <summary>Prints newest-first API items oldest first (like a log). With --json, one compact object per line.</summary>
    private static async Task PrintAsync(CommandContext context, IReadOnlyList<ActivityItemDto> items, List<JsonNode?> raw)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var line = context.Json
                ? ApiJson.ToCompactJson(raw[i])
                : $"{Output.Time(item.OccurredAt)}  {item.Type,-28} {Output.Actor(item.Actor),-16} {item.Summary}";
            await context.Out.WriteLineAsync(line).ConfigureAwait(false);
        }

        await context.Out.FlushAsync().ConfigureAwait(false);
    }

    private static List<JsonNode?> JsonItems(string raw)
    {
        try
        {
            return JsonNode.Parse(raw)?["items"]?.AsArray().ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
